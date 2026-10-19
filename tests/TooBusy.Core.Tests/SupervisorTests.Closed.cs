using TooBusy.Core.Queue;
using TooBusy.Core.Run;

namespace TooBusy.Core.Tests;

// A task that is closed while its session works or before a run settles it.
public sealed partial class SupervisorTests
{
    [Theory]
    [InlineData("Which key is it?\nTOOBUSY: owner", RunEnd.Emptied, "**Waits for the owner.** Nothing could be done without you. The task was closed before its session ended, and it is left closed: open it again when this is answered, and a run takes it.\n\nWhich key is it?\n\n_Session: `fake attach 3`_")]
    [InlineData("I tried twice.\nTOOBUSY: failed the tests do not pass", RunEnd.Problem, "**Failed.** The run stopped over this task: its session failed: the tests do not pass. The task was closed before its session ended, and it is left closed: look at what went wrong, open it again, and a run takes it.\n\nI tried twice.\n\n_Session: `fake attach 3`_")]
    [InlineData("Nothing is left.\nTOOBUSY: interrupted", RunEnd.Problem, "**Interrupted.** Nothing is committed. The task was closed before its session ended, and it is left closed: open it again, and a run takes it.\n\nNothing is left.\n\n_Session: `fake attach 3`_")]
    public async Task AClosedTaskThatIsNotDoneGetsItsReportAndIsNotPutBackAmongThoseToDo(string reply, RunEnd end, string comment)
    {
        tracker.Add(3);
        assistant.Session(3).Works(and: () => tracker.Shut(3)).Ends(reply);

        var result = await RunAsync();

        // Neither a label nor a status: the task is closed, and the run does not open it again.
        Assert.Equal(end, result.End);
        Assert.Equal(["status 3 InProgress", "assign 3", "comment 3"], tracker.Did);
        Assert.Equal((3, comment), Assert.Single(tracker.Comments));
        Assert.Contains(log.Lines, line => line.Contains("#3 was closed before its session ended, and is left closed", StringComparison.Ordinal));
        Assert.Null(state.Record);
        Assert.Equal(["feature"], Assert.Single(tracker.Closed).Labels);
    }

    [Fact]
    public async Task AClosedTaskWhoseSessionLeftChangesGetsTheCommentOnlyAndStopsTheRun()
    {
        tracker.Add(3);
        assistant.Session(3).Works(and: () => tracker.Shut(3)).Ends(Done);
        workspace.Next.Enqueue(new WorkspaceState(0, 0));
        workspace.State = new WorkspaceState(1, 0);

        var result = await RunAsync();

        Assert.Equal("#3 left changes in the working tree · fake attach 3", result.Why);
        Assert.Equal(["status 3 InProgress", "assign 3", "comment 3"], tracker.Did);
        Assert.StartsWith("**Failed.** The run stopped over this task: its session left changes in the working tree. The task was closed before its session ended, and it is left closed:", tracker.Comments[0].Text, StringComparison.Ordinal);
        Assert.Contains("  #3 was closed before its session ended, and is left closed, with a comment that says what went wrong; no run takes it until it is opened again", log.Lines);
    }

    [Fact]
    public async Task ATrackerThatDoesNotSayWhetherATaskIsClosedLeavesItAsOneThatIsOpen()
    {
        tracker.Add(3);
        tracker.Refused.Add("look");
        assistant.Session(3).Works().Ends("Which key is it?\nTOOBUSY: owner");

        var result = await RunAsync();

        Assert.Equal(new RunResult(RunEnd.Emptied, 0), result);
        Assert.Equal(["status 3 InProgress", "assign 3", "label 3 +needs-owner", "comment 3", "status 3 Todo"], tracker.Did);
    }

    [Fact]
    public async Task ATaskOfTheRecordThatWasClosedIsSettledWhenItsSessionEndedWithALine()
    {
        // The session pushed a commit that closed its task, and the run was killed before it settled the task.
        state.Record = Record(5);
        tracker.Add(3);
        tracker.Add(5, status: BoardStatus.InProgress);
        tracker.Shut(5);
        var session = assistant.Session(5).Ends("All of it.\nTOOBUSY: done");

        var result = await RunAsync();

        Assert.Equal(new RunResult(RunEnd.Emptied, 2), result);
        Assert.Equal([5], tracker.Looked);
        Assert.Empty(session.Told);
        Assert.Equal(1, session.Stopped);

        // The task is not taken again: it is not moved, nobody is assigned, and it is not closed once more.
        Assert.Equal(["comment 5", "status 5 Done"], tracker.Did.Take(2));
        Assert.Equal((5, "**Done.**\n\nAll of it.\n\n_Session: `fake attach 5`_"), tracker.Comments[0]);
        Assert.Equal([3], assistant.Started.Select(start => start.Task));
        Assert.Equal([(5, RunMark.Done), (3, RunMark.Done)], Ended);
        Assert.Null(state.Record);
        Assert.Equal(
            """
            ✻ v0.2.0 · 1 task in the queue · the assistant's own model · high effort · auto mode
            → #5 Task 5 — taken again · it was closed since, and its session ended while nobody watched · fake attach 5
            ✔ #5 Task 5 — done in 0 s
              #5 was closed already, and has the report of the session as its comment
            """.ReplaceLineEndings("\n"),
            string.Join('\n', log.Lines.Take(4)));
    }

    [Fact]
    public async Task AClosedTaskOfTheRecordIsCheckedAgainstTheWorkingCopyAsAnyOther()
    {
        state.Record = Record(5);
        tracker.Add(3);
        tracker.Add(5, status: BoardStatus.InProgress);
        tracker.Shut(5);
        assistant.Session(5).Ends("All of it.\nTOOBUSY: done");
        workspace.State = new WorkspaceState(0, 2);

        var result = await RunAsync();

        Assert.Equal("#5 left 2 commits that are not pushed · fake attach 5", result.Why);
        Assert.Equal(["comment 5"], tracker.Did);
        Assert.Empty(assistant.Started);
        Assert.Null(state.Record);
    }

    [Theory]
    [InlineData("Half of it; the rest is #9.\nTOOBUSY: partial", RunEnd.Emptied, new[] { "comment 5", "status 5 Done" })]
    [InlineData("Which key is it?\nTOOBUSY: owner", RunEnd.Emptied, new[] { "comment 5" })]
    [InlineData("No way.\nTOOBUSY: failed the tests do not pass", RunEnd.Problem, new[] { "comment 5" })]
    [InlineData("Nothing is left.\nTOOBUSY: interrupted", RunEnd.Problem, new[] { "comment 5" })]
    public async Task AClosedTaskOfTheRecordIsSettledWhateverItsSessionSaid(string reply, RunEnd end, string[] did)
    {
        state.Record = Record(5);
        tracker.Add(5, status: BoardStatus.InProgress);
        tracker.Shut(5);
        assistant.Session(5).Lost(reply);

        var result = await RunAsync();

        Assert.Equal(end, result.End);
        Assert.Equal(did, tracker.Did);
        Assert.Null(state.Record);
    }

    [Theory]
    [InlineData(RecordState.Working, false, "it is closed, and its session did not say how the task went")]
    [InlineData(RecordState.Working, true, "it is closed, and nothing is left of its session")]
    [InlineData(RecordState.WrappedUp, false, "it is closed")]
    public async Task AClosedTaskOfTheRecordWhoseSessionDidNotComeToALineIsForgotten(RecordState left, bool gone, string why)
    {
        // A session that wrapped up did say `interrupted`, and its task was settled then: it is not settled again.
        state.Record = Record(5, left);
        tracker.Add(3);
        tracker.Add(5, status: BoardStatus.InProgress);
        tracker.Shut(5);
        assistant.Gone = gone;
        var session = assistant.Session(5);
        if (left == RecordState.WrappedUp)
            session.Ends("Nothing is left.\nTOOBUSY: interrupted");
        else
            session.Works(3).Ends(Done);

        var result = await RunAsync();

        Assert.Equal(new RunResult(RunEnd.Emptied, 1), result);
        Assert.Null(state.Record);
        Assert.Equal(gone ? 0 : 1, session.Stopped);
        Assert.DoesNotContain(tracker.Did, did => did.EndsWith(" 5", StringComparison.Ordinal) || did.Contains(" 5 ", StringComparison.Ordinal));
        Assert.Equal($"▲ #5 was left with a session by an earlier run, but it is not gone on with: {why} · its session is stopped and forgotten", log.Lines[0]);
    }

    [Fact]
    public async Task WhileTheSessionsCannotBeReadAClosedTaskOfTheRecordIsLeftAsItIs()
    {
        state.Record = Record(5);
        tracker.Add(5, status: BoardStatus.InProgress);
        tracker.Shut(5);
        var session = assistant.Session(5).Unseen();

        var result = await RunAsync();

        Assert.Equal("the sessions of the assistant cannot be read, so the session of #5 is left as it is · fake attach 5 · the next run looks again", result.Why);
        Assert.Equal(Record(5), state.Record);
        Assert.Equal(0, session.Stopped);
        Assert.Empty(tracker.Did);
    }

    [Fact]
    public async Task ATrackerThatDoesNotSayWhetherTheTaskOfTheRecordIsClosedStopsTheRunWithTheRecordKept()
    {
        state.Record = Record(5);
        tracker.Add(3);
        tracker.Refused.Add("look");

        var result = await RunAsync();

        Assert.Equal("The tracker refused: look 5.", result.Why);
        Assert.Equal(Record(5), state.Record);
        Assert.Empty(assistant.Started);
    }

    [Fact]
    public async Task TheCommandAfterATaskRunsForAClosedTaskThatIsSettled()
    {
        plan = plan with { Commands = Around };
        state.Record = Record(5);
        tracker.Add(5, status: BoardStatus.InProgress);
        tracker.Shut(5);
        assistant.Session(5).Ends(Done);

        await RunAsync();

        Assert.Equal([(Halt, "5")], shell.Ran.Select(ran => (ran.Command, ran.Task)));
    }

    [Fact]
    public async Task ARunOfOneTaskIsNotStartedOverAClosedTaskThatIsToBeSettled()
    {
        plan = plan with { Only = 3 };
        state.Record = Record(5);
        tracker.Add(3);
        tracker.Add(5, status: BoardStatus.InProgress);
        tracker.Shut(5);
        assistant.Session(5).Ends(Done);

        var result = await RunAsync();

        Assert.Equal(new RunResult(RunEnd.Problem, 0, "#3 is not taken: an earlier run left #5 with a session, and a run settles that task before any other"), result);
        Assert.Empty(tracker.Did);
        Assert.Equal(Record(5), state.Record);
    }

    [Fact]
    public async Task ARunOfOneTaskSettlesThatTaskWhenItIsClosedAndEndsAsOneThatItsOwnerStopped()
    {
        plan = plan with { Only = 5 };
        state.Record = Record(5);
        tracker.Add(3);
        tracker.Add(5, status: BoardStatus.InProgress);
        tracker.Shut(5);
        assistant.Session(5).Ends(Done);

        var result = await RunAsync();

        Assert.Equal(new RunResult(RunEnd.Stopped, 1, "Stopped after #5, as you asked"), result);
        Assert.Empty(assistant.Started);
    }

    [Fact]
    public async Task AClosedTaskThatIsSettledCountsAsOneOfARunOfANumberOfTasks()
    {
        plan = plan with { Most = 2 };
        state.Record = Record(5);
        tracker.Add(3);
        tracker.Add(7);
        tracker.Add(5, status: BoardStatus.InProgress);
        tracker.Shut(5);
        assistant.Session(5).Ends(Done);

        var result = await RunAsync();

        Assert.Equal(new RunResult(RunEnd.Stopped, 2, "Stopped after 2 tasks, as you asked"), result);
        Assert.Equal([(5, RunMark.Done), (3, RunMark.Done)], Ended);
    }

    [Theory]
    [InlineData("needs-owner", "it waits for the owner: it has the needs-owner label")]
    public async Task ATaskOfTheRecordThatGotALabelThatKeepsItOutIsNotTaken(string label, string why)
    {
        state.Record = Record(5, RecordState.Paused);
        tracker.Add(3);
        tracker.Add(5, labels: ["feature", label]);
        workspace.State = new WorkspaceState(1, 0);

        var result = await RunAsync();

        // Its changes are nobody's now: the run stops over them, as over any others.
        Assert.Equal("the working tree is not clean: commit or stash the changes first", result.Why);
        Assert.Null(state.Record);
        Assert.Empty(assistant.Started);
        Assert.Empty(tracker.Did);
        Assert.Equal($"▲ #5 was left with a session by an earlier run, but it is not gone on with: {why} · its session is stopped and forgotten, and its changes are still in the working tree", log.Lines[0]);
    }
}
