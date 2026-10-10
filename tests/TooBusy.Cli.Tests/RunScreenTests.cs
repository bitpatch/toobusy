using TooBusy.Cli.Terminal;
using TooBusy.Core.Queue;
using TooBusy.Core.Run;

namespace TooBusy.Cli.Tests;

public sealed class RunScreenTests : IDisposable
{
    static readonly QueueTask Export = new(3, "Export the data", "https://example.com/3", ["feature"], BoardStatus.InProgress, "In Progress", [], []);

    static readonly RunStatus Working = new(RunPhase.Working, "")
    {
        Task = Export,
        Since = InstantClock.Start - TimeSpan.FromSeconds(84),
        Step = "Edit src/Export.cs",
        Context = 31_400,
        Queued = 2,
        Open = "claude attach 1a2b",
        Available = RunCommands.Stop | RunCommands.Abort,
    };

    readonly TestTerminal terminal = new(width: 100, height: 24);
    DateTimeOffset now = InstantClock.Start;

    // The page looks for a key while the run goes on, so the terminal says whether one is pressed.
    public RunScreenTests() => terminal.Device = terminal.Device with { KeyWaiting = () => terminal.Keys.Waiting };

    public void Dispose() => terminal.Dispose();

    [Fact]
    public async Task ThePageShowsTheLogAndWhatTheRunIsDoingAndSendsItsCommands()
    {
        var run = new ScriptedRun(async (run, view) =>
        {
            view.Say(new RunLine(RunMark.Head, "v0.2.0 · 2 tasks in the queue"));
            view.Say(new RunLine(RunMark.Started, "#3 Export the data — started"));
            view.Show(Working with { Limits = new UsageLimits(new UsageWindow("5-hour", 41.7, null), new UsageWindow("weekly", 12, null)) });
            Assert.Equal(RunCommand.Stop, await run.NextAsync());
            view.Say(new RunLine(RunMark.Paused, "Will stop after #3"));
            return new RunResult(RunEnd.Stopped, 1);
        });
        terminal.Keys.Hold(() => terminal.Saw("1:24")).Type("/stop").Press(Keys.Enter).Hold(() => terminal.Saw("The run is over")).Type("/exit").Press(Keys.Enter);

        using var page = terminal.Open();
        page.Status = "Running the queue";
        var screen = new RunScreen(page, () => now);
        var after = await screen.RunAsync(run, TestContext.Current.CancellationToken);

        Assert.Equal(AfterRun.Exit, after);
        Assert.Equal(new RunResult(RunEnd.Stopped, 1), screen.Result);
        terminal.AssertSaw("Running the queue · 5-hour 41% · weekly 12%");
        terminal.AssertSaw("""
             ✻ v0.2.0 · 2 tasks in the queue
             → #3 Export the data — started
             #3 Export the data · 1:24 · Edit src/Export.cs · 31k context · 2 queued
             •••••••
               /stop   finish the current task, then stop
               /abort  stop as soon as possible: nothing committed, a report in the task
             ctrl+c stop the session and exit
            """.ReplaceLineEndings("\n").TrimEnd('\n'));
        terminal.AssertSaw("""
               ❯ /stop
             ‖ Will stop after #3
             The run is over · 1 done
               /menu  go back to the menu
               /exit  leave toobusy
             ctrl+c exit
            """.ReplaceLineEndings("\n").TrimEnd('\n'));
        Assert.Equal(
            ["✻ v0.2.0 · 2 tasks in the queue", "→ #3 Export the data — started", "  ❯ /stop", "‖ Will stop after #3"],
            screen.Log.Select(line => line.ToString()));
        Assert.Equal("Running the queue", page.Status);
    }

    [Fact]
    public async Task ASlashListsTheCommandsThatMeanSomethingNowAndTabCompletesTheOneThatFits()
    {
        RunCommand? sent = null;
        var run = new ScriptedRun(async (run, view) =>
        {
            view.Show(Working with
            {
                Phase = RunPhase.WaitingForOwner,
                Since = InstantClock.Start - TimeSpan.FromSeconds(10),
                Until = InstantClock.Start + TimeSpan.FromSeconds(7),
                Available = RunCommands.Stop | RunCommands.Abort | RunCommands.Nudge | RunCommands.Hold,
            });
            sent = await run.NextAsync();
            return new RunResult(RunEnd.Emptied, 0);
        });
        terminal.Keys
            .Hold(() => terminal.Saw("waits for the owner")).Type("/")
            .Hold(() => terminal.Saw("❯ /stop")).Type("h")
            .Hold(() => terminal.Saw("❯ /hold")).Press(Keys.Tab)
            .Hold(() => terminal.Saw(" /hold\n")).Press(Keys.Enter)
            .Hold(() => terminal.Saw("The run is over")).Type("/").Press(Keys.Enter);

        using var page = terminal.Open();
        var after = await new RunScreen(page, () => now).RunAsync(run, TestContext.Current.CancellationToken);

        Assert.Equal(AfterRun.Menu, after);
        Assert.Equal(RunCommand.Hold, sent);
        terminal.AssertSaw(" #3 Export the data · 0:10 · waits for the owner · is told to go on alone in 0:07 · 2 queued");
        terminal.AssertSaw("""
             /
             ❯ /stop   finish the current task, then stop
               /abort  stop as soon as possible: nothing committed, a report in the task
               /nudge  tell the session to go on alone, now
               /hold   tell the session nothing: it waits for you
             tab complete · enter run · ctrl+c stop the session and exit
            """.ReplaceLineEndings("\n").TrimEnd('\n'));
        terminal.AssertSaw(" /h\n ❯ /hold  tell the session nothing: it waits for you");
        terminal.AssertSaw(" /hold\n ❯ /hold  tell the session nothing: it waits for you");
    }

    [Fact]
    public async Task WhatFitsNoCommandSaysSoAndATextThatIsNotACommandIsNotRun()
    {
        var run = new ScriptedRun(async (run, view) =>
        {
            view.Show(Working);
            await run.NextAsync();
            return new RunResult(RunEnd.Stopped, 0);
        });
        terminal.Keys
            .Hold(() => terminal.Saw("1:24")).Type("/exit")
            .Hold(() => terminal.Saw("No command fits.")).Press(Keys.Enter, Keys.Escape).Type("stop")
            .Hold(() => terminal.Saw("Commands start with /.")).Press(Keys.Enter, Keys.Escape).Type("/ab").Press(Keys.Enter)
            .Hold(() => terminal.Saw("The run is over")).Type("/exit").Press(Keys.Enter);

        using var page = terminal.Open();
        var screen = new RunScreen(page, () => now);
        await screen.RunAsync(run, TestContext.Current.CancellationToken);

        terminal.AssertSaw(" /exit\n No command fits.");
        terminal.AssertSaw(" stop\n Commands start with /.");
        Assert.Equal(["  ❯ /abort"], screen.Log.Select(line => line.ToString()));
    }

    [Fact]
    public async Task CtrlCTwiceKillsTheRunAndLeavesThePage()
    {
        var run = new ScriptedRun(async (run, view) =>
        {
            view.Show(Working);
            return await run.ForEverAsync();
        });
        terminal.Keys
            .Hold(() => terminal.Saw("1:24")).Press(Keys.ControlC)
            .Hold(() => terminal.Saw("press ctrl+c again to stop the session and exit")).Press(Keys.ControlC);

        using var page = terminal.Open();
        var screen = new RunScreen(page, () => now);
        await Assert.ThrowsAsync<OperationCanceledException>(() => screen.RunAsync(run, TestContext.Current.CancellationToken));

        Assert.True(run.Killed);
        Assert.Equal(new RunResult(RunEnd.Killed, 0), screen.Result);
        Assert.Equal(["■ Killed the session of #3"], screen.Log.Select(line => line.ToString()));
    }

    [Fact]
    public async Task WhenTheRunIsOverCtrlCOnlyLeaves()
    {
        var run = new ScriptedRun((_, _) => Task.FromResult(new RunResult(RunEnd.Emptied, 2)));
        terminal.Keys
            .Hold(() => terminal.Saw("The run is over")).Press(Keys.ControlC)
            .Hold(() => terminal.Saw("press ctrl+c again to exit")).Press(Keys.ControlC);

        using var page = terminal.Open();
        var screen = new RunScreen(page, () => now);
        await Assert.ThrowsAsync<OperationCanceledException>(() => screen.RunAsync(run, TestContext.Current.CancellationToken));

        Assert.False(run.Killed);
        Assert.Equal(new RunResult(RunEnd.Emptied, 2), screen.Result);
        terminal.AssertSaw(" The run is over · 2 done");
    }

    [Fact]
    public async Task TheTimesOfThePageGoOnWhileNothingElseChanges()
    {
        var run = new ScriptedRun(async (run, view) =>
        {
            view.Show(Working);
            await run.NextAsync();
            return new RunResult(RunEnd.Stopped, 0);
        });
        terminal.Keys
            .Hold(() => terminal.Saw("1:24") && Later(TimeSpan.FromSeconds(41)) && terminal.Saw("2:05")).Type("/stop").Press(Keys.Enter)
            .Hold(() => terminal.Saw("The run is over")).Type("/exit").Press(Keys.Enter);

        using var page = terminal.Open();
        await new RunScreen(page, () => now).RunAsync(run, TestContext.Current.CancellationToken);

        terminal.AssertSaw(" #3 Export the data · 2:05 · Edit src/Export.cs");
    }

    [Theory]
    [InlineData(RunPhase.Preparing, false, "Reading the queue…", 0, false, false, " Reading the queue…\n •••••••")]
    [InlineData(RunPhase.Preparing, true, "Starting #3…", 0, false, false, " #3 Export the data · 1:24 · starting · 2 queued")]
    [InlineData(RunPhase.Working, true, "", 0, true, false, " #3 Export the data · 1:24 · Edit src/Export.cs · 31k context · the last task of this run")]
    [InlineData(RunPhase.Working, true, "", 0, false, true, " #3 Export the data · 1:24 · Edit src/Export.cs · 31k context · wrapping up")]
    [InlineData(RunPhase.WaitingForOwner, true, "", 0, false, false, " #3 Export the data · 1:24 · waits for the owner · gets no message · 2 queued")]
    [InlineData(RunPhase.WaitingForLimit, true, "", 3725, false, false, " #3 Export the data · 1:24 · waits for a usage limit · goes on in 1:02:05 · 2 queued")]
    [InlineData(RunPhase.WaitingForLimit, false, "Waiting for the 5-hour limit to reset", 125, false, false, " Waiting for the 5-hour limit to reset · 2:05")]
    public async Task WhatTheRunIsDoingIsSaidInOneLine(RunPhase phase, bool task, string text, int until, bool stopping, bool aborting, string expected)
    {
        var status = Working with
        {
            Phase = phase,
            Text = text,
            Task = task ? Export : null,
            Until = until > 0 ? InstantClock.Start + TimeSpan.FromSeconds(until) : null,
            Stopping = stopping,
            Aborting = aborting,
        };
        var run = new ScriptedRun(async (run, view) =>
        {
            view.Show(status);
            await run.NextAsync();
            return new RunResult(RunEnd.Stopped, 0);
        });
        terminal.Keys
            .Hold(() => terminal.Saw("/abort")).Type("/abort").Press(Keys.Enter)
            .Hold(() => terminal.Saw("The run is over")).Type("/exit").Press(Keys.Enter);

        using var page = terminal.Open();
        await new RunScreen(page, () => now).RunAsync(run, TestContext.Current.CancellationToken);

        terminal.AssertSaw(expected);
    }

    [Fact]
    public async Task ALineOfTheLogThatDoesNotFitGoesOnUnderItsText()
    {
        using var narrow = new TestTerminal(width: 44, height: 24);
        narrow.Device = narrow.Device with { KeyWaiting = () => narrow.Keys.Waiting };
        var run = new ScriptedRun((_, view) =>
        {
            view.Say(new RunLine(RunMark.Attention, "#3 waits for the owner · it is told to go on alone in 90 s"));
            view.Say(new RunLine(RunMark.Note, "│ CSV or JSON?"));
            return Task.FromResult(new RunResult(RunEnd.Emptied, 0));
        });
        narrow.Keys.Hold(() => narrow.Saw("The run is over")).Type("/exit").Press(Keys.Enter);

        using var page = narrow.Open();
        await new RunScreen(page, () => now).RunAsync(run, TestContext.Current.CancellationToken);

        narrow.AssertSaw(" ▲ #3 waits for the owner · it is told to\n   go on alone in 90 s\n   │ CSV or JSON?");
    }

    // Moves the time of the page on, once, and says that it did.
    bool Later(TimeSpan time)
    {
        if (now == InstantClock.Start)
            now += time;
        return true;
    }
}
