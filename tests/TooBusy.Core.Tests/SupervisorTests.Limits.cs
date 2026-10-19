using TooBusy.Core.Run;

namespace TooBusy.Core.Tests;

// The usage limits: read with every look, waited out inside a task, and a pause for the next run.
public sealed partial class SupervisorTests
{
    [Fact]
    public async Task AWindowWhoseResetHasPassedIsNotKnown()
    {
        tracker.Add(3);
        assistant.Limits = new UsageLimits(new UsageWindow("5-hour", 100, FakeClock.Start - TimeSpan.FromMinutes(1)), new UsageWindow("weekly", 100, FakeClock.Start));

        var result = await RunAsync();

        Assert.Equal(new RunResult(RunEnd.Emptied, 1), result);
        Assert.All(log.Statuses, status => Assert.Equal(UsageLimits.Unknown, status.Limits));
    }

    [Fact]
    public async Task TheLimitsAreReadWithEveryLookAtTheSession()
    {
        tracker.Add(3);
        var before = new UsageLimits(new UsageWindow("5-hour", 2, FakeClock.Start + TimeSpan.FromHours(4)), new UsageWindow("weekly", 3, FakeClock.Start + TimeSpan.FromDays(5)));
        var now = new UsageLimits(new UsageWindow("5-hour", 11, FakeClock.Start + TimeSpan.FromHours(4)), new UsageWindow("weekly", 4, FakeClock.Start + TimeSpan.FromDays(5)));
        assistant.Limits = before;
        assistant.Session(3)
            .Works()
            .Works(and: () => assistant.Limits = now)
            .Works()
            .Ends(Done);

        await RunAsync();

        Assert.Equal(before, log.Statuses.First(status => status is { Phase: RunPhase.Preparing, Text: "Starting #3…" }).Limits);
        Assert.Equal([before, now], log.Statuses.Where(status => status.Phase == RunPhase.Working).Select(status => status.Limits).Distinct());

        // After the task the limits are those read last.
        Assert.Equal(now, log.Statuses[^1].Limits);
    }

    [Fact]
    public async Task AWindowWhoseResetPassesWhileTheSessionWorksIsNotOnTheStatusFromThen()
    {
        tracker.Add(3);
        var far = new UsageWindow("weekly", 4, FakeClock.Start + TimeSpan.FromDays(5));
        assistant.Limits = new UsageLimits(new UsageWindow("5-hour", 11, FakeClock.Start + TimeSpan.FromMinutes(1)), far);
        assistant.Session(3).Works(looks: 30).Ends(Done);

        await RunAsync();

        Assert.Equal(
            [new UsageLimits(new UsageWindow("5-hour", 11, FakeClock.Start + TimeSpan.FromMinutes(1)), far), new UsageLimits(null, far)],
            log.Statuses.Where(status => status.Phase == RunPhase.Working).Select(status => status.Limits).Distinct());
    }

    [Fact]
    public async Task TheLimitsAreReadWhileTheSessionsCannotBe()
    {
        tracker.Add(3);
        var now = new UsageLimits(new UsageWindow("5-hour", 11, null), null);
        assistant.Session(3).Works().Unseen().Unseen(and: () => assistant.Limits = now).Unseen().Works().Ends(Done);

        await RunAsync();

        Assert.Contains(log.Statuses, status => status is { Phase: RunPhase.Working, Step: "its session cannot be read" } && status.Limits == now);
    }

    [Fact]
    public async Task ALimitInsideATaskIsWaitedOutAndTheSameSessionGoesOn()
    {
        tracker.Add(3);
        var session = assistant.Session(3)
            .Works(and: () => assistant.Limits = new UsageLimits(new UsageWindow("5-hour", 100, FakeClock.Start + TimeSpan.FromHours(1)), null))
            .Limit("You have hit your limit · resets 10am\nmore")
            .Then().Works().Ends(Done);

        var result = await RunAsync();

        Assert.Equal(new RunResult(RunEnd.Emptied, 1), result);
        Assert.Equal([Briefing.AfterLimit(3)], session.Told);
        Assert.Equal(
            [
                "‖ #3 ran into a usage limit: You have hit your limit · resets 10am",
                "‖ #3 waits 1 h 00 min for the 5-hour limit to reset, then goes on · /stop pauses it for the next run",
                "▶ #3 goes on after the reset of the limit · fake attach 3",
            ],
            log.Lines.Skip(3).Take(3));
        Assert.Null(state.Record);
        Assert.Contains(log.Statuses, status => status.Phase == RunPhase.WaitingForLimit && status.Task?.Number == 3 && status.Until == FakeClock.Start + TimeSpan.FromMinutes(61));
    }

    [Fact]
    public async Task ALimitThatDoesNotResetPausesTheTaskForTheNextRun()
    {
        policy = policy with { LimitWaits = 2 };
        tracker.Add(3);
        tracker.Add(5);
        var session = assistant.Session(3).Works().Limit().Then().Limit().Then().Limit();

        var result = await RunAsync();

        Assert.Equal(new RunResult(RunEnd.Limited, 0, "#3 is paused: the limit did not reset after 2 waits. Its changes stay in the working tree, and the next run goes on with its session"), result);
        Assert.Equal(2, session.Told.Count);
        Assert.Equal(["status 3 InProgress", "assign 3", "label 3 +interrupted", "status 3 Todo", "comment 3"], tracker.Did);
        Assert.Equal(Record(3, RecordState.Paused), state.Record);
        Assert.Equal([(3, RunMark.Paused)], Ended);
        Assert.StartsWith("**Stopped by a usage limit** (the limit did not reset after 2 waits).", tracker.Comments[0].Text, StringComparison.Ordinal);
        Assert.Contains("‖ #3 is paused: the limit did not reset after 2 waits. Its changes stay in the working tree, and the next run goes on with its session · 0 done", log.Lines);
        Assert.Contains("#3 is paused by a usage limit", machine.Notified);
        Assert.Equal([3], assistant.Started.Select(start => start.Task));
    }

    [Fact]
    public async Task AFarLimitWhoseResetHasPassedPausesNothing()
    {
        tracker.Add(3);
        assistant.Limits = new UsageLimits(null, new UsageWindow("weekly", 100, FakeClock.Start + TimeSpan.FromMinutes(1)));
        var session = assistant.Session(3).Works(looks: 30).Limit().Then().Works().Ends(Done);

        var result = await RunAsync();

        Assert.Equal(new RunResult(RunEnd.Emptied, 1), result);
        Assert.Equal([Briefing.AfterLimit(3)], session.Told);
        Assert.Contains("‖ #3 waits 30 min for the limit to reset, then goes on · /stop pauses it for the next run", log.Lines);
    }

    [Fact]
    public async Task StopDuringTheWaitForALimitPausesTheTaskAtOnce()
    {
        tracker.Add(3);
        var session = assistant.Session(3).Works().Limit();
        clock.At(TimeSpan.FromMinutes(10), () => Run.Send(RunCommand.Stop));

        var result = await RunAsync();

        Assert.Equal(RunEnd.Limited, result.End);
        Assert.Empty(session.Told);
        Assert.Contains("‖ #3 waits for a limit: it is paused now, and the next run goes on with its session", log.Lines);
        Assert.Contains(log.Lines, line => line.StartsWith("‖ #3 is paused: toobusy was stopped while it waited for the limit.", StringComparison.Ordinal));
        Assert.Equal(Record(3, RecordState.Paused), state.Record);
    }

    [Fact]
    public async Task KillDuringTheWaitForALimitPausesTheTaskToo()
    {
        tracker.Add(3);
        assistant.Session(3)
            .Works(and: () => assistant.Limits = new UsageLimits(new UsageWindow("5-hour", 100, FakeClock.Start + TimeSpan.FromHours(1)), null))
            .Limit();
        clock.At(TimeSpan.FromMinutes(10), kill.Cancel);

        var result = await RunAsync();

        Assert.Equal(RunEnd.Killed, result.End);
        Assert.Equal(Record(3, RecordState.Paused), state.Record);
        Assert.Contains("label 3 +interrupted", tracker.Did);
        Assert.DoesNotContain(log.Lines, line => line.StartsWith("■ Killed", StringComparison.Ordinal));

        // The sign said what the run waited for, and the kill took it down.
        Assert.Contains(
            new RunSign(RunPhase.WaitingForLimit) { Task = 3, Title = "Task 3", Url = "https://example.com/3", Open = "fake attach 3", Until = FakeClock.Start + TimeSpan.FromMinutes(61) },
            sign.Written);
        Assert.Null(sign.Written[^1]);
    }

    [Fact]
    public async Task AKillWhileTheSessionIsStoppedForALimitPausesTheTask()
    {
        tracker.Add(3);
        var session = assistant.Session(3).Works().Limit();
        session.Stopping = kill.Cancel;

        var result = await RunAsync();

        Assert.Equal(RunEnd.Killed, result.End);
        Assert.StartsWith("#3 is paused: toobusy was stopped while it waited for the limit.", result.Why, StringComparison.Ordinal);
        Assert.Equal(["status 3 InProgress", "assign 3", "label 3 +interrupted", "status 3 Todo", "comment 3"], tracker.Did);
        Assert.Equal(Record(3, RecordState.Paused), state.Record);
        Assert.Equal([(3, RunMark.Paused)], Ended);

        // The stop for the wait was cut, and the pause stops the session again.
        Assert.Equal(1, session.Stopped);
        Assert.DoesNotContain(log.Lines, line => line.Contains("to reset, then goes on", StringComparison.Ordinal));
    }

    [Fact]
    public async Task AKillInTheMiddleOfAPauseDoesNotCutItAndEndsTheRun()
    {
        tracker.Add(3);
        tracker.Add(5);
        assistant.Session(3)
            .Works(and: () => assistant.Limits = new UsageLimits(null, new UsageWindow("weekly", 100, FakeClock.Start + TimeSpan.FromDays(1))))
            .Limit();
        tracker.Doing = what =>
        {
            if (what == "label 3 +interrupted")
                kill.Cancel();
        };

        var result = await RunAsync();

        Assert.Equal(RunEnd.Killed, result.End);
        Assert.StartsWith("#3 is paused: the weekly limit is spent", result.Why, StringComparison.Ordinal);
        Assert.Equal(["status 3 InProgress", "assign 3", "label 3 +interrupted", "status 3 Todo", "comment 3"], tracker.Did);
        Assert.Equal(Record(3, RecordState.Paused), state.Record);
        Assert.Single(assistant.Started);
    }

    [Fact]
    public async Task APauseThatIsNotWholeSaysWhatIsMissing()
    {
        tracker.Add(3);
        tracker.Refused.Add("comment");
        var session = assistant.Session(3).Works().Limit();
        session.Conversation = null;
        clock.At(TimeSpan.FromMinutes(10), () => Run.Send(RunCommand.Stop));

        await RunAsync();

        Assert.Equal(new TaskRecord(3, new SessionTrace("fake-3", null, FakeClock.Start), RecordState.Paused), state.Record);
        Assert.Contains("▲ The pause of #3 is not whole: The tracker refused: comment 3. · the conversation of its session is not known · set it right by hand before the next run", log.Lines);
    }

    [Fact]
    public async Task TheRecordIsWrittenWhenASessionStartsAndForgottenWhenTheTaskComesToAnOutcome()
    {
        tracker.Add(3);
        tracker.Add(5);
        tracker.Add(7);
        TaskRecord? atWork = null;
        assistant.Session(3).Works(and: () => atWork = state.Record).Ends(Done);
        assistant.Session(5).Works().Ends("Half of it; the rest is #9.\nTOOBUSY: partial");
        assistant.Session(7).Works().Ends("Which key?\nTOOBUSY: owner");

        await RunAsync();

        Assert.Equal(Record(3), atWork);
        Assert.Equal([Record(3), null, Record(5), null, Record(7), null], state.Written);
    }
}
