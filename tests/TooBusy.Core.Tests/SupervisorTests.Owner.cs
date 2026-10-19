using TooBusy.Core.Run;

namespace TooBusy.Core.Tests;

// A session that waits for the owner: the countdown, nudge and hold, and the messages that tell it to go on alone.
public sealed partial class SupervisorTests
{
    [Fact]
    public async Task ASessionMarkedAsWaitingWhoseReplyEndsWithTheLineIsToldOnceAndIsOverAtItsNextReply()
    {
        tracker.Add(3);
        var session = assistant.Session(3).Works().Asks("input needed", Half).Then().Asks("input needed", Half);

        var result = await RunAsync();

        Assert.Equal((RunEnd.Emptied, 0, 1), (result.End, result.Done, result.Partial));
        Assert.Equal([Briefing.NoAnswer(3, "needs-owner")], session.Told);
        Assert.Equal(
            [
                "▲ #3 ended its turn with a line for toobusy, but Claude Code marks it as waiting for an answer · it is told that none will come · fake attach 3",
                "  │ Half of it; the rest is #9.",
                $"  “{Briefing.NoAnswer(3, "needs-owner")}”",
                $"◐ #3 Task 3 — done in part in {Spoken.Time(2 * policy.Poll)}",
            ],
            log.Lines.Skip(3).Take(4));
        AssertDid("status 3 InProgress", "assign 3", "comment 3", "close 3", "status 3 Done");
        Assert.Equal([(3, RunMark.Partial)], Ended);

        // Nothing is asked of the owner: no countdown, no commands for a session that waits, and no notification.
        Assert.DoesNotContain(log.Statuses, status => status.Phase == RunPhase.WaitingForOwner);
        Assert.DoesNotContain("#3 waits for an answer", machine.Notified);
    }

    [Fact]
    public async Task ASessionThatWasToldToGoOnAloneIsOverAtOnceWhenItEndsWithTheLineMarkedAsWaiting()
    {
        tracker.Add(3);
        var session = assistant.Session(3).Works().Asks("input needed", "CSV or JSON?").Then().Works().Asks("input needed", "JSON it is.\nTOOBUSY: done");

        var result = await RunAsync();

        Assert.Equal(new RunResult(RunEnd.Emptied, 1), result);
        Assert.Equal([Briefing.Alone(3, "needs-owner")], session.Told);
        Assert.Contains($"✔ #3 Task 3 — done in {Spoken.Time(OwnerWaited)}", log.Lines);
        Assert.DoesNotContain(log.Lines, line => line.StartsWith("▲ #3 ended its turn with a line for toobusy", StringComparison.Ordinal));
    }

    [Fact]
    public async Task ASessionThatHasReportedAndDoesNotGoOnWithTheMessageIsSettledByItsReport()
    {
        tracker.Add(3);
        var session = assistant.Session(3).Works().Asks("input needed", Half);
        session.Deaf = "it is deaf";

        var result = await RunAsync();

        Assert.Equal(1, result.Partial);
        Assert.Single(session.Told);
        Assert.Contains("✖ #3 did not go on with the message: it is deaf", log.Lines);
        Assert.Contains("close 3", tracker.Did);
    }

    [Fact]
    public async Task AUsageLimitComesBeforeAReplyThatEndsWithTheLine()
    {
        tracker.Add(3);
        var session = assistant.Session(3).Works().Limit(phase: SessionPhase.Asking, reply: Half).Then().Works().Ends(Done);

        var result = await RunAsync();

        Assert.Equal(new RunResult(RunEnd.Emptied, 1), result);
        Assert.Equal([Briefing.AfterLimit(3)], session.Told);
        Assert.Contains("‖ #3 ran into a usage limit: You have hit your usage limit.", log.Lines);
        Assert.DoesNotContain(log.Lines, line => line.Contains("it is told that none will come", StringComparison.Ordinal));
    }

    [Fact]
    public async Task ATurnThatEndsWithoutSayingHowTheTaskWentIsToldToo()
    {
        tracker.Add(3);
        var session = assistant.Session(3).Works().Ends("Shall I go on?").Then().Works().Ends(Done);

        var result = await RunAsync();

        Assert.Equal(1, result.Done);
        Assert.Single(session.Told);
        Assert.Contains($"‖ #3 ended its turn without saying how the task went · it is told to go on alone in {Spoken.Time(policy.OwnerWait)}: /nudge does it now, /hold never · fake attach 3", log.Lines);
    }

    [Fact]
    public async Task ATurnThatEndsWithAQuestionAfterTheLineWaitsAsOneWithoutItDoes()
    {
        tracker.Add(3);
        var session = assistant.Session(3).Works().Ends($"{Done}\n\nShall I go on with the docs?").Then().Works().Ends(Done);

        var result = await RunAsync();

        Assert.Equal(1, result.Done);
        Assert.Equal([Briefing.Alone(3, Rules.Manual)], session.Told);
        Assert.Contains($"‖ #3 ended its turn without saying how the task went · it is told to go on alone in {Spoken.Time(policy.OwnerWait)}: /nudge does it now, /hold never · fake attach 3", log.Lines);
    }

    [Fact]
    public async Task NudgeTellsTheSessionAtOnce()
    {
        tracker.Add(3);
        var session = assistant.Session(3).Works().Asks().Then().Works().Ends(Done);
        clock.At(AfterLook(2), () => Run.Send(RunCommand.Nudge));

        await RunAsync();

        Assert.Single(session.Told);
        Assert.True(clock.Now - FakeClock.Start < policy.OwnerWait, "The session was told only after the countdown.");
    }

    [Fact]
    public async Task HoldCancelsTheCountdownAndTheSessionWaitsForTheOwner()
    {
        tracker.Add(3);
        var session = assistant.Session(3).Works().Asks().Then().Works().Ends(Done);
        var later = 10 * policy.OwnerWait;
        clock.At(AfterLook(2), () => Run.Send(RunCommand.Hold));
        clock.At(later, () => Run.Send(RunCommand.Nudge));

        await RunAsync();

        Assert.Single(session.Told);
        Assert.True(clock.Now - FakeClock.Start >= later, "The session was told while it was held.");
        Assert.Contains("‖ #3 gets no message and waits for the owner · /nudge tells it to go on alone · fake attach 3", log.Lines);
    }

    [Fact]
    public async Task ASessionThatGoesBackToWorkIsNotTold()
    {
        tracker.Add(3);
        var session = assistant.Session(3).Works().Asks().Works(2).Ends(Done);

        var result = await RunAsync();

        Assert.Equal(1, result.Done);
        Assert.Empty(session.Told);
    }

    [Fact]
    public async Task ATaskIsToldToGoOnAloneOnlySoManyTimes()
    {
        policy = policy with { Messages = 2 };
        tracker.Add(3);
        var session = assistant.Session(3).Works().Asks().Then().Works().Asks().Then().Works().Asks(what: null);
        clock.At(10 * policy.OwnerWait, kill.Cancel);

        var result = await RunAsync();

        Assert.Equal(RunEnd.Killed, result.End);
        Assert.Equal(2, session.Told.Count);
        Assert.Contains("‖ #3 waits for the owner: an answer is needed · it is left to the owner: it was told to go on alone 2 times · fake attach 3", log.Lines);
    }

    [Fact]
    public async Task MessagesThatBringNoStepOfItsOwnLeaveTheTaskToTheOwner()
    {
        tracker.Add(3);
        var session = assistant.Session(3).Works().Ends("What now?").Then().Ends("And now?").Then().Ends("Still here.");

        var result = await RunAsync();

        Assert.Equal(policy.IdleMessages, session.Told.Count);
        Assert.Equal("the session of #3 did not say how the task went · fake attach 3", result.Why);
        Assert.Contains($"‖ #3 ended its turn without saying how the task went · it is left to the owner: {policy.IdleMessages} messages in a row brought no step of its own · fake attach 3", log.Lines);
    }

    [Fact]
    public async Task ASessionThatDoesNotGoOnWithTheMessageIsLost()
    {
        tracker.Add(3);
        var session = assistant.Session(3).Works().Asks();
        session.Deaf = "it is deaf";

        var result = await RunAsync();

        Assert.Single(session.Told);
        Assert.Contains("✖ #3 did not go on with the message: it is deaf", log.Lines);
        Assert.Equal(RunEnd.Problem, result.End);
    }

    [Fact]
    public async Task StopFinishesTheTaskAndTakesNoMore()
    {
        tracker.Add(3);
        tracker.Add(5);
        assistant.Session(3).Works(4).Ends(Done);
        clock.At(AfterLook(2), () => Run.Send(RunCommand.Stop));

        var result = await RunAsync();

        Assert.Equal(new RunResult(RunEnd.Stopped, 1), result);
        Assert.Equal([3], assistant.Started.Select(start => start.Task));
        Assert.Contains("‖ Will stop after #3 · /continue takes tasks again", log.Lines);
        Assert.Contains("‖ Stopped by /stop · 1 done", log.Lines);
        Assert.Contains("Stopped: 1 done", machine.Notified);
    }
}
