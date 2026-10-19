using TooBusy.Core.Queue;
using TooBusy.Core.Run;

namespace TooBusy.Core.Tests;

// The record of a task, and the session of an earlier run that a run goes on with.
public sealed partial class SupervisorTests
{
    [Fact]
    public async Task AConversationThatIsKnownOnlyLaterIsWrittenWhenItIsKnown()
    {
        tracker.Add(3);
        var session = assistant.Session(3);
        session.Conversation = null;
        session.Works(2).Works(and: () => session.Conversation = "conversation-3").Works().Ends(Done);

        await RunAsync();

        Assert.Equal([new TaskRecord(3, new SessionTrace("fake-3", null, FakeClock.Start)), Record(3), null], state.Written);
    }

    [Fact]
    public async Task ASessionThatStillWorksIsTakenOverAndWatchedWhateverTheStatusOfItsTask()
    {
        state.Record = Record(5);
        tracker.Add(3);
        tracker.Add(5, status: BoardStatus.InProgress);
        var session = assistant.Session(5).Works(2).Ends(Done);

        var result = await RunAsync();

        Assert.Equal(new RunResult(RunEnd.Emptied, 2), result);
        Assert.Equal([Record(5).Session], assistant.Found);
        Assert.Empty(session.Told);
        Assert.Equal([3], assistant.Started.Select(started => started.Task));
        AssertDid(5, "status 5 InProgress", "assign 5", "comment 5", "close 5", "status 5 Done");
        Assert.Equal(
            [
                "✻ v0.2.0 · 2 tasks in the queue · the assistant's own model · high effort · auto mode",
                "→ #5 Task 5 — taken again · its session still works, and is watched again · fake attach 5",
            ],
            log.Lines.Take(2));
        Assert.Null(state.Record);
    }

    [Fact]
    public async Task ASessionThatEndedWithAnOutcomeWhileNobodyWatchedIsSettledAsUsual()
    {
        state.Record = Record(5);
        tracker.Add(5, status: BoardStatus.InProgress);
        var session = assistant.Session(5).Ends("Half of it; the rest is #9.\nTOOBUSY: partial");

        var result = await RunAsync();

        Assert.Equal(new RunResult(RunEnd.Emptied, 0) { Partial = 1 }, result);
        Assert.Equal(1, session.Looks);
        Assert.Empty(session.Told);
        Assert.Contains("→ #5 Task 5 — taken again · its session ended while nobody watched · fake attach 5", log.Lines);
        Assert.Contains("◐ #5 Task 5 — done in part in 0 s", log.Lines);
        AssertDid("status 5 InProgress", "assign 5", "comment 5", "close 5", "status 5 Done");
        Assert.Null(state.Record);
    }

    [Fact]
    public async Task ASessionThatFailedWhileNobodyWatchedStopsTheRunAndIsNotGoneOnWithAgain()
    {
        state.Record = Record(5);
        tracker.Add(5, status: BoardStatus.InProgress);
        assistant.Session(5).Lost("No way.\nTOOBUSY: failed the tests do not pass");

        var result = await RunAsync();

        Assert.Equal("#5 failed · fake attach 5", result.Why);
        Assert.Null(state.Record);
    }

    [Fact]
    public async Task ASessionThatWasStoppedGoesOnWithTheSameConversationOverItsChanges()
    {
        state.Record = Record(5);
        tracker.Add(3);
        tracker.Add(5, status: BoardStatus.InProgress);
        workspace.Next.Enqueue(new WorkspaceState(2, 0));
        var session = assistant.Session(5).Lost("I was about to").Then().Works().Ends(Done);

        var result = await RunAsync();

        Assert.Equal(new RunResult(RunEnd.Emptied, 2), result);
        Assert.Equal([Briefing.AfterStop(5)], session.Told);
        Assert.Equal([3], assistant.Started.Select(started => started.Task));
        Assert.Contains("→ #5 Task 5 — taken again · its session was stopped, and goes on · fake attach 5", log.Lines);
        Assert.Contains($"✔ #5 Task 5 — done in {Spoken.Time(policy.Poll)}", log.Lines);
    }

    [Fact]
    public async Task ASessionThatALimitStoppedWhileNobodyWatchedIsToldThatItHasReset()
    {
        state.Record = Record(5);
        tracker.Add(5, status: BoardStatus.InProgress);
        var session = assistant.Session(5).Limit().Then().Works().Ends(Done);

        await RunAsync();

        Assert.Equal([Briefing.AfterLimit(5)], session.Told);
        Assert.Contains("→ #5 Task 5 — taken again · its session goes on after the usage limit · fake attach 5", log.Lines);
    }

    [Fact]
    public async Task ASessionThatWaitsForTheOwnerIsTakenOverAndToldToGoOnAlone()
    {
        state.Record = Record(5);
        tracker.Add(5, status: BoardStatus.InProgress);
        var session = assistant.Session(5).Asks().Then().Works().Ends(Done);

        var result = await RunAsync();

        Assert.Equal(1, result.Done);
        Assert.Equal([Briefing.Alone(5, "needs-owner")], session.Told);
        Assert.Contains("→ #5 Task 5 — taken again · its session still works, and is watched again · fake attach 5", log.Lines);
    }

    [Fact]
    public async Task ASessionOfAnEarlierRunMarkedAsWaitingAfterItsReportIsToldOnceAndIsOverAtItsNextReply()
    {
        state.Record = Record(5);
        tracker.Add(5, status: BoardStatus.InProgress);
        var session = assistant.Session(5).Asks("input needed", Half).Then().Asks("input needed", Half);

        var result = await RunAsync();

        Assert.Equal(1, result.Partial);
        Assert.Equal([Briefing.NoAnswer(5, "needs-owner")], session.Told);
        Assert.Contains("→ #5 Task 5 — taken again · its session still works, and is watched again · fake attach 5", log.Lines);
        Assert.Contains("▲ #5 ended its turn with a line for toobusy, but Claude Code marks it as waiting for an answer · it is told that none will come · fake attach 5", log.Lines);
        Assert.Null(state.Record);
    }

    [Fact]
    public async Task ThePausedTaskGoesFirstWithItsSessionAndItsChanges()
    {
        state.Record = Record(5, RecordState.Paused);
        tracker.Add(3);
        tracker.Add(5, labels: ["feature", "interrupted"]);
        workspace.Next.Enqueue(new WorkspaceState(2, 0));

        var result = await RunAsync();

        Assert.Equal(new RunResult(RunEnd.Emptied, 2), result);
        Assert.Equal([Briefing.AfterLimit(5)], assistant.Session(5).Told);
        Assert.Equal([3], assistant.Started.Select(started => started.Task));
        AssertDid(5, "status 5 InProgress", "assign 5", "label 5 -interrupted", "comment 5", "close 5", "status 5 Done");
        Assert.Null(state.Record);
        Assert.Contains("→ #5 Task 5 — taken again · its session goes on after the usage limit · fake attach 5", log.Lines);
    }

    [Fact]
    public async Task AfterAnAbortTheNextRunGoesOnWithTheSameConversation()
    {
        state.Record = Record(5, RecordState.WrappedUp);
        tracker.Add(3);
        tracker.Add(5, labels: ["feature", "interrupted"]);
        var session = assistant.Session(5);

        var result = await RunAsync();

        Assert.Equal(new RunResult(RunEnd.Emptied, 2), result);
        Assert.Equal([Briefing.AfterAbort(5)], session.Told);
        Assert.Equal([3], assistant.Started.Select(started => started.Task));
        AssertDid(5, "status 5 InProgress", "assign 5", "label 5 -interrupted", "comment 5", "close 5", "status 5 Done");
        Assert.Contains("→ #5 Task 5 — taken again · its session goes on after /abort · fake attach 5", log.Lines);
    }

    [Fact]
    public async Task AfterAnAbortANewSessionStartsWithTheReportOnlyWhenTheConversationIsGone()
    {
        state.Record = Record(5, RecordState.WrappedUp);
        tracker.Add(5, labels: ["feature", "interrupted"]);
        assistant.Gone = true;

        var result = await RunAsync();

        Assert.Equal(1, result.Done);
        var start = Assert.Single(assistant.Started);
        Assert.Contains("- This task was interrupted before.", start.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("its conversation is gone", start.Message, StringComparison.Ordinal);
        Assert.Contains("→ #5 Task 5 — taken again · a new session: the conversation of the one before is gone · fake attach 5", log.Lines);
        Assert.Equal([Record(5), null], state.Written);
    }

    [Theory]
    [InlineData(RecordState.Working)]
    [InlineData(RecordState.Paused)]
    public async Task ASessionWhoseConversationIsGoneGivesANewOneThatIsToldWhatItFinds(RecordState left)
    {
        state.Record = Record(5, left);
        tracker.Add(5, labels: left == RecordState.Paused ? ["feature", "interrupted"] : null, status: left == RecordState.Paused ? BoardStatus.Todo : BoardStatus.InProgress);
        workspace.Next.Enqueue(new WorkspaceState(2, 0));
        assistant.Gone = true;

        var result = await RunAsync();

        Assert.Equal(1, result.Done);
        var start = Assert.Single(assistant.Started);
        Assert.Contains("- A session worked on this task before, and its conversation is gone: it left no report.", start.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("- This task was interrupted before.", start.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ATaskOfTheRecordThatIsNotAmongTheTasksIsNotTakenAndTheRecordIsForgotten()
    {
        state.Record = Record(5);
        tracker.Add(3);

        var result = await RunAsync();

        Assert.Equal(new RunResult(RunEnd.Emptied, 1), result);
        Assert.Null(state.Record);
        Assert.Equal(1, assistant.Session(5).Stopped);
        Assert.Equal([3], assistant.Started.Select(started => started.Task));
        Assert.Equal("▲ #5 was left with a session by an earlier run, but it is not gone on with: it is not among the open tasks · its session is stopped and forgotten", log.Lines[0]);
    }

    [Fact]
    public async Task ATaskThatACommitOfItsSessionClosedIsSettledAllTheSame()
    {
        // The push of a commit with `Closes #3` closes the task while its session still works.
        tracker.Add(3);
        tracker.Add(5);
        assistant.Session(3).Works(and: () => tracker.Shut(3)).Ends(Done);

        var result = await RunAsync();

        Assert.Equal(new RunResult(RunEnd.Emptied, 2), result);
        AssertDid(3, "status 3 InProgress", "assign 3", "comment 3", "close 3", "status 3 Done");
        Assert.Equal((3, "**Done.**\n\nDid it.\n\n_Session: `fake attach 3`_"), tracker.Comments[0]);
        Assert.Contains($"✔ #3 Task 3 — done in {Spoken.Time(policy.Poll)}", log.Lines);
        Assert.Equal([(3, RunMark.Done), (5, RunMark.Done)], Ended);

        // A task that is done is not asked for: it is closed, whoever closed it.
        Assert.Empty(tracker.Looked);
    }

    [Fact]
    public async Task ASessionThatDoesNotGoOnIsLeftForTheNextRun()
    {
        state.Record = Record(5, RecordState.Paused);
        tracker.Add(5, labels: ["feature", "interrupted"]);
        workspace.State = new WorkspaceState(1, 0);
        assistant.Session(5).Deaf = "it is deaf";

        var result = await RunAsync();

        Assert.Equal("the session of #5 did not go on: it is deaf · fake attach 5 · the next run tries again", result.Why);
        Assert.Equal(Record(5, RecordState.Paused), state.Record);
        Assert.Empty(tracker.Did);
    }

    [Fact]
    public async Task ChangesInTheTreeAreNotThoseOfASessionThatWrappedUp()
    {
        state.Record = Record(5, RecordState.WrappedUp);
        tracker.Add(5, labels: ["feature", "interrupted"]);
        workspace.State = new WorkspaceState(1, 0);

        var result = await RunAsync();

        Assert.Equal("the working tree is not clean: commit or stash the changes first", result.Why);
        Assert.Equal(Record(5, RecordState.WrappedUp), state.Record);
        Assert.Empty(assistant.Found);
    }
}
