using TooBusy.Core.Assistant;
using TooBusy.Core.Queue;
using TooBusy.Core.Run;

namespace TooBusy.Core.Tests;

// Which tasks a run takes, in what order, and which it holds.
public sealed partial class SupervisorTests
{
    [Fact]
    public async Task ATaskThatOpensAfterAnotherIsTakenOnceThatOneIsClosed()
    {
        tracker.Add(3, blockedBy: [5]);
        tracker.Add(5);

        await RunAsync();

        Assert.Equal([5, 3], assistant.Started.Select(start => start.Task));
        Assert.Contains("✻ v0.2.0 · 2 tasks in the queue · the assistant's own model · high effort · auto mode", log.Lines);
        Assert.Contains("  Opens later: #3 after #5", log.Lines);
    }

    [Fact]
    public async Task TasksThatAreHeldAreNamedAndNotTaken()
    {
        tracker.Add(3, labels: ["feature", "manual"]);
        tracker.Add(4, status: BoardStatus.InProgress);
        tracker.Add(5);

        var result = await RunAsync();

        Assert.Equal([5], assistant.Started.Select(start => start.Task));
        Assert.Contains("  Held: #3 it has the manual label · #4 its status is InProgress", log.Lines);
        Assert.Equal(RunEnd.Emptied, result.End);
        Assert.Equal("✔ No task is left · 1 done", log.Lines[^2]);
    }

    [Fact]
    public async Task ARunThatSkipsLabelsNamesThemAndHoldsTheirTasksWithThoseThatWaitForThemForTheWholeRun()
    {
        plan = plan with { Rules = Rules with { Skip = ["design", "sketch"] } };
        tracker.Add(3, labels: ["feature", "Design"]);
        tracker.Add(4, blockedBy: [3]);
        tracker.Add(5);

        // The queue is read anew before every task, and the skip holds there too.
        assistant.Session(5).Works(and: () => tracker.Add(6, labels: ["bug", "sketch"])).Ends(Done);

        var result = await RunAsync();

        Assert.Equal(new RunResult(RunEnd.Emptied, 1), result);
        Assert.Equal([5], assistant.Started.Select(start => start.Task));
        Assert.Equal("✻ v0.2.0 · 1 task in the queue · skips design, sketch · the assistant's own model · high effort · auto mode", log.Lines[0]);
        Assert.Equal("  Held: #3 it has the Design label, which this run skips · #4 it waits for #3 (it has the Design label, which this run skips)", log.Lines[1]);
        Assert.Contains("  Held: #3 it has the Design label, which this run skips · #4 it waits for #3 (it has the Design label, which this run skips) · #6 it has the sketch label, which this run skips", log.Lines);
        Assert.All(tracker.Did, did => Assert.Equal("5", did.Split(' ')[1]));
    }

    [Fact]
    public async Task ATaskDoneInPartIsCountedApartFromThoseThatAreDone()
    {
        tracker.Add(3);
        tracker.Add(5);
        tracker.Add(7);
        assistant.Session(3).Works().Ends(Done);
        assistant.Session(5).Works().Ends("Half of it; the rest is #9.\nTOOBUSY: partial");
        assistant.Session(7).Works().Ends(Done);

        var result = await RunAsync();

        Assert.Equal(new RunResult(RunEnd.Emptied, 2) { Partial = 1 }, result);
        Assert.Equal("✔ No task is left · 2 done · 1 done in part", log.Lines[^2]);
        Assert.Contains("No task is left: 2 done · 1 done in part", machine.Notified);
    }

    [Fact]
    public async Task WithoutAMilestoneTheTasksAreReadWhateverTheirs()
    {
        plan = plan with { Milestone = null, Rules = Rules with { Board = false }, Model = new ModelChoice("opus"), Effort = "max", PermissionMode = "dontAsk" };
        tracker.Add(3, status: BoardStatus.Missing);

        await RunAsync();

        Assert.All(tracker.Read, Assert.Null);
        Assert.Equal("✻ No milestone · 1 task in the queue · opus · max effort · dontAsk mode", log.Lines[0]);
        Assert.DoesNotContain(log.Lines, line => line.Contains("on the board", StringComparison.Ordinal));
    }

    [Fact]
    public async Task TheFirstMessageNamesWhatANewTaskNeeds()
    {
        plan = plan with { Board = "https://github.com/orgs/acme/projects/4" };
        tracker.Add(3, labels: ["Feature", "urgent"]);

        await RunAsync();

        var message = Assert.Single(assistant.Started).Message;
        Assert.Contains("- Put the label `needs-owner` on it", message, StringComparison.Ordinal);
        Assert.Contains("the label `Feature`, as task #3 has", message, StringComparison.Ordinal);
        Assert.Contains("- Put it into the milestone `v0.2.0`, the one the owner works on.\n- Put it on the board https://github.com/orgs/acme/projects/4 with the status `Todo`.\n", message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ATaskOfTheRecordIsGoneOnWithWhateverLabelsTheRunSkips()
    {
        plan = plan with { Rules = Rules with { Skip = ["design"] } };
        state.Record = Record(5);
        tracker.Add(5, labels: ["feature", "design"], status: BoardStatus.InProgress);
        tracker.Add(7, labels: ["feature", "design"]);
        assistant.Session(5).Works().Ends(Done);

        var result = await RunAsync();

        Assert.Equal(new RunResult(RunEnd.Emptied, 1), result);
        Assert.Empty(assistant.Started);
        Assert.Contains("→ #5 Task 5 — taken again · its session still works, and is watched again · fake attach 5", log.Lines);
        Assert.Contains("  Held: #7 it has the design label, which this run skips", log.Lines);
        Assert.Null(state.Record);
    }
}
