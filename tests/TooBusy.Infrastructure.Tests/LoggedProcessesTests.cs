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
    public async Task ACommandThatPassedIsWrittenWithItsFirstLinesAndHowMuchThereWas()
    {
        var eight = string.Join('\n', Enumerable.Range(1, 8).Select(line => $"line {line}")) + "\n";
        var processes = new LoggedProcesses(new FakeRunner((_, _) => Took(2, Exited(0, eight))), log);

        var result = await processes.RunAsync("gh", ["issue", "list", "--repo", "acme/rocket", "--json", "number,title"], Patience, TestContext.Current.CancellationToken);
        log.Dispose();

        Assert.Equal(0, result.ExitCode);
        Assert.Equal(
            ["$ gh issue list --repo acme/rocket --json number,title", "→ gh exited 0 in 2 s", "line 1", "line 2", "line 3", "line 4", "line 5", "… 8 lines, 55 B in all"],
            Logged.Lines(log));
    }

    [Fact]
    public async Task ACommandThatFailedHasItsWholeAnswerInTheLog()
    {
        var processes = new LoggedProcesses(new FakeRunner((_, _) => new ProcessResult(ProcessStatus.Exited, 1, "one\ntwo\n", "error: it broke\n")), log);

        await processes.RunAsync("claude", ["--bg", "--resume", "1a2b"], Patience, TestContext.Current.CancellationToken);
        log.Dispose();

        Assert.Equal(["$ claude --bg --resume 1a2b", "→ claude exited 1 in 0 s", "one", "two", "error: it broke"], Logged.Lines(log));
    }

    [Fact]
    public async Task ACommandThatIsNotFoundOrDidNotEndInItsTimeIsSaidSo()
    {
        var processes = new LoggedProcesses(new FakeRunner((command, _) => command == "gh" ? new ProcessResult(ProcessStatus.NotFound, 0, "", "") : Took(120, new ProcessResult(ProcessStatus.TimedOut, 0, "", ""))), log);

        await processes.RunAsync("gh", ["auth", "status"], Patience, TestContext.Current.CancellationToken);
        await processes.RunAsync("claude", ["agents", "--json", "--all"], Patience, TestContext.Current.CancellationToken);
        log.Dispose();

        Assert.Equal(["$ gh auth status", "→ gh is not found", "$ claude agents --json --all", "→ claude did not end in 2 min, and was stopped"], Logged.Lines(log));
    }

    [Fact]
    public async Task ACommandThatIsCutIsWrittenSoAndTheCutIsThrownOn()
    {
        var processes = new LoggedProcesses(new FakeRunner((_, _) =>
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
        var processes = new LoggedProcesses(new FakeRunner((_, _) => Exited(0, "")), log);

        await processes.RunAsync("claude", ["--bg", "-n", "#12 Export the data", message], Patience, TestContext.Current.CancellationToken);
        log.Dispose();

        Assert.Equal(
            [$"$ claude --bg -n \"#12 Export the data\" \"{message[..120]}… (283 B)\"", "→ claude exited 0 in 0 s"],
            Logged.Lines(log));
    }

    [Fact]
    public async Task ARunnerInsideAFolderIsLoggedToo()
    {
        var inner = new FakeRunner((_, _) => Exited(0, "ok\n"));
        var processes = new LoggedProcesses(inner, log).Inside("/work/rocket");

        await processes.RunAsync("git", ["status"], Patience, TestContext.Current.CancellationToken);
        log.Dispose();

        Assert.Equal([("/work/rocket", "git")], inner.Asked);
        Assert.Equal(["$ git status", "→ git exited 0 in 0 s", "ok"], Logged.Lines(log));
    }

    ProcessResult Took(int seconds, ProcessResult result)
    {
        clock.Now += TimeSpan.FromSeconds(seconds);
        return result;
    }

    static ProcessResult Exited(int code, string output) => new(ProcessStatus.Exited, code, output, "");
}
