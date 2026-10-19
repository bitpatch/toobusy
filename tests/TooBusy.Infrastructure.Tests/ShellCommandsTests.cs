using TooBusy.Core.Run;
using TooBusy.Infrastructure.Processes;

namespace TooBusy.Infrastructure.Tests;

public sealed class ShellCommandsTests : IDisposable
{
    static readonly TimeSpan Long = TimeSpan.FromSeconds(30);
    static readonly Dictionary<string, string> Nothing = [];

    readonly DirectoryInfo folder = Directory.CreateTempSubdirectory("toobusy-shell-");
    readonly ShellCommands shell;

    public ShellCommandsTests() => shell = new ShellCommands(folder.FullName);

    public void Dispose() => folder.Delete(recursive: true);

    [Fact]
    public async Task ACommandIsALineForTheShellAndRunsInTheFolder()
    {
        File.WriteAllText(Path.Combine(folder.FullName, "here.txt"), "");

        var end = await shell.RunAsync("ls | tr a-z A-Z && echo \"it's done\"", Nothing, Long, TestContext.Current.CancellationToken);

        Assert.Equal(new CommandEnd(CommandStatus.Exited, 0, "HERE.TXT\nit's done\n"), end);
    }

    [Fact]
    public async Task WhatACommandPrintsIsReadAsUtf8()
    {
        var end = await shell.RunAsync("printf 'port 3000 — “занят” ✖'", Nothing, Long, TestContext.Current.CancellationToken);

        Assert.Equal(new CommandEnd(CommandStatus.Exited, 0, "port 3000 — “занят” ✖"), end);
    }

    [Fact]
    public async Task ACommandThatFailsGivesItsExitCodeAndWhatItPrintedToBothStreams()
    {
        var end = await shell.RunAsync("echo out; echo err >&2; exit 3", Nothing, Long, TestContext.Current.CancellationToken);

        Assert.Equal((CommandStatus.Exited, 3), (end.Status, end.ExitCode));
        Assert.Equal(["err", "out"], end.Printed.Split('\n', StringSplitOptions.RemoveEmptyEntries).Order());
    }

    [Fact]
    public async Task ACommandIsToldInItsEnvironmentBesideWhatToobusyHas()
    {
        var told = new Dictionary<string, string> { ["TOOBUSY_TASK"] = "12", ["TOOBUSY_TASK_TITLE"] = "Export \"the\" data" };

        var end = await shell.RunAsync("echo \"$TOOBUSY_TASK: $TOOBUSY_TASK_TITLE\"; test -n \"$PATH\"", told, Long, TestContext.Current.CancellationToken);

        Assert.Equal(new CommandEnd(CommandStatus.Exited, 0, "12: Export \"the\" data\n"), end);
    }

    [Fact]
    public async Task ACommandHasGitsPromptsOff()
    {
        var end = await shell.RunAsync("echo \"$GIT_TERMINAL_PROMPT $GCM_INTERACTIVE\"", Nothing, Long, TestContext.Current.CancellationToken);

        Assert.Equal(new CommandEnd(CommandStatus.Exited, 0, "0 never\n"), end);
    }

    [Fact]
    public async Task ACommandThatDoesNotEndInItsTimeIsStoppedAndWhatItPrintedIsKept()
    {
        var end = await shell.RunAsync("echo started; sleep 30", Nothing, TimeSpan.FromSeconds(1), TestContext.Current.CancellationToken);

        Assert.Equal(new CommandEnd(CommandStatus.TimedOut, 0, "started\n"), end);
    }

    [Fact]
    public async Task ACancellationStopsTheCommandAndThrows()
    {
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        var running = shell.RunAsync("sleep 30", Nothing, Long, cancellation.Token);

        cancellation.CancelAfter(TimeSpan.FromMilliseconds(200));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => running);
    }

    [Fact]
    public async Task WhatACommandLeavesRunningDoesNotKeepItFromEnding()
    {
        var watch = System.Diagnostics.Stopwatch.StartNew();

        var end = await shell.RunAsync("sleep 20 & echo started", Nothing, Long, TestContext.Current.CancellationToken);

        Assert.Equal(new CommandEnd(CommandStatus.Exited, 0, "started\n"), end);
        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(10), "The command was waited for as long as what it left running.");
    }
}
