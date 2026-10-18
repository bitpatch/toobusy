using TooBusy.Core.Queue;
using TooBusy.Core.Run;
using TooBusy.Infrastructure.Run;

namespace TooBusy.Infrastructure.Tests;

public sealed class LoggedRunTests : IDisposable
{
    static readonly QueueTask Task12 = new(12, "Export the data", "https://github.com/acme/rocket/issues/12", ["feature"], BoardStatus.Todo, "Todo", [], []);

    readonly DirectoryInfo folder = Directory.CreateTempSubdirectory("toobusy-log-");
    readonly FixedClock clock = new(FixedClock.Start);
    readonly FakeView view = new();

    public void Dispose() => folder.Delete(recursive: true);

    [Fact]
    public async Task WhatTheRunSaysGoesToTheViewAndIntoTheLogWithWhatAScreenDoesNotShow()
    {
        var log = new RunLogFile(folder.FullName, clock);
        var run = new LoggedRun(new FakeRun(view =>
        {
            view.Say(new RunLine(RunMark.Head, "v1.0.0 · 3 tasks in the queue"));
            view.Show(new RunStatus(RunPhase.Preparing, "Reading the queue…"));
            view.Report(new TaskEnd(Task12, RunMark.Done, TimeSpan.FromSeconds(95)));
            view.Say(new RunLine(RunMark.Failed, "Stopped: an error toobusy did not foresee: InvalidOperationException: boom") { Detail = "System.InvalidOperationException: boom\n   at Somewhere()" });
            return Task.FromResult(new RunResult(RunEnd.Problem, 1, "an error toobusy did not foresee: InvalidOperationException: boom"));
        }), log);

        var result = await run.RunAsync(view, TestContext.Current.CancellationToken);

        Assert.Equal(RunEnd.Problem, result.End);
        Assert.Equal(
            ["✻ v1.0.0 · 3 tasks in the queue", "✖ Stopped: an error toobusy did not foresee: InvalidOperationException: boom", "  the log of the run: " + log.DisplayPath],
            view.Lines.Select(line => line.ToString()));
        Assert.Single(view.Ends);
        Assert.Single(view.Statuses);
        Assert.Equal(
            [
                "✻ v1.0.0 · 3 tasks in the queue",
                "✔ #12 is over: done in 95 s",
                "✖ Stopped: an error toobusy did not foresee: InvalidOperationException: boom",
                "System.InvalidOperationException: boom",
                "   at Somewhere()",
                "  the log of the run: " + log.DisplayPath,
                "run ended: problem · an error toobusy did not foresee: InvalidOperationException: boom · 1 done",
            ],
            Logged.Lines(log));
    }

    [Fact]
    public async Task TheLogIsNamedUnderEveryRunAndIsClosedWhenTheRunEnds()
    {
        var log = new RunLogFile(folder.FullName, clock);
        var run = new LoggedRun(new FakeRun(_ => Task.FromResult(new RunResult(RunEnd.Emptied, 2))), log);

        await run.RunAsync(view, TestContext.Current.CancellationToken);
        log.Write("after the run");

        Assert.Equal(["  the log of the run: " + log.DisplayPath], view.Lines.Select(line => line.ToString()));
        Assert.Equal(["  the log of the run: " + log.DisplayPath, "run ended: emptied · 2 done"], Logged.Lines(log));
    }

    // How the run sees its session goes into the log and not to the screen.
    [Fact]
    public async Task ATraceGoesIntoTheLogOnly()
    {
        var log = new RunLogFile(folder.FullName, clock);
        var run = new LoggedRun(new FakeRun(view =>
        {
            view.Trace("#12 session 1a2b3c4d: working → asking (input needed)");
            return Task.FromResult(new RunResult(RunEnd.Stopped, 0));
        }), log);

        await run.RunAsync(view, TestContext.Current.CancellationToken);

        Assert.DoesNotContain(view.Lines, line => line.Text.Contains("session", StringComparison.Ordinal));
        Assert.Equal("#12 session 1a2b3c4d: working → asking (input needed)", Logged.Lines(log)[0]);
    }

    [Fact]
    public async Task ALogThatCannotBeWrittenIsSaidOnceAndNotNamed()
    {
        File.WriteAllText(Path.Combine(folder.FullName, "logs"), "a file where the folder of the logs should be");
        var log = new RunLogFile(folder.FullName, clock);
        var run = new LoggedRun(new FakeRun(view =>
        {
            view.Say(new RunLine(RunMark.Head, "v1.0.0"));
            view.Say(new RunLine(RunMark.Failed, "Stopped: it broke"));
            return Task.FromResult(new RunResult(RunEnd.Problem, 0, "it broke"));
        }), log);

        await run.RunAsync(view, TestContext.Current.CancellationToken);

        Assert.Equal(["✻ v1.0.0", $"▲ the log of the run cannot be written: {log.Unwritable} · the run goes on without it", "✖ Stopped: it broke"], view.Lines.Select(line => line.ToString()));
    }

    [Fact]
    public async Task AnErrorThatLeavesTheRunIsWrittenAndThrownOn()
    {
        var log = new RunLogFile(folder.FullName, clock);
        var run = new LoggedRun(new FakeRun(_ => throw new InvalidOperationException("boom")), log);

        await Assert.ThrowsAsync<InvalidOperationException>(() => run.RunAsync(view, TestContext.Current.CancellationToken));
        log.Write("after the run");

        var lines = Logged.Lines(log);
        Assert.Equal("run ended with an error:", lines[0]);
        Assert.StartsWith("System.InvalidOperationException: boom", lines[1], StringComparison.Ordinal);
        Assert.DoesNotContain("after the run", lines);
    }

    [Fact]
    public async Task ACommandIsPassedOnToTheRun()
    {
        var inner = new FakeRun(_ => Task.FromResult(new RunResult(RunEnd.Stopped, 0)));
        var run = new LoggedRun(inner, new RunLogFile(folder.FullName, clock));

        run.Send(RunCommand.Stop);
        await run.RunAsync(view, TestContext.Current.CancellationToken);

        Assert.Equal([RunCommand.Stop], inner.Sent);
    }
}
