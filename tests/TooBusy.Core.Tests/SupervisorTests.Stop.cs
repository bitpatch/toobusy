using TooBusy.Core.Queue;
using TooBusy.Core.Run;

namespace TooBusy.Core.Tests;

// Stop, abort and kill: what each leaves of the task and of its session.
public sealed partial class SupervisorTests
{
    [Fact]
    public async Task ASessionThatReportsWhileTheRunAbortsIsSettledAndToldNothing()
    {
        tracker.Add(3);
        var session = assistant.Session(3).Works(3).Asks("input needed", Done);
        clock.At(AfterLook(2), () => Run.Send(RunCommand.Abort));

        var result = await RunAsync();

        Assert.Equal(new RunResult(RunEnd.Stopped, 1), result);
        Assert.Empty(session.Told);
        AssertDid("status 3 InProgress", "assign 3", "comment 3", "close 3", "status 3 Done");
    }

    [Fact]
    public async Task ContinueTakesTheStopBack()
    {
        tracker.Add(3);
        tracker.Add(5);
        assistant.Session(3).Works(4).Ends(Done);
        clock.At(AfterLook(2), () => Run.Send(RunCommand.Stop));
        clock.At(AfterLook(2) + policy.Tick, () => Run.Send(RunCommand.Stop));
        clock.At(AfterLook(3), () => Run.Send(RunCommand.Continue));
        clock.At(AfterLook(3) + policy.Tick, () => Run.Send(RunCommand.Continue));

        var result = await RunAsync();

        Assert.Equal(new RunResult(RunEnd.Emptied, 2), result);
        Assert.Contains("  Already stopping after the current task", log.Lines);
        Assert.Contains("▶ The queue goes on after the current task", log.Lines);
        Assert.Contains("  Nothing to take back: the queue goes on", log.Lines);
    }

    [Fact]
    public async Task AbortAsksTheSessionToWrapUpAndTheTaskIsMarkedAsInterrupted()
    {
        tracker.Add(3);
        tracker.Add(5);
        var session = assistant.Session(3).Works(20).WhenAsked().Works().Ends("Undone: a.cs.\nLeft: the export.\nTOOBUSY: interrupted");
        clock.At(AfterLook(2), () => Run.Send(RunCommand.Abort));

        var result = await RunAsync();

        Assert.Equal(new RunResult(RunEnd.Stopped, 0), result);
        Assert.Equal([Briefing.WrapUp(3)], session.Asked);
        Assert.Empty(session.Told);
        AssertDid("status 3 InProgress", "assign 3", "label 3 +interrupted", "comment 3", "status 3 Todo");
        Assert.StartsWith("**Interrupted.** Nothing is committed; a run takes the task again.\n\nUndone: a.cs.\nLeft: the export.", tracker.Comments[0].Text, StringComparison.Ordinal);
        Assert.Equal([3], assistant.Started.Select(start => start.Task));
        Assert.Equal([(3, RunMark.Interrupted)], Ended);
        Assert.Contains("■ Asked #3 to wrap up after its current step: nothing committed, a report in the task, the interrupted label", log.Lines);
        Assert.Contains($"■ #3 Task 3 — interrupted after {Spoken.Time(3 * policy.Poll)}: the report is in the task, and the next run takes it first", log.Lines);
        Assert.Contains("  │ Undone: a.cs.", log.Lines);
        Assert.Equal("■ Stopped after /abort · 0 done", log.Lines[^2]);

        // The record stays, and says that the session wrapped up: the next run goes on with its conversation.
        Assert.Equal(Record(3, RecordState.WrappedUp), state.Record);
    }

    [Fact]
    public async Task AnAbortThatIsNotHeardCutsTheTurnOnceAfterAWhile()
    {
        tracker.Add(3);
        var session = assistant.Session(3).Works().Then().Works().Ends("Undone.\nTOOBUSY: interrupted");
        clock.At(AfterLook(2), () => Run.Send(RunCommand.Abort));

        var result = await RunAsync();

        Assert.Equal(RunEnd.Stopped, result.End);
        Assert.Single(session.Asked);
        Assert.Equal([Briefing.WrapUp(3)], session.Told);
        Assert.Contains($"■ #3 did not wrap up in {Spoken.Time(policy.AbortGrace)}: its turn is cut, and it is asked again", log.Lines);
        Assert.Contains("label 3 +interrupted", tracker.Did);
    }

    [Fact]
    public async Task AnAbortOfASessionThatWaitsForTheOwnerCutsItsTurnAtOnce()
    {
        tracker.Add(3);
        var session = assistant.Session(3).Works().Asks().Then().Ends("Undone.\nTOOBUSY: interrupted");
        clock.At(AfterLook(2), () => Run.Send(RunCommand.Abort));

        var result = await RunAsync();

        Assert.Equal(RunEnd.Stopped, result.End);
        Assert.Empty(session.Asked);
        Assert.Equal([Briefing.WrapUp(3)], session.Told);
        Assert.Contains("■ #3 waits for the owner: its turn is cut, and it is told to wrap up", log.Lines);
        Assert.True(clock.Now - FakeClock.Start < policy.OwnerWait, "The abort waited for the countdown.");
    }

    [Fact]
    public async Task AnAbortedTurnThatEndsWithoutTheLineIsToldToWrapUpAndThenWrapsUp()
    {
        tracker.Add(3);
        var session = assistant.Session(3).Works(20).WhenAsked().Ends("I stopped.").Then().Works().Ends("Undone.\nTOOBUSY: interrupted");
        clock.At(AfterLook(2), () => Run.Send(RunCommand.Abort));

        var result = await RunAsync();

        Assert.Equal(new RunResult(RunEnd.Stopped, 0), result);
        Assert.Equal([Briefing.WrapUp(3)], session.Asked);
        Assert.Equal([Briefing.WrapUp(3)], session.Told);
        Assert.Contains("‖ #3 ended its turn without saying how the task went · fake attach 3", log.Lines);
        Assert.Contains("■ #3 waits for the owner: its turn is cut, and it is told to wrap up", log.Lines);
        Assert.Contains("label 3 +interrupted", tracker.Did);
        Assert.DoesNotContain("#3 waits for an answer", machine.Notified);
    }

    [Fact]
    public async Task AnAbortedTurnThatEndsWithoutTheLineAgainAfterTheRequestFails()
    {
        tracker.Add(3);
        var session = assistant.Session(3).Works(20).WhenAsked().Ends("I stopped.");
        clock.At(AfterLook(2), () => Run.Send(RunCommand.Abort));

        var result = await RunAsync();

        Assert.Equal([Briefing.WrapUp(3)], session.Told);
        Assert.Equal("the session of #3 did not say how the task went · fake attach 3", result.Why);
        Assert.Contains("label 3 +needs-owner", tracker.Did);
        Assert.DoesNotContain(log.Lines, line => line.Contains("go on alone", StringComparison.Ordinal));
    }

    // The messages it got before do not leave it to the owner: the request to wrap up comes first.
    [Fact]
    public async Task AnAbortedTurnThatEndsWithoutTheLineIsToldToWrapUpHoweverOftenItWasToldToGoOnAlone()
    {
        policy = policy with { Messages = 1 };
        tracker.Add(3);
        var session = assistant.Session(3).Works().Ends("What now?")
            .Then().Works(and: () => Run.Send(RunCommand.Abort)).Ends("Still nothing.")
            .Then().Ends("Undone.\nTOOBUSY: interrupted");

        var result = await RunAsync();

        Assert.Equal(new RunResult(RunEnd.Stopped, 0), result);
        Assert.Equal([Briefing.Alone(3, Rules.Labels.Manual), Briefing.WrapUp(3)], session.Told);
        Assert.DoesNotContain(log.Lines, line => line.Contains("it is left to the owner", StringComparison.Ordinal));
        Assert.Contains("label 3 +interrupted", tracker.Did);
    }

    [Fact]
    public async Task AnAbortBeforeAnythingRunsStopsTheQueue()
    {
        tracker.Add(3);
        Run.Send(RunCommand.Abort);

        var result = await RunAsync();

        Assert.Equal(new RunResult(RunEnd.Stopped, 0), result);
        Assert.Empty(assistant.Started);
        Assert.Equal(["■ Nothing is running: the queue stops before the next task", "■ Stopped after /abort · 0 done", "  Worked 0 s"], log.Lines);
    }

    [Fact]
    public async Task AnAbortCannotBeTakenBackAndIsAskedForOnce()
    {
        tracker.Add(3);
        assistant.Session(3).Works(20).WhenAsked().Works().Ends("Undone.\nTOOBUSY: interrupted");
        clock.At(AfterLook(2), () => Run.Send(RunCommand.Abort));
        clock.At(AfterLook(2) + policy.Tick, () =>
        {
            Run.Send(RunCommand.Continue);
            Run.Send(RunCommand.Abort);
            Run.Send(RunCommand.Stop);
            Run.Send(RunCommand.Nudge);
        });

        await RunAsync();

        Assert.Equal(
            ["  An /abort cannot be taken back", "  Already asked to wrap up", "  Already aborting", "  /nudge means nothing now"],
            log.Lines.Where(line => line.StartsWith("  A", StringComparison.Ordinal) || line.StartsWith("  /", StringComparison.Ordinal)));
    }

    [Fact]
    public async Task ASessionThatInterruptsItselfStopsTheRun()
    {
        tracker.Add(3);
        tracker.Add(5);
        assistant.Session(3).Works().Ends("I gave up.\nTOOBUSY: interrupted");

        var result = await RunAsync();

        Assert.Equal("#3 was interrupted, and nobody asked for it · fake attach 3", result.Why);
        Assert.Contains("label 3 +interrupted", tracker.Did);
        Assert.Single(assistant.Started);
    }

    [Fact]
    public async Task KillStopsTheSessionAndLeavesTheTaskAsItIs()
    {
        tracker.Add(3);
        var session = assistant.Session(3).Works();
        clock.At(TimeSpan.FromSeconds(10), kill.Cancel);

        var result = await RunAsync();

        Assert.Equal(new RunResult(RunEnd.Killed, 0, "#3 stays In Progress, and the next run goes on with its session · fake attach 3"), result);
        Assert.Equal(1, session.Stopped);
        AssertDid("status 3 InProgress", "assign 3");
        Assert.Contains("■ Killed the session of #3: the task stays In Progress, and the next run goes on with its session · fake attach 3", log.Lines);

        // The record of the task stays: it is what the next run goes on by.
        Assert.Equal(Record(3), state.Record);
        Assert.False(machine.Awake);
        Assert.Equal([(3, RunMark.Interrupted)], Ended);
    }

    [Fact]
    public async Task ATaskThatWasInterruptedGoesFirstLosesTheLabelAndItsSessionIsToldSo()
    {
        tracker.Add(3);
        tracker.Add(5, labels: ["bug", "interrupted"]);

        await RunAsync();

        Assert.Equal([5, 3], assistant.Started.Select(start => start.Task));
        AssertDid(5, "status 5 InProgress", "assign 5", "label 5 -interrupted", "comment 5", "close 5", "status 5 Done");
        Assert.Contains("- This task was interrupted before.", assistant.Started[0].Message, StringComparison.Ordinal);
        Assert.DoesNotContain("interrupted before", assistant.Started[1].Message, StringComparison.Ordinal);
        Assert.Contains("→ #5 Task 5 — started · a new session; the task was interrupted before · fake attach 5", log.Lines);
    }

    [Fact]
    public async Task AnAssistantThatDoesNotStartStopsTheRunAndTheTaskGoesBack()
    {
        tracker.Add(3);
        assistant.RefuseToStart = "claude did not start #3: not logged in";

        var result = await RunAsync();

        Assert.Equal(new RunResult(RunEnd.Problem, 0, "claude did not start #3: not logged in"), result);
        Assert.Equal(["status 3 InProgress", "status 3 Todo"], tracker.Did);

        // A task whose session never started is not one that is over.
        Assert.Empty(log.Ends);
    }

    [Fact]
    public async Task AKillWhileASessionStartsSendsTheTaskBackAndStopsTheSessionTheStartLeft()
    {
        plan = plan with { Commands = Around };
        tracker.Add(3);
        tracker.Add(5);
        assistant.Starting = _ => kill.Cancel();
        assistant.StartsAnyway = true;

        var result = await RunAsync();

        const string Left = "#3 was being started: it is back in Todo, and the session that started for it all the same is stopped · fake attach 3";
        Assert.Equal(new RunResult(RunEnd.Killed, 0, Left), result);
        Assert.Contains($"■ Killed while {Left}", log.Lines);
        Assert.Equal(["status 3 InProgress", "status 3 Todo"], tracker.Did);

        // The session is looked for by the name the start gave it, since the start was made, after the command before.
        var (start, since) = Assert.Single(assistant.LookedFor);
        Assert.Equal(("#3 Task 3", FakeClock.Start + shell.Takes), (start.Name, since));
        Assert.Equal(1, assistant.Session(3).Stopped);

        // No record names a session that does not work, and what the task started is stopped by the command after it.
        Assert.Null(state.Record);
        Assert.Equal([(Free, "3", policy.CommandTime), (Halt, "3", policy.KilledCommandTime)], shell.Ran);
        Assert.Empty(log.Ends);
    }

    [Fact]
    public async Task AKillWhileASessionStartsSaysWhenNoSessionWasFound()
    {
        tracker.Add(3);
        assistant.Starting = _ => kill.Cancel();

        var result = await RunAsync();

        Assert.Equal(new RunResult(RunEnd.Killed, 0, "#3 was being started: it is back in Todo, and no session was found for it"), result);
        Assert.Equal(["status 3 InProgress", "status 3 Todo"], tracker.Did);
        Assert.Single(assistant.LookedFor);
        Assert.Empty(assistant.Started);
    }

    [Fact]
    public async Task AKillWhileATaskIsMovedToInProgressSendsItBackAndLooksForNoSession()
    {
        tracker.Add(3);
        tracker.Doing = what =>
        {
            if (what == "status 3 InProgress")
                kill.Cancel();
        };

        var result = await RunAsync();

        Assert.Equal(new RunResult(RunEnd.Killed, 0, "#3 was being started: it is back in Todo"), result);
        Assert.Equal(["status 3 Todo"], tracker.Did);
        Assert.Empty(assistant.LookedFor);
    }

    [Fact]
    public async Task ATaskThatCannotBeSentBackAfterAKillWhileItsSessionStartsIsSaid()
    {
        tracker.Add(3);
        tracker.Refused.Add("status 3 Todo");
        assistant.Starting = _ => kill.Cancel();

        var result = await RunAsync();

        Assert.Equal(new RunResult(RunEnd.Killed, 0, "#3 was being started: it could not be moved back to Todo, and no session was found for it"), result);
        Assert.Contains("▲ The tracker refused: status 3 Todo. · set it right by hand before the next run", log.Lines);
    }

    [Fact]
    public async Task AKillWhileASessionStartsWithoutABoardLeavesTheTaskAsItWas()
    {
        plan = plan with { Rules = Rules with { Board = false } };
        tracker.Add(3);
        assistant.Starting = _ => kill.Cancel();

        var result = await RunAsync();

        Assert.Equal(new RunResult(RunEnd.Killed, 0, "#3 was being started: it is left as it was, and no session was found for it"), result);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task NeitherTheNearNorTheFarLimitHoldsATaskBack(bool far)
    {
        tracker.Add(3);
        tracker.Add(5);
        assistant.Limits = far
            ? new UsageLimits(null, new UsageWindow("weekly", 100, FakeClock.Start + TimeSpan.FromDays(3)))
            : new UsageLimits(new UsageWindow("5-hour", 99.5, FakeClock.Start + TimeSpan.FromHours(2)), new UsageWindow("weekly", 40, null));

        var result = await RunAsync();

        Assert.Equal(new RunResult(RunEnd.Emptied, 2), result);
        Assert.Equal([3, 5], assistant.Started.Select(started => started.Task));
        Assert.True(clock.Now - FakeClock.Start < TimeSpan.FromMinutes(1));
        Assert.DoesNotContain(log.Statuses, status => status.Phase == RunPhase.WaitingForLimit);
    }

    // The look it is settled by is the one of its question: the line before the question is not read.
    [Fact]
    public async Task AnAbortedSessionThatAsksAfterItsLineAndDoesNotGoOnIsNotSettledByTheLine()
    {
        tracker.Add(3);
        var session = assistant.Session(3).Works().Asks("input needed", $"{Done}\n\nShould I also update the docs?");
        session.Deaf = "it is deaf";
        clock.At(AfterLook(2), () => Run.Send(RunCommand.Abort));

        var result = await RunAsync();

        Assert.Equal([Briefing.WrapUp(3)], session.Told);
        Assert.Equal("the session of #3 did not say how the task went · fake attach 3", result.Why);
        Assert.DoesNotContain("close 3", tracker.Did);
    }

    [Fact]
    public async Task ATrackerThatRefusesAfterASessionWasStartedStopsItAndTheNextRunGoesOnWithIt()
    {
        tracker.Add(5, labels: ["bug", "interrupted"]);
        tracker.Refused.Add("label 5 -interrupted");
        var session = assistant.Session(5).Lost("I was about to").Then().Works().Ends(Done);

        var result = await RunAsync();

        var why = "The tracker refused: label 5 -interrupted. · the session of #5 is stopped, and the next run goes on with it · fake attach 5";
        Assert.Equal(new RunResult(RunEnd.Problem, 0, why), result);
        Assert.Equal(1, session.Stopped);
        Assert.Equal(0, session.Looks);
        Assert.Contains(log.Lines, line => line.StartsWith($"✖ Stopped: {why} · ", StringComparison.Ordinal));
        Assert.Equal(Record(5), state.Record);

        tracker.Refused.Clear();
        run = null;
        result = await RunAsync();

        Assert.Equal(new RunResult(RunEnd.Emptied, 1), result);
        Assert.Single(assistant.Started);
        Assert.Equal([Briefing.AfterStop(5)], session.Told);
        Assert.Contains("→ #5 Task 5 — taken again · its session was stopped, and goes on · fake attach 5", log.Lines);
        AssertDid("status 5 InProgress", "assign 5", "status 5 InProgress", "assign 5", "label 5 -interrupted", "comment 5", "close 5", "status 5 Done");
        Assert.Null(state.Record);
    }

    [Fact]
    public async Task AKillWhileATaskIsLeftToTheOwnerDoesNotCutItAndTheRunStopsOverTheTask()
    {
        tracker.Add(3);
        assistant.Session(3).Works().Ends("The tests do not build.\nTOOBUSY: failed the tests do not build");
        tracker.Doing = what =>
        {
            if (what == "label 3 +needs-owner")
                kill.Cancel();
        };

        var result = await RunAsync();

        Assert.Equal(new RunResult(RunEnd.Problem, 0, "#3 failed · fake attach 3"), result);
        AssertDid("status 3 InProgress", "assign 3", "label 3 +needs-owner", "comment 3", "status 3 Todo");
        Assert.Contains("■ Killed while #3 was being settled, and the tracker was told how it went all the same", log.Lines);
    }

    [Fact]
    public async Task AKillInAPauseWhileTheOutcomeIsToldLeavesTheFailureOfTheCall()
    {
        tracker.Add(3);
        tracker.Unanswered["close"] = 1;
        tracker.Doing = what =>
        {
            if (what == "close 3")
                clock.At(clock.Now - FakeClock.Start + policy.RetryPauses[0] / 2, kill.Cancel);
        };

        var result = await RunAsync();

        // The close is not made again, and the task is left to the owner as over any tracker that does not answer.
        Assert.Equal(new RunResult(RunEnd.Problem, 0, "The tracker did not answer: close 3 (1)."), result);
        Assert.Single(tracker.Asked, asked => asked == "close 3");
        AssertDid("status 3 InProgress", "assign 3", "comment 3", "label 3 +needs-owner", "comment 3", "status 3 Todo");
        Assert.Contains("■ Killed while #3 was being settled, and the tracker was told how it went all the same", log.Lines);
    }

    [Fact]
    public async Task AKillBeforeTheRecordOfASettledTaskIsForgottenLeavesTheTaskToTheNextRun()
    {
        tracker.Add(3);
        var session = assistant.Session(3);
        session.Stopping = kill.Cancel;

        var result = await RunAsync();

        Assert.Equal(new RunResult(RunEnd.Killed, 0, "#3 was being settled, and the next run settles it · fake attach 3"), result);
        Assert.Contains("■ Killed while #3 was being settled: nothing of how it went is told yet, and the next run settles it · fake attach 3", log.Lines);
        AssertDid("status 3 InProgress", "assign 3");
        Assert.Equal(Record(3), state.Record);
        Assert.Equal(1, session.Stopped);
        Assert.Equal([(3, RunMark.Interrupted)], Ended);

        // The working copy is read before the task, and not after its session.
        Assert.Equal(1, workspace.Read);
    }

    [Fact]
    public async Task WhileTheSessionsCannotBeReadTheSessionOfTheRecordIsLeftAsItIs()
    {
        state.Record = Record(5);
        tracker.Add(5, status: BoardStatus.InProgress);
        var session = assistant.Session(5).Unseen();

        var result = await RunAsync();

        Assert.Equal("the sessions of the assistant cannot be read, so the session of #5 is left as it is · fake attach 5 · the next run looks again", result.Why);
        Assert.Equal(Record(5), state.Record);
        Assert.Empty(session.Told);
        Assert.Empty(tracker.Did);
    }
}
