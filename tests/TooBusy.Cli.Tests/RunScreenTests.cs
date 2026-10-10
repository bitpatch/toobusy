using TooBusy.Cli.Terminal;
using TooBusy.Core.Queue;
using TooBusy.Core.Run;

namespace TooBusy.Cli.Tests;

public sealed class RunScreenTests : IDisposable
{
    const RunCommands ForOwner = RunCommands.Stop | RunCommands.Abort | RunCommands.Nudge | RunCommands.Hold;

    static readonly QueueTask Total = Task(2, "Show the total");

    static readonly QueueTask Export = Task(3, "Export the data");

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

    static readonly RunStatus Waiting = Working with
    {
        Phase = RunPhase.WaitingForOwner,
        Since = InstantClock.Start - TimeSpan.FromSeconds(10),
        Until = InstantClock.Start + TimeSpan.FromSeconds(7),
        Reply = ["CSV or JSON?"],
        Available = ForOwner,
    };

    readonly TestTerminal terminal = Live(width: 100, height: 24);
    DateTimeOffset now = InstantClock.Start;

    public void Dispose() => terminal.Dispose();

    static QueueTask Task(int number, string title) => new(number, title, $"https://example.com/{number}", ["feature"], BoardStatus.InProgress, "In Progress", [], []);

    // A terminal that says whether a key is pressed: the page looks for one while the run goes on.
    static TestTerminal Live(int width, int height)
    {
        var terminal = new TestTerminal(width, height);
        terminal.Device = terminal.Device with { KeyWaiting = () => terminal.Keys.Waiting };
        return terminal;
    }

    // What the terminal's own screen has at the moment.
    bool Has(string text) => terminal.Tape.Text.Contains(text, StringComparison.Ordinal);

    [Fact]
    public async Task ATaskThatIsOverIsOneLineAndTheOneThatIsWorkedOnStandsUnderIt()
    {
        var run = new ScriptedRun(async (run, view) =>
        {
            view.Say(new RunLine(RunMark.Head, "v0.2.0 · 2 tasks in the queue"));
            view.Say(new RunLine(RunMark.Started, "#2 Show the total — started"));
            view.Say(new RunLine(RunMark.Done, "#2 Show the total — done in 84 s"));
            view.Report(new TaskEnd(Total, RunMark.Done, TimeSpan.FromSeconds(84)));
            view.Say(new RunLine(RunMark.Started, "#3 Export the data — started"));
            view.Show(Working with { Limits = new UsageLimits(new UsageWindow("5-hour", 41.7, null), new UsageWindow("weekly", 12, null)) });
            Assert.Equal(RunCommand.Stop, await run.NextAsync());
            return new RunResult(RunEnd.Stopped, 1);
        });
        terminal.Keys.Hold(() => Has("1:24 · Edit src/Export.cs")).Type("/").Hold(() => Has("❯ stop")).Press(Keys.Enter);

        using var page = terminal.Open();
        page.Status = "Running the queue";
        var screen = new RunScreen(page, () => now);
        var result = await screen.RunAsync(run, TestContext.Current.CancellationToken);

        Assert.Equal(new RunResult(RunEnd.Stopped, 1), result);
        Assert.Equal(result, screen.Result);
        Assert.Equal([new TaskEnd(Total, RunMark.Done, TimeSpan.FromSeconds(84))], screen.Ends);
        var bar = "  toobusy · ~/rocket" + new string(' ', 61) + "Running the queue";
        terminal.AssertTaped(bar + "\n ✔ Show the total  1:24\n ● Export the data\n   1:24 · Edit src/Export.cs\n ❯ press / to show the menu\n"
            + " ctrl+c stop the session and exit" + new string(' ', 43) + "5-hour 41% · weekly 12%");

        // Nothing is said of a task that starts, and the page is left with how the run went under its tasks.
        Assert.DoesNotContain("started", terminal.Output.ToString(), StringComparison.Ordinal);
        Assert.Equal(
            [bar, " ✔ Show the total  1:24", " 1 task in 0 s · 1 done", " ‖ Stopped, as you asked"],
            terminal.Tape.Text.Split('\n'));
        Assert.Equal("1 task in 0 s · 1 done · Stopped, as you asked", screen.Summary);
        Assert.True(terminal.Tape.Wraps);
    }

    [Fact]
    public async Task ASlashOpensTheMenuOfTheCommandsThatMeanSomethingNowAndNothingIsTyped()
    {
        RunCommand? sent = null;
        var run = new ScriptedRun(async (run, view) =>
        {
            view.Show(Working);
            sent = await run.NextAsync();
            return new RunResult(RunEnd.Stopped, 0);
        });

        // A letter means nothing; the menu is opened, looked at and closed; then it is opened again for `abort`.
        terminal.Keys
            .Hold(() => Has("press / to show the menu")).Type("#/")
            .Hold(() => Has("❯ stop")).Press(Keys.Down)
            .Hold(() => Has("❯ abort")).Press(Keys.Escape)
            .Hold(() => Has("press / to show the menu")).Type("/")
            .Hold(() => Has("❯ stop")).Press(Keys.Up, Keys.Enter);

        using var page = terminal.Open();
        await new RunScreen(page, () => now).RunAsync(run, TestContext.Current.CancellationToken);

        Assert.Equal(RunCommand.Abort, sent);
        Assert.False(terminal.Saw("#"));
        terminal.AssertTaped("""
              ● Export the data
                1:24 · Edit src/Export.cs
              ❯ stop   finish the current task, then stop
                abort  stop as soon as possible: nothing committed, a report in the task
              ↑↓ move · enter run · esc close
            """.ReplaceLineEndings("\n").TrimEnd('\n').Replace("\n ", "\n", StringComparison.Ordinal)[1..]);
        terminal.AssertTaped("   stop   finish the current task, then stop\n ❯ abort  stop as soon as possible");
    }

    [Fact]
    public async Task ASessionThatWaitsForTheOwnerShowsWhatItSaidAndBringsItsTwoCommands()
    {
        var sent = new List<RunCommand>();
        var run = new ScriptedRun(async (run, view) =>
        {
            view.Show(Waiting);
            sent.Add(await run.NextAsync());
            view.Show(Waiting with { Until = null, Available = RunCommands.Stop | RunCommands.Abort | RunCommands.Nudge });
            sent.Add(await run.NextAsync());
            view.Show(Working);
            sent.Add(await run.NextAsync());
            return new RunResult(RunEnd.Stopped, 0);
        });
        terminal.Keys
            .Hold(() => Has("❯ hold")).Press(Keys.Enter)
            .Hold(() => Has("· gets no message")).Press(Keys.Enter)
            .Hold(() => Has("press / to show the menu")).Type("/")
            .Hold(() => Has("❯ stop")).Press(Keys.Enter);

        using var page = terminal.Open();
        await new RunScreen(page, () => now).RunAsync(run, TestContext.Current.CancellationToken);

        Assert.Equal([RunCommand.Hold, RunCommand.Nudge, RunCommand.Stop], sent);
        terminal.AssertTaped("""
              ● Export the data
                0:10 · waits for you · goes on alone in 0:07
                │ CSV or JSON?
                answer it in its session: claude attach 1a2b
              ❯ hold      it gets no message and waits for you
                send now  it is told to go on alone at once
              ↑↓ move · enter run · esc close
            """.ReplaceLineEndings("\n").TrimEnd('\n').Replace("\n ", "\n", StringComparison.Ordinal)[1..]);
        terminal.AssertTaped("   0:10 · waits for you · gets no message\n   │ CSV or JSON?\n   answer it in its session: claude attach 1a2b\n ❯ send now  it is told to go on alone at once");

        // Once the session goes on there is the task again, the time and what it does, and nothing else.
        terminal.AssertTaped(" ● Export the data\n   1:24 · Edit src/Export.cs\n ❯ press / to show the menu");
    }

    [Fact]
    public async Task EscapePutsAwayWhatAWaitingSessionBroughtAndASlashOpensTheWholeMenu()
    {
        RunCommand? sent = null;
        var run = new ScriptedRun(async (run, view) =>
        {
            view.Show(Waiting);
            sent = await run.NextAsync();
            return new RunResult(RunEnd.Stopped, 0);
        });
        terminal.Keys
            .Hold(() => Has("❯ hold")).Press(Keys.Escape)
            .Hold(() => Has("press / to show the menu")).Type("/")
            .Hold(() => Has("  abort")).Press(Keys.Escape)
            .Hold(() => Has("press / to show the menu")).Type("/")
            .Hold(() => Has("  abort")).Press(Keys.Down, Keys.Down, Keys.Enter);

        using var page = terminal.Open();
        await new RunScreen(page, () => now).RunAsync(run, TestContext.Current.CancellationToken);

        Assert.Equal(RunCommand.Stop, sent);
        terminal.AssertTaped("""
              ❯ hold      it gets no message and waits for you
                send now  it is told to go on alone at once
                stop      finish the current task, then stop
                abort     stop as soon as possible: nothing committed, a report in the task
              ↑↓ move · enter run · esc close
            """.ReplaceLineEndings("\n").TrimEnd('\n').Replace("\n ", "\n", StringComparison.Ordinal)[1..]);
    }

    [Fact]
    public async Task CtrlCTwiceKillsTheRunAndLeavesHowItWentOnTheTape()
    {
        var run = new ScriptedRun(async (run, view) =>
        {
            view.Show(Working);
            return await run.ForEverAsync();
        });
        terminal.Keys
            .Hold(() => Has("1:24")).Press(Keys.ControlC)
            .Hold(() => Has("press ctrl+c again to stop the session and exit")).Press(Keys.ControlC);

        using var page = terminal.Open();
        var screen = new RunScreen(page, () => now);
        await Assert.ThrowsAsync<OperationCanceledException>(() => screen.RunAsync(run, TestContext.Current.CancellationToken));

        Assert.True(run.Killed);
        Assert.Equal(new RunResult(RunEnd.Killed, 0), screen.Result);
        Assert.Equal(["  toobusy · ~/rocket", " 0 tasks in 0 s", " ■ Killed"], terminal.Tape.Text.Split('\n'));
    }

    [Fact]
    public async Task TheTimesGoOnWhileNothingElseChanges()
    {
        var run = new ScriptedRun(async (run, view) =>
        {
            view.Show(Working);
            await run.NextAsync();
            return new RunResult(RunEnd.Stopped, 0);
        });
        // Once the first time is on the tape the clock is moved on, and the tape is to follow it by itself.
        terminal.Keys
            .Hold(() => (now != InstantClock.Start || Has("1:24")) && Later(TimeSpan.FromSeconds(41)) && Has("2:05")).Type("/")
            .Hold(() => Has("❯ stop")).Press(Keys.Enter);

        using var page = terminal.Open();
        await new RunScreen(page, () => now).RunAsync(run, TestContext.Current.CancellationToken);

        terminal.AssertTaped(" ● Export the data\n   2:05 · Edit src/Export.cs");
    }

    [Theory]
    [InlineData(RunPhase.Preparing, false, "Reading the queue…", 0, false, false, " ● Reading the queue…\n ❯ press /")]
    [InlineData(RunPhase.Preparing, true, "Starting #3…", 0, false, false, " ● Export the data\n   1:24 · starting\n")]
    [InlineData(RunPhase.Working, true, "", 0, true, false, " ● Export the data\n   1:24 · Edit src/Export.cs · the last task of this run\n")]
    [InlineData(RunPhase.Working, true, "", 0, false, true, " ● Export the data\n   1:24 · Edit src/Export.cs · wrapping up\n")]
    [InlineData(RunPhase.WaitingForOwner, true, "", 0, false, false, " ● Export the data\n   1:24 · waits for you · gets no message\n")]
    [InlineData(RunPhase.WaitingForLimit, true, "", 3725, false, false, " ● Export the data\n   1:24 · waits for a usage limit · goes on in 1:02:05\n")]
    [InlineData(RunPhase.WaitingForLimit, false, "Waiting for the 5-hour limit to reset", 125, false, false, " ● Waiting for the 5-hour limit to reset\n   goes on in 2:05\n")]
    public async Task WhatTheRunIsDoingIsSaidInTwoLines(RunPhase phase, bool task, string text, int until, bool stopping, bool aborting, string expected)
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
        terminal.Keys.Hold(() => Has(expected)).Type("/").Hold(() => Has("❯ stop")).Press(Keys.Enter);

        using var page = terminal.Open();
        await new RunScreen(page, () => now).RunAsync(run, TestContext.Current.CancellationToken);

        terminal.AssertTaped(expected);
    }

    [Theory]
    [InlineData(RunEnd.Emptied, null, "3 tasks in 0 s · 1 done · 1 done in part · 1 for the owner", null)]
    [InlineData(RunEnd.Limited, "the weekly limit is at 80%", "3 tasks in 0 s · 1 done · 1 done in part · 1 for the owner", " ‖ Stopped: the weekly limit is at 80%")]
    [InlineData(RunEnd.Problem, "#5 left changes in the working tree · claude attach 1a2b", "3 tasks in 0 s · 1 done · 1 done in part · 1 for the owner", " ✖ Stopped: #5 left changes in the working tree · claude attach 1a2b")]
    public async Task WhenTheRunIsOverThePageSaysHowItWentAndIsLeftWithoutAKey(RunEnd end, string? why, string tasks, string? reason)
    {
        var run = new ScriptedRun((_, view) =>
        {
            view.Report(new TaskEnd(Total, RunMark.Done, TimeSpan.FromSeconds(84)));
            view.Report(new TaskEnd(Export, RunMark.Partial, TimeSpan.FromSeconds(3725)));
            view.Report(new TaskEnd(Task(5, "Ship it"), RunMark.Owner, TimeSpan.FromSeconds(7)));
            return System.Threading.Tasks.Task.FromResult(new RunResult(end, 2, why));
        });

        using var page = terminal.Open();
        var screen = new RunScreen(page, () => now);
        await screen.RunAsync(run, TestContext.Current.CancellationToken);

        Assert.Equal(
            [" ✔ Show the total  1:24", " ◐ Export the data  1:02:05", " ◇ Ship it  0:07", " " + tasks, .. reason is null ? (string[])[] : [reason]],
            terminal.Tape.Text.Split('\n').Skip(1));
        Assert.Equal(tasks + (why is null ? "" : $" · Stopped: {why}"), screen.Summary);

        // Between the tasks and how they went there is an empty line, and nothing of the foot is left.
        Assert.Equal("", terminal.Tape.Rows[7]);
        Assert.Equal(" " + tasks, terminal.Tape.Rows[8]);
    }

    [Fact]
    public async Task WhatTheRunWarnsOfStaysOnTheTapeAndGoesOnUnderItsTextWhenItIsLong()
    {
        using var narrow = Live(width: 44, height: 24);
        var run = new ScriptedRun((_, view) =>
        {
            view.Say(new RunLine(RunMark.Paused, "#3 waits for the owner: input needed · it is told to go on alone in 90 s"));
            view.Say(new RunLine(RunMark.Note, "│ CSV or JSON?"));
            view.Say(new RunLine(RunMark.Attention, "The pause of #3 is not whole: its session cannot be gone on with"));
            view.Report(new TaskEnd(Task(3, "Export the data of every order of the year as a file"), RunMark.Paused, TimeSpan.FromSeconds(65)));
            return System.Threading.Tasks.Task.FromResult(new RunResult(RunEnd.Limited, 0, "the weekly limit is spent"));
        });

        using var page = narrow.Open();
        await new RunScreen(page, () => now).RunAsync(run, TestContext.Current.CancellationToken);

        // A title that does not fit gives way to the time.
        Assert.Equal(
            [
                " ▲ The pause of #3 is not whole: its",
                "   session cannot be gone on with",
                " ‖ Export the data of every order of…  1:05",
                " 1 task in 0 s · 1 paused",
                " ‖ Stopped: the weekly limit is spent",
            ],
            narrow.Tape.Text.Split('\n').Skip(1));
    }

    [Fact]
    public async Task InAShortWindowWhatTheSessionSaidGivesWayToTheRestOfTheFoot()
    {
        using var low = Live(width: 100, height: 12);
        var run = new ScriptedRun(async (run, view) =>
        {
            view.Show(Waiting with { Reply = [.. Enumerable.Range(1, 10).Select(line => $"line {line}")] });
            await run.NextAsync();
            return new RunResult(RunEnd.Stopped, 0);
        });
        low.Keys.Hold(() => low.Tape.Text.Contains("❯ hold", StringComparison.Ordinal)).Press(Keys.Enter);

        using var page = low.Open();
        await new RunScreen(page, () => now).RunAsync(run, TestContext.Current.CancellationToken);

        low.AssertTaped("""
              ● Export the data
                0:10 · waits for you · goes on alone in 0:07
                │ line 1
                │ line 2
                │ …
                answer it in its session: claude attach 1a2b
              ❯ hold      it gets no message and waits for you
                send now  it is told to go on alone at once
              ↑↓ move · enter run · esc close
            """.ReplaceLineEndings("\n").TrimEnd('\n').Replace("\n ", "\n", StringComparison.Ordinal)[1..]);
    }

    // Moves the time of the page on, once, and says that it did.
    bool Later(TimeSpan time)
    {
        if (now == InstantClock.Start)
            now += time;
        return true;
    }
}
