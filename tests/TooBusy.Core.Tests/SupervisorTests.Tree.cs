using TooBusy.Core.Run;

namespace TooBusy.Core.Tests;

// The working tree before and after a task: changes and commits that a session leaves.
public sealed partial class SupervisorTests
{
    [Fact]
    public async Task ATreeThatCannotBeReadStopsTheRun()
    {
        tracker.Add(3);
        workspace.State = null;

        Assert.Equal("the working tree cannot be read", (await RunAsync()).Why);
    }

    [Fact]
    public async Task ChangesLeftAfterATaskStopTheRunAndTheTaskIsLeftToTheOwner()
    {
        tracker.Add(3);
        tracker.Add(5);
        workspace.Next.Enqueue(new WorkspaceState(0, 0));
        workspace.State = new WorkspaceState(1, 0);

        var result = await RunAsync();

        Assert.Equal("#3 left changes in the working tree · fake attach 3", result.Why);
        Assert.Null(state.Record);
        Assert.Equal(["status 3 InProgress", "assign 3", "label 3 +needs-owner", "comment 3", "status 3 Todo"], tracker.Did);
        Assert.Equal(
            (3, Briefing.Stopped("its session left changes in the working tree", "Did it.", "fake attach 3", "needs-owner")),
            Assert.Single(tracker.Comments));
        Assert.Contains("  #3 has the needs-owner label and a comment that says what went wrong; no run takes it while it has the label", log.Lines);
        Assert.Equal([3], assistant.Started.Select(start => start.Task));
        Assert.Equal([(3, RunMark.Failed)], Ended);
    }

    [Fact]
    public async Task CommitsThatAreNotPushedStopTheRun()
    {
        tracker.Add(3);
        workspace.State = new WorkspaceState(0, 2);

        var result = await RunAsync();

        Assert.Equal("#3 left 2 commits that are not pushed · fake attach 3", result.Why);
        Assert.Equal(["status 3 InProgress", "assign 3", "label 3 +needs-owner", "comment 3", "status 3 Todo"], tracker.Did);
        Assert.StartsWith("**Failed.** The run stopped over this task: its session left 2 commits that are not pushed.", tracker.Comments[0].Text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ATreeThatCannotBeReadAfterATaskLeavesItToTheOwner()
    {
        tracker.Add(3);
        workspace.Next.Enqueue(new WorkspaceState(0, 0));
        workspace.State = null;

        var result = await RunAsync();

        Assert.Equal("the working tree cannot be read after #3 · fake attach 3", result.Why);
        Assert.Contains("label 3 +needs-owner", tracker.Did);
        Assert.StartsWith("**Failed.** The run stopped over this task: the working tree could not be read after its session.", tracker.Comments[0].Text, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("Did the half. What is left is #41, with the questions for the owner.")]
    public async Task ATaskDoneInPartIsClosedWithItsReportAndTheRunMakesNothing(string report)
    {
        tracker.Add(3, labels: ["Feature", "urgent"], milestone: "v0.2.0");
        assistant.Session(3).Works().Ends($"{report}\nTOOBUSY: partial");

        var result = await RunAsync();

        Assert.Equal(new RunResult(RunEnd.Emptied, 0) { Partial = 1 }, result);
        Assert.Equal(["status 3 InProgress", "assign 3", "comment 3", "close 3", "status 3 Done"], tracker.Did);
        Assert.Equal((3, $"**Done in part.** What is left is a task that the session made: the report names it.\n\n{report}\n\n_Session: `fake attach 3`_"), Assert.Single(tracker.Comments));
        Assert.Contains("◐ #3 Task 3 — done in part in 3 s", log.Lines);
        Assert.Contains("  #3 is closed, with the report of the session as its comment; what is left is a task that the session made", log.Lines);
    }

    [Fact]
    public async Task TheMachineStaysAwakeWhileTheRunLasts()
    {
        tracker.Add(3);
        var awake = false;
        assistant.Session(3).Works(and: () => awake = machine.Awake).Ends(Done);

        await RunAsync();

        Assert.True(awake);
        Assert.False(machine.Awake);
        Assert.Equal(1, machine.Kept);
    }
}
