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

    // What the terminal's own screen has at the moment: the one of the test, unless another is named.
    bool Has(string text, TestTerminal? on = null) => (on ?? terminal).Tape.Text.Contains(text, StringComparison.Ordinal);

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
            view.Show(Working with { Behind = 1, Limits = new UsageLimits(new UsageWindow("5-hour", 41.7, null), new UsageWindow("weekly", 12, null)) });
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
        terminal.AssertTaped(bar + "\n ✔ #2 Show the total  1:24\n ● #3 Export the data\n   1:24 · Edit src/Export.cs\n"
            + " ❯ press / to show the menu" + new string(' ', 36) + "limits used: 5-hour 41% · weekly 12%\n"
            + " / menu · ctrl+c stop the session and exit" + new string(' ', 46) + "task 2 of 4");

        // Nothing is said of a task that starts, and the page is left with how the run went under its tasks.
        Assert.DoesNotContain("started", terminal.Output.ToString(), StringComparison.Ordinal);
        Assert.Equal(
            [bar, " ✔ #2 Show the total  1:24", " 1 task in 0 s · 1 done", " ‖ Stopped, as you asked"],
            terminal.Tape.Text.Split('\n'));
        Assert.Equal("1 task in 0 s · 1 done · Stopped, as you asked", screen.Summary);
        Assert.True(terminal.Tape.Wraps);
    }

    [Fact]
    public async Task ThePlanOfTheSessionIsABarUnderTheTaskThatIsBuiltAnewWhenThePlanChanges()
    {
        var bars = new List<string?>();
        var run = new ScriptedRun(async (run, view) =>
        {
            // A session that keeps no plan has the two lines of its task and nothing under them.
            view.Show(Working);
            await SeenAsync("1:24 · Edit src/Export.cs");
            bars.Add(Bar());

            view.Show(Working with { Plan = [PlanStep.Done, PlanStep.Active, PlanStep.Pending, PlanStep.Pending] });
            await SeenAsync("▰▱▱▱");
            bars.Add(Bar());

            // The step goes on to the next, and the plan gets two more: a cell for each.
            view.Show(Working with { Plan = [PlanStep.Done, PlanStep.Done, PlanStep.Active, PlanStep.Pending, PlanStep.Pending, PlanStep.Pending] });
            await SeenAsync("▰▰▱▱▱▱");
            bars.Add(Bar());

            view.Show(Working with { Plan = [PlanStep.Done, PlanStep.Done] });
            await SeenAsync("▰▰\n");
            bars.Add(Bar());
            return new RunResult(RunEnd.Emptied, 0);
        });

        using var page = terminal.Open();
        await new RunScreen(page, () => now).RunAsync(run, TestContext.Current.CancellationToken);

        Assert.Equal([" ❯ press / to show the menu", "   ▰▱▱▱", "   ▰▰▱▱▱▱", "   ▰▰"], bars);
    }

    [Fact]
    public async Task TheStepsThatAreDoneAreInTheAccentAndTheOneTheSessionIsAtBlinks()
    {
        var palette = Palette.Dark;
        var run = new ScriptedRun(async (run, view) =>
        {
            view.Show(Working with { Plan = [PlanStep.Done, PlanStep.Done, PlanStep.Active, PlanStep.Pending] });
            await SeenAsync("▰▰▰▱");

            // Nothing of the line changes when the session takes the next step but the cell that blinks.
            view.Show(Working with { Plan = [PlanStep.Done, PlanStep.Done, PlanStep.Active, PlanStep.Active] });
            await SeenAsync("▰▰▰▰");
            return new RunResult(RunEnd.Emptied, 0);
        });

        using var page = terminal.Open(palette: palette);
        await new RunScreen(page, () => now).RunAsync(run, TestContext.Current.CancellationToken);

        var written = terminal.Output.ToString();
        Assert.Contains(palette.Accent("▰▰") + palette.Muted("▱▱"), written, StringComparison.Ordinal);

        // The cell is written where it stands, full, in the shade the blink has at the moment.
        Assert.Matches("\u001b\\[6G\u001b\\[38;2;[0-9;]+m▰", written);
        Assert.Matches("\u001b\\[7G\u001b\\[38;2;[0-9;]+m▰", written);
    }

    [Fact]
    public async Task TheBarCountsTheStepsAndFillsFromTheLeftWhateverOrderTheSessionMarksThemIn()
    {
        var bars = new List<string>();
        var run = new ScriptedRun(async (run, view) =>
        {
            // The third step is done before the second, and the session is at the fourth: the bar has no hole, and
            // without colours the step it is at is an empty cell after the full ones.
            view.Show(Working with { Plan = [PlanStep.Done, PlanStep.Pending, PlanStep.Done, PlanStep.Active, PlanStep.Pending] });
            bars.Add(await CellsAsync(5));

            // A step that went back from done is counted where it is now.
            view.Show(Working with { Plan = [PlanStep.Pending, PlanStep.Done, PlanStep.Active, PlanStep.Pending] });
            bars.Add(await CellsAsync(4));
            return new RunResult(RunEnd.Emptied, 0);
        });

        using var page = terminal.Open();
        await new RunScreen(page, () => now).RunAsync(run, TestContext.Current.CancellationToken);

        Assert.Equal(["   ▰▰▱▱▱", "   ▰▱▱▱"], bars);
    }

    [Fact]
    public async Task WhatBlinksIsTheCellsRightAfterTheFullOnesWhereverItsStepsStandInThePlan()
    {
        var palette = Palette.Dark;
        var frames = new List<string>();
        var run = new ScriptedRun(async (run, view) =>
        {
            view.Show(Working with { Plan = [PlanStep.Done, PlanStep.Pending, PlanStep.Done, PlanStep.Active, PlanStep.Pending] });
            await CellsAsync(5);
            frames.Add(terminal.Output.ToString());

            // The session is at two steps that are apart, the second and the fourth.
            view.Show(Working with { Plan = [PlanStep.Done, PlanStep.Active, PlanStep.Pending, PlanStep.Active] });
            await CellsAsync(4);
            frames.Add(terminal.Output.ToString()[frames[0].Length..]);

            // Between two steps it is at none, and nothing blinks.
            view.Show(Working with { Plan = [PlanStep.Done, PlanStep.Pending, PlanStep.Done] });
            await CellsAsync(3);
            frames.Add(terminal.Output.ToString()[(frames[0].Length + frames[1].Length)..]);
            return new RunResult(RunEnd.Emptied, 0);
        });

        using var page = terminal.Open(palette: palette);
        await new RunScreen(page, () => now).RunAsync(run, TestContext.Current.CancellationToken);

        // The first cell of the bar stands in the fourth column. The third blinks, right after the two that are done,
        // and not the fourth, whose step the session is at.
        static string Lit(int column) => $"\u001b\\[{column}G\u001b\\[38;2;[0-9;]+m▰";
        Assert.Contains(palette.Accent("▰▰") + palette.Muted("▱▱▱"), frames[0], StringComparison.Ordinal);
        Assert.Matches(Lit(6), frames[0]);
        Assert.DoesNotMatch(Lit(7), frames[0]);

        Assert.Contains(palette.Accent("▰") + palette.Muted("▱▱▱"), frames[1], StringComparison.Ordinal);
        Assert.Matches(Lit(5), frames[1]);
        Assert.Matches(Lit(6), frames[1]);
        Assert.DoesNotMatch(Lit(7), frames[1]);

        Assert.Contains(palette.Accent("▰▰") + palette.Muted("▱"), frames[2], StringComparison.Ordinal);
        Assert.DoesNotMatch("\u001b\\[[0-9]+G\u001b\\[38;2;[0-9;]+m▰", frames[2]);
    }

    [Fact]
    public async Task TheLineOfTheCommandsShowsTheWindowsOfTheLastStatusAndNothingForOneThatIsNotKnown()
    {
        var edges = new List<string>();
        var run = new ScriptedRun(async (run, view) =>
        {
            view.Show(Working with { Limits = new UsageLimits(new UsageWindow("5-hour", 11, null), new UsageWindow("weekly", 4, null)) });
            await SeenAsync("5-hour 11% · weekly 4%");
            edges.Add(Pointer());

            // A window whose reset has passed is not on the status, and nothing stands for it.
            view.Show(Working with { Limits = new UsageLimits(null, new UsageWindow("weekly", 4, null)) });
            await GoneAsync("5-hour");
            edges.Add(Pointer());

            view.Show(Working);
            await GoneAsync("weekly");
            edges.Add(Pointer());
            return new RunResult(RunEnd.Emptied, 0);
        });

        using var page = terminal.Open();
        await new RunScreen(page, () => now).RunAsync(run, TestContext.Current.CancellationToken);

        const string Menu = " ❯ press / to show the menu";
        Assert.Equal([Menu + new string(' ', 37) + "limits used: 5-hour 11% · weekly 4%", Menu + new string(' ', 50) + "limits used: weekly 4%", Menu], edges);
    }

    [Theory]
    [InlineData(60, 10, "5-hour 11% · weekly 4%")]
    [InlineData(40, 0, "")]
    public async Task InANarrowWindowTheWordsOfTheLimitsGiveWayFirstAndThenTheFigures(int width, int gap, string limits)
    {
        using var narrow = Live(width, height: 24);
        var run = new ScriptedRun(async (run, view) =>
        {
            view.Show(Working with { Limits = new UsageLimits(new UsageWindow("5-hour", 11, null), new UsageWindow("weekly", 4, null)) });
            await SeenAsync("Edit src/Export.cs", narrow);
            return new RunResult(RunEnd.Emptied, 0);
        });

        using var page = narrow.Open();
        await new RunScreen(page, () => now).RunAsync(run, TestContext.Current.CancellationToken);

        narrow.AssertTaped(" ❯ press / to show the menu" + new string(' ', gap) + limits + "\n");
    }

    [Fact]
    public async Task TheLimitsAreNotShownWhileTheMenuIsOpen()
    {
        var limits = new UsageLimits(new UsageWindow("5-hour", 11, null), new UsageWindow("weekly", 4, null));
        var seen = new List<bool>();
        var run = new ScriptedRun(async (run, view) =>
        {
            // The menu that `/` opens, and the one a session that waits for the owner brings.
            view.Show(Working with { Limits = limits });
            await SeenAsync("❯ stop");
            seen.Add(Has("5-hour"));
            await SeenAsync("press / to show the menu");
            seen.Add(Has("5-hour"));
            view.Show(Waiting with { Limits = limits });
            await SeenAsync("❯ hold");
            seen.Add(Has("5-hour"));
            Assert.Equal(RunCommand.Hold, await run.NextAsync());
            return new RunResult(RunEnd.Emptied, 0);
        });
        terminal.Keys
            .Hold(() => Has("press / to show the menu")).Type("/")
            .Hold(() => seen.Count == 1).Press(Keys.Escape)
            .Hold(() => seen.Count == 3).Press(Keys.Enter);

        using var page = terminal.Open();
        await new RunScreen(page, () => now).RunAsync(run, TestContext.Current.CancellationToken);

        Assert.Equal([false, true, false], seen);
    }

    [Fact]
    public async Task UnderTheSecondRuleStandsThePlaceOfTheTaskAmongTheTasksOfTheRun()
    {
        var edges = new List<string>();
        var run = new ScriptedRun(async (run, view) =>
        {
            // Before the first task there is none.
            view.Show(new RunStatus(RunPhase.Preparing, "Reading the queue…"));
            await SeenAsync("Reading the queue…");
            edges.Add(terminal.Tape.Text.Split('\n')[^1]);

            view.Show(Working with { Behind = 1 });
            await SeenAsync("task 2 of 4");
            edges.Add(terminal.Tape.Text.Split('\n')[^1]);

            // Between tasks it stays as it was, and the next task has it as the queue was read before it.
            view.Report(new TaskEnd(Export, RunMark.Done, TimeSpan.FromSeconds(84)));
            view.Show(new RunStatus(RunPhase.Preparing, "Reading the queue…"));
            await SeenAsync("Reading the queue…");
            edges.Add(terminal.Tape.Text.Split('\n')[^1]);

            view.Show(Working with { Task = Total, Behind = 2, Queued = 3 });
            await SeenAsync("task 3 of 6");
            edges.Add(terminal.Tape.Text.Split('\n')[^1]);
            return new RunResult(RunEnd.Emptied, 1);
        });

        using var page = terminal.Open();
        await new RunScreen(page, () => now).RunAsync(run, TestContext.Current.CancellationToken);

        const string Hints = " / menu · ctrl+c stop the session and exit";
        Assert.Equal([Hints, Hints + new string(' ', 46) + "task 2 of 4", Hints + new string(' ', 46) + "task 2 of 4", Hints + new string(' ', 46) + "task 3 of 6"], edges);
    }

    [Fact]
    public async Task TheKeysNameTheSlashWhileNoMenuIsOpenAlsoWhenTheRunStopsOrAborts()
    {
        var keys = new List<string>();
        var run = new ScriptedRun(async (run, view) =>
        {
            view.Show(Working);
            await SeenAsync("press / to show the menu");
            keys.Add(Under());

            // The menu that `/` opens has keys of its own, and the first Ctrl+C asks what it asks.
            await SeenAsync("❯ stop");
            keys.Add(Under());
            await SeenAsync("press / to show the menu");
            keys.Add(Under());
            await SeenAsync("press ctrl+c again");
            keys.Add(Under());
            await GoneAsync("press ctrl+c again");

            // Once the line of the commands says that the run stops or aborts, the keys still say where the menu opens.
            view.Show(Working with { Stopping = true });
            await SeenAsync("stopping after this task");
            keys.Add(Under());
            view.Show(Working with { Aborting = true });
            await SeenAsync("aborting: the session is wrapping up");
            keys.Add(Under());
            view.Show(new RunStatus(RunPhase.Preparing, "Reading the queue…") { Stopping = true });
            await SeenAsync("stopping before the next task");
            keys.Add(Under());
            return new RunResult(RunEnd.Stopped, 0);
        });
        terminal.Keys
            .Hold(() => keys.Count == 1).Type("/")
            .Hold(() => keys.Count == 2).Press(Keys.Escape)
            .Hold(() => keys.Count == 3).Press(Keys.ControlC)
            .Hold(() => keys.Count == 4).Type("#");

        using var page = terminal.Open();
        await new RunScreen(page, () => now).RunAsync(run, TestContext.Current.CancellationToken);

        const string Hints = " / menu · ctrl+c stop the session and exit";
        Assert.Equal([Hints, " ↑↓ move · enter run · esc close", Hints, " press ctrl+c again to stop the session and exit", Hints, Hints, Hints], keys);
    }

    // Waits until the terminal's own screen shows the text.
    Task SeenAsync(string text, TestTerminal? on = null) => UntilAsync(() => Has(text, on), $"show \"{text}\"", on);

    // Waits until the terminal's own screen does not show the text any more.
    Task GoneAsync(string text) => UntilAsync(() => !Has(text), $"stop showing \"{text}\"");

    // Waits until that is so. A page that does not get there within the patience of the keys fails the test with
    // what its screen shows, instead of keeping the run, and the whole run of the tests with it, from ever ending.
    async Task UntilAsync(Func<bool> done, string what, TestTerminal? on = null)
    {
        var waited = System.Diagnostics.Stopwatch.StartNew();
        while (!done())
        {
            if (waited.Elapsed > Keys.Patience)
                throw new InvalidOperationException($"The screen did not {what} in {Keys.Patience.TotalSeconds} s. It shows:\n{(on ?? terminal).Tape.Text}");
            await System.Threading.Tasks.Task.Delay(5, TestContext.Current.CancellationToken);
        }
    }

    // The line under the second rule, without what stands at its right edge.
    string Under() => terminal.Tape.Text.Split('\n')[^1].Split("  ")[0];

    // The line of the commands, with the pointer that shows where the menu opens.
    string Pointer(string text = "press / to show the menu") => terminal.Tape.Text.Split('\n').First(row => row.Contains(text, StringComparison.Ordinal));

    // The line under the time and the step of the task that is worked on.
    string? Bar()
    {
        var rows = terminal.Tape.Text.Split('\n');
        var at = Array.FindIndex(rows, row => row.Contains("1:24 · Edit src/Export.cs", StringComparison.Ordinal));
        return at < 0 || at + 1 >= rows.Length ? null : rows[at + 1];
    }

    // Waits until that line is a bar of so many cells, whatever they are, and gives it.
    async Task<string> CellsAsync(int count)
    {
        string? bar = null;
        await UntilAsync(() => (bar = Bar()) is not null && bar.Trim().Length == count && bar.Trim().All(cell => cell is '▰' or '▱'), $"show a bar of {count} cells");
        return bar!;
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
        Assert.False(terminal.Saw("❯ #"));
        terminal.AssertTaped("""
              ● #3 Export the data
                1:24 · Edit src/Export.cs
              ❯ stop   finish the current task, then stop
                abort  stop as soon as possible: nothing committed, a report in the task
              ↑↓ move · enter run · esc close
            """.ReplaceLineEndings("\n").TrimEnd('\n').Replace("\n ", "\n", StringComparison.Ordinal)[1..]);
        terminal.AssertTaped("   stop   finish the current task, then stop\n ❯ abort  stop as soon as possible");
    }

    [Fact]
    public async Task TheMenuMovesWithTheKeysOfEveryList()
    {
        RunCommand? sent = null;
        var run = new ScriptedRun(async (run, view) =>
        {
            view.Show(Working);
            sent = await run.NextAsync();
            return new RunResult(RunEnd.Stopped, 0);
        });

        // Tab goes down to `abort`, Shift+Tab back up to `stop`, and `j` down again.
        terminal.Keys
            .Hold(() => Has("press / to show the menu")).Type("/")
            .Hold(() => Has("❯ stop")).Press(Keys.Tab)
            .Hold(() => Has("❯ abort")).Press(Keys.ShiftTab)
            .Hold(() => Has("❯ stop")).Press(Keys.Letter('j'))
            .Hold(() => Has("❯ abort")).Press(Keys.Enter);

        using var page = terminal.Open();
        await new RunScreen(page, () => now).RunAsync(run, TestContext.Current.CancellationToken);

        Assert.Equal(RunCommand.Abort, sent);
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
              ● #3 Export the data
                0:10 · waits for you · goes on alone in 0:07
                │ CSV or JSON?
                answer it in its session: claude attach 1a2b
              ❯ hold      it gets no message and waits for you
                send now  it is told to go on alone at once
              ↑↓ move · enter run · esc close
            """.ReplaceLineEndings("\n").TrimEnd('\n').Replace("\n ", "\n", StringComparison.Ordinal)[1..]);
        terminal.AssertTaped("   0:10 · waits for you · gets no message\n   │ CSV or JSON?\n   answer it in its session: claude attach 1a2b\n ❯ send now  it is told to go on alone at once");

        // Once the session goes on there is the task again, the time and what it does, and nothing else.
        terminal.AssertTaped(" ● #3 Export the data\n   1:24 · Edit src/Export.cs\n ❯ press / to show the menu");
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
    public async Task AKilledRunIsShownUntilItIsOverAndNoKeyDoesAnythingMeanwhile()
    {
        var settled = new TaskCompletionSource();
        var run = new ScriptedRun(async (run, view) =>
        {
            view.Show(Working);
            try
            {
                return await run.ForEverAsync();
            }
            catch (OperationCanceledException)
            {
                // What the kill does not cut takes its time, and what the run says meanwhile goes on the tape.
                view.Say(new RunLine(RunMark.Attention, "#3 could not be labelled: GitHub did not answer"));
                await Settled(settled);
                return new RunResult(RunEnd.Killed, 0);
            }
        });
        var stopping = "  toobusy · ~/rocket\n ▲ #3 could not be labelled: GitHub did not answer\n ● Stopping the run and leaving #3 as it should be…";
        bool? taken = null;
        terminal.Keys
            .Hold(() => Has("1:24")).Press(Keys.ControlC)
            .Hold(() => Has("press ctrl+c again to stop the session and exit")).Press(Keys.ControlC)
            .Hold(() => terminal.Tape.Text == stopping).Type("/").Press(Keys.Enter)
            .Hold(() =>
            {
                // Every key was read, and the foot drawn after it: the run may be over now.
                taken = terminal.ControlCTaken;
                settled.TrySetResult();
                return true;
            });

        using var page = terminal.Open();
        var screen = new RunScreen(page, () => now);
        await Assert.ThrowsAsync<OperationCanceledException>(() => screen.RunAsync(run, TestContext.Current.CancellationToken));

        // Ctrl+C was the terminal's own meanwhile, the keys opened no menu, and how the run ended is kept.
        Assert.False(taken);
        Assert.False(terminal.Saw("finish the current task, then stop"));
        Assert.Equal(new RunResult(RunEnd.Killed, 0), screen.Result);
        Assert.Equal(["  toobusy · ~/rocket", " ▲ #3 could not be labelled: GitHub did not answer", " 0 tasks in 0 s", " ■ Killed"], terminal.Tape.Text.Split('\n'));
    }

    [Fact]
    public async Task ARunThatIsKilledFromOutsideBetweenTasksSaysThatItStopsUntilItIsOver()
    {
        using var outside = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        var settled = new TaskCompletionSource();
        var run = new ScriptedRun(async (run, view) =>
        {
            view.Show(new RunStatus(RunPhase.Preparing, "Reading the queue…") { Available = RunCommands.Stop | RunCommands.Abort });
            try
            {
                return await run.ForEverAsync();
            }
            catch (OperationCanceledException)
            {
                await Settled(settled);
                return new RunResult(RunEnd.Killed, 0);
            }
        });
        terminal.Keys
            .Hold(() =>
            {
                if (Has("● Reading the queue…"))
                    outside.Cancel();
                return terminal.Tape.Text == "  toobusy · ~/rocket\n ● Stopping the run…";
            })
            .Type("/")
            .Hold(() =>
            {
                settled.TrySetResult();
                return true;
            });

        using var page = terminal.Open();
        var result = await new RunScreen(page, () => now).RunAsync(run, outside.Token);

        Assert.Equal(new RunResult(RunEnd.Killed, 0), result);
        Assert.False(terminal.Saw("finish the current task, then stop"));
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

        terminal.AssertTaped(" ● #3 Export the data\n   2:05 · Edit src/Export.cs");
    }

    [Theory]
    [InlineData(RunPhase.Preparing, false, "Reading the queue…", 0, false, false, " ● Reading the queue…\n ❯ press /")]
    [InlineData(RunPhase.Preparing, true, "Starting #3…", 0, false, false, " ● #3 Export the data\n   1:24 · starting\n")]
    [InlineData(RunPhase.Preparing, true, "Running the command before #3…", 0, false, false, " ● #3 Export the data\n   1:24 · the command before the task\n", "the command before the task")]
    [InlineData(RunPhase.Working, true, "", 0, true, false, " ● #3 Export the data\n   1:24 · Edit src/Export.cs\n ❯ stopping after this task\n")]
    [InlineData(RunPhase.Working, true, "", 0, false, true, " ● #3 Export the data\n   1:24 · Edit src/Export.cs\n ❯ aborting: the session is wrapping up\n")]
    [InlineData(RunPhase.Preparing, false, "Reading the queue…", 0, true, false, " ● Reading the queue…\n ❯ stopping before the next task\n")]
    [InlineData(RunPhase.Preparing, false, "Reading the queue…", 0, false, true, " ● Reading the queue…\n ❯ stopping before the next task\n")]
    [InlineData(RunPhase.WaitingForOwner, true, "", 0, false, false, " ● #3 Export the data\n   1:24 · waits for you · gets no message\n")]
    [InlineData(RunPhase.WaitingForLimit, true, "", 3725, false, false, " ● #3 Export the data\n   1:24 · waits for a usage limit · goes on in 1:02:05\n")]
    [InlineData(RunPhase.WaitingForLimit, false, "Waiting for the 5-hour limit to reset", 125, false, false, " ● Waiting for the 5-hour limit to reset\n   goes on in 2:05\n")]
    public async Task WhatTheRunIsDoingIsSaidInTwoLines(RunPhase phase, bool task, string text, int until, bool stopping, bool aborting, string expected, string? command = null)
    {
        // A task that is being taken has no session yet: its step is the command of the project that runs.
        var status = Working with
        {
            Phase = phase,
            Step = phase == RunPhase.Preparing ? command : Working.Step,
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

    [Fact]
    public async Task TheLineOfTheCommandsSaysWhereTheMenuOpensAgainWhenTheStopIsTakenBack()
    {
        var lines = new List<string>();
        var run = new ScriptedRun(async (run, view) =>
        {
            view.Show(Working with { Stopping = true });
            await SeenAsync("stopping after this task");
            lines.Add(Pointer("❯ stopping"));

            view.Show(Working);
            await SeenAsync("press / to show the menu");
            lines.Add(Pointer("❯ press"));
            return new RunResult(RunEnd.Emptied, 0);
        });

        using var page = terminal.Open();
        await new RunScreen(page, () => now).RunAsync(run, TestContext.Current.CancellationToken);

        Assert.Equal([" ❯ stopping after this task", " ❯ press / to show the menu"], lines);
        Assert.False(terminal.Saw("the last task of this run"));
    }

    [Fact]
    public async Task ARunThatWasStartedForANumberOfTasksSaysSoWhereItSaysThatItsOwnerStoppedIt()
    {
        var run = new ScriptedRun((_, view) =>
        {
            view.Report(new TaskEnd(Total, RunMark.Done, TimeSpan.FromSeconds(84)));
            return System.Threading.Tasks.Task.FromResult(new RunResult(RunEnd.Stopped, 1, "Stopped after 1 task, as you asked"));
        });

        using var page = terminal.Open();
        var screen = new RunScreen(page, () => now);
        await screen.RunAsync(run, TestContext.Current.CancellationToken);

        Assert.Equal([" ✔ #2 Show the total  1:24", " 1 task in 0 s · 1 done", " ‖ Stopped after 1 task, as you asked"], terminal.Tape.Text.Split('\n').Skip(1));
        Assert.Equal("1 task in 0 s · 1 done · Stopped after 1 task, as you asked", screen.Summary);
    }

    [Theory]
    [InlineData(RunEnd.Emptied, null, "3 tasks in 0 s · 1 done · 1 done in part · 1 for the owner", null)]
    [InlineData(RunEnd.Limited, "#5 is paused: the weekly limit is spent", "3 tasks in 0 s · 1 done · 1 done in part · 1 for the owner", " ‖ Stopped: #5 is paused: the weekly limit is spent")]
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
            [" ✔ #2 Show the total  1:24", " ◐ #3 Export the data  1:02:05", " ◇ #5 Ship it  0:07", " " + tasks, .. reason is null ? (string[])[] : [reason]],
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
                " ‖ #3 Export the data of every order…  1:05",
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
              ● #3 Export the data
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

    // The end of what a killed run does not cut, which the keys of the test give: a page that stops reading them
    // fails the test instead of keeping it waiting.
    static Task Settled(TaskCompletionSource settled) => settled.Task.WaitAsync(Keys.Patience, TestContext.Current.CancellationToken);

    // Moves the time of the page on, once, and says that it did.
    bool Later(TimeSpan time)
    {
        if (now == InstantClock.Start)
            now += time;
        return true;
    }
}
