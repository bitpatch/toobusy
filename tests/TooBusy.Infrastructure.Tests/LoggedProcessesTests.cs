using TooBusy.Core.Processes;
using TooBusy.Infrastructure.Processes;
using TooBusy.Infrastructure.Run;

namespace TooBusy.Infrastructure.Tests;

public sealed class LoggedProcessesTests : IDisposable
{
    static readonly TimeSpan Patience = TimeSpan.FromMinutes(2);

    readonly DirectoryInfo folder = Directory.CreateTempSubdirectory("toobusy-log-");
    readonly FixedClock clock = new(FixedClock.Start);
    readonly RunLogFile log;

    public LoggedProcessesTests() => log = new RunLogFile(folder.FullName, clock);

    public void Dispose()
    {
        log.Dispose();
        folder.Delete(recursive: true);
    }

    [Fact]
    public async Task ACommandThatPassedWithAShortAnswerIsWrittenWithIt()
    {
        var processes = new LoggedProcesses(new ScriptedProcesses((_, _) => Took(2, Exited(0, "note: started a copy as 953872ed.\nbackgrounded · 953872ed\n"))), log);

        var result = await processes.RunAsync("claude", ["--bg", "--resume", "1a2b"], Patience, TestContext.Current.CancellationToken);
        log.Dispose();

        Assert.Equal(0, result.ExitCode);
        Assert.Equal(["$ claude --bg --resume 1a2b", "→ claude exited 0 in 2 s", "note: started a copy as 953872ed.", "backgrounded · 953872ed"], Logged.Lines(log));
    }

    // A long answer of a command that passed, or one in JSON, as the list of the sessions, says nothing in the log.
    [Fact]
    public async Task ACommandThatPassedWithALongAnswerOrAnAnswerInJsonIsWrittenWithoutIt()
    {
        var nine = string.Concat(Enumerable.Range(1, 9).Select(line => $"line {line}\n"));
        var processes = new LoggedProcesses(new ScriptedProcesses((command, _) => Exited(0, command == "gh" ? nine : """[{"id":"1a2b3c4d","state":"done"}]""")), log);

        await processes.RunAsync("gh", ["issue", "list"], Patience, TestContext.Current.CancellationToken);
        await processes.RunAsync("claude", ["agents", "--json", "--all"], Patience, TestContext.Current.CancellationToken);
        log.Dispose();

        // An answer in JSON is not written however short it is.
        Assert.Equal(["$ gh issue list", "→ gh exited 0 in 0 ms", "$ claude agents --json --all", "→ claude exited 0 in 0 ms"], Logged.Lines(log));
    }

    [Fact]
    public async Task ACommandThatFailedHasItsWholeAnswerInTheLog()
    {
        var long_ = string.Concat(Enumerable.Range(1, 20).Select(line => $"line {line}\n"));
        var processes = new LoggedProcesses(new ScriptedProcesses((_, _) => new ProcessResult(ProcessStatus.Exited, 1, long_, "error: it broke\n")), log);

        await processes.RunAsync("claude", ["--bg", "--resume", "1a2b"], Patience, TestContext.Current.CancellationToken);
        log.Dispose();

        var lines = Logged.Lines(log);
        Assert.Equal(["$ claude --bg --resume 1a2b", "→ claude exited 1 in 0 ms"], lines.Take(2));
        Assert.Equal(23, lines.Count);
        Assert.Equal("error: it broke", lines[^1]);
    }

    [Fact]
    public async Task ACommandThatIsNotFoundCouldNotBeStartedOrDidNotEndInItsTimeIsSaidSo()
    {
        var processes = new LoggedProcesses(new ScriptedProcesses((command, _) => command switch
        {
            "gh" => new ProcessResult(ProcessStatus.NotFound, 0, "", ""),
            "git" => new ProcessResult(ProcessStatus.NotStarted, 0, "", "Permission denied"),
            _ => Took(120, new ProcessResult(ProcessStatus.TimedOut, 0, "", "")),
        }), log);

        await processes.RunAsync("gh", ["auth", "status"], Patience, TestContext.Current.CancellationToken);
        await processes.RunAsync("git", ["status"], Patience, TestContext.Current.CancellationToken);
        await processes.RunAsync("claude", ["agents", "--json", "--all"], Patience, TestContext.Current.CancellationToken);
        log.Dispose();

        Assert.Equal(
            [
                "$ gh auth status", "→ gh is not found",
                "$ git status", "→ git could not be started: Permission denied",
                "$ claude agents --json --all", "→ claude did not end in 2 min, and was stopped",
            ],
            Logged.Lines(log));
    }

    [Fact]
    public async Task ACommandThatIsCutIsWrittenSoAndTheCutIsThrownOn()
    {
        var processes = new LoggedProcesses(new ScriptedProcesses((_, _) =>
        {
            clock.Now += TimeSpan.FromMinutes(72);
            throw new OperationCanceledException();
        }), log);

        await Assert.ThrowsAsync<OperationCanceledException>(() => processes.RunAsync("caffeinate", ["-i", "-w", "123"], Timeout.InfiniteTimeSpan, TestContext.Current.CancellationToken));
        log.Dispose();

        Assert.Equal(["$ caffeinate -i -w 123", "→ caffeinate was cut after 1 h 12 min"], Logged.Lines(log));
    }

    [Fact]
    public async Task ALongArgumentIsCutToItsFirstLineAndOneWithASpaceIsQuoted()
    {
        var message = "toobusy runs again: go on with task #12. " + new string('x', 200) + "\nThe rules of this session hold as before.";
        var processes = new LoggedProcesses(new ScriptedProcesses((_, _) => Exited(0, "")), log);

        await processes.RunAsync("claude", ["--bg", "-n", "#12 Export the data", message], Patience, TestContext.Current.CancellationToken);
        log.Dispose();

        Assert.Equal(
            [$"$ claude --bg -n \"#12 Export the data\" \"{message[..120]}… (283 B)\"", "→ claude exited 0 in 0 ms"],
            Logged.Lines(log));
    }

    [Fact]
    public async Task ARunnerInsideAFolderIsLoggedToo()
    {
        var inner = new ScriptedProcesses((_, _) => Exited(0, "ok\n"));
        var processes = new LoggedProcesses(inner, log).Inside("/work/rocket");

        await processes.RunAsync("git", ["status"], Patience, TestContext.Current.CancellationToken);
        log.Dispose();

        Assert.Equal([("/work/rocket", "git")], inner.Asked.Select(asked => (asked.Folder, asked.Command)));
        Assert.Equal(["$ git status", "→ git exited 0 in 0 ms", "ok"], Logged.Lines(log));
    }

    // A command that is run again and again, as the list of the sessions is, is written once and counted, and the
    // count stands before whatever comes next.
    [Fact]
    public async Task TheSameCommandThatPassesAgainAndAgainIsCountedInOneLine()
    {
        var processes = new LoggedProcesses(new ScriptedProcesses((_, _) => Took(1, Exited(0, ""))), log);

        for (var look = 0; look < 4; look++)
        {
            await processes.RunAsync("claude", ["agents", "--json", "--all"], Patience, TestContext.Current.CancellationToken);
            clock.Now += TimeSpan.FromSeconds(19);
        }

        log.Write("✔ #12 Export the data — done in 2 min");
        await processes.RunAsync("claude", ["agents", "--json", "--all"], Patience, TestContext.Current.CancellationToken);
        await processes.RunAsync("claude", ["agents", "--json", "--all"], Patience, TestContext.Current.CancellationToken);
        log.Dispose();

        Assert.Equal(
            [
                "$ claude agents --json --all", "→ claude exited 0 in 1 s",
                "  the same 3 times more in 41 s, each exited 0",
                "✔ #12 Export the data — done in 2 min",
                "$ claude agents --json --all", "→ claude exited 0 in 1 s",
                "  the same 1 time more in 1 s, each exited 0",
            ],
            Logged.Lines(log));
    }

    // Runners made by Inside share what was run last, and a failure among the repeats is written in full.
    [Fact]
    public async Task AFailureAmongTheRepeatsIsWrittenInFullAndStartsTheCountAnew()
    {
        var answers = new Queue<ProcessResult>([Exited(0, ""), Exited(0, ""), new(ProcessStatus.Exited, 1, "", "error: list\n"), Exited(0, ""), Exited(0, "")]);
        var processes = new LoggedProcesses(new ScriptedProcesses((_, _) => answers.Dequeue()), log);
        var inside = processes.Inside("/work/rocket");

        await processes.RunAsync("claude", ["agents"], Patience, TestContext.Current.CancellationToken);
        await inside.RunAsync("claude", ["agents"], Patience, TestContext.Current.CancellationToken);
        await processes.RunAsync("claude", ["agents"], Patience, TestContext.Current.CancellationToken);
        await inside.RunAsync("claude", ["agents"], Patience, TestContext.Current.CancellationToken);
        await processes.RunAsync("claude", ["agents"], Patience, TestContext.Current.CancellationToken);
        log.Dispose();

        Assert.Equal(
            [
                "$ claude agents", "→ claude exited 0 in 0 ms",
                "  the same 1 time more in 0 ms, each exited 0",
                "$ claude agents", "→ claude exited 1 in 0 ms", "error: list",
                "$ claude agents", "→ claude exited 0 in 0 ms",
                "  the same 1 time more in 0 ms, each exited 0",
            ],
            Logged.Lines(log));
    }

    ProcessResult Took(int seconds, ProcessResult result)
    {
        clock.Now += TimeSpan.FromSeconds(seconds);
        return result;
    }

    static ProcessResult Exited(int code, string output) => new(ProcessStatus.Exited, code, output, "");
}
