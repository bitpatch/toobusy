using TooBusy.Core.Queue;
using TooBusy.Core.Run;

namespace TooBusy.Core.Tests;

// What a run counts: the tasks done, done in part and left to the owner, and a run of a number of tasks.
public sealed partial class SupervisorTests
{
    [Fact]
    public async Task ARunThatIsStoppedCountsATaskDoneInPartApart()
    {
        tracker.Add(3);
        tracker.Add(5);
        assistant.Session(3).Works(4).Ends("Half of it; the rest is #9.\nTOOBUSY: partial");
        clock.At(AfterLook(2), () => Run.Send(RunCommand.Stop));

        var result = await RunAsync();

        Assert.Equal(new RunResult(RunEnd.Stopped, 0) { Partial = 1 }, result);
        Assert.Contains("‖ Stopped by /stop · 0 done · 1 done in part", log.Lines);
        Assert.Contains("Stopped: 0 done · 1 done in part", machine.Notified);
    }

    [Fact]
    public async Task ARunThatStopsOverATaskCountsATaskDoneInPartApart()
    {
        tracker.Add(3);
        tracker.Add(5);
        tracker.Add(7);
        assistant.Session(3).Works().Ends(Done);
        assistant.Session(5).Works().Ends("Half of it; the rest is #9.\nTOOBUSY: partial");
        assistant.Session(7).Works().Ends("I tried twice.\nTOOBUSY: failed the tests do not pass");

        var result = await RunAsync();

        Assert.Equal(new RunResult(RunEnd.Problem, 1, "#7 failed · fake attach 7") { Partial = 1 }, result);
        Assert.Contains("✖ Stopped: #7 failed · fake attach 7 · the next task did not start · 1 done · 1 done in part", log.Lines);
    }

    [Fact]
    public async Task APauseCountsATaskDoneInPartApart()
    {
        tracker.Add(3);
        tracker.Add(5);
        assistant.Session(3).Works().Ends("Half of it; the rest is #9.\nTOOBUSY: partial");
        assistant.Session(5)
            .Works(and: () => assistant.Limits = new UsageLimits(null, new UsageWindow("weekly", 100, FakeClock.Start + TimeSpan.FromDays(1))))
            .Limit();

        var result = await RunAsync();

        Assert.Equal(RunEnd.Limited, result.End);
        Assert.Equal(1, result.Partial);

        // A far limit that is spent pauses the task at once.
        Assert.Contains("‖ #5 is paused: the weekly limit is spent, it resets 2030-01-02 09:00 UTC. Its changes stay in the working tree, and the next run goes on with its session · 0 done · 1 done in part", log.Lines);
        Assert.Equal(Record(5, RecordState.Paused), state.Record);
    }

    [Fact]
    public async Task WithNoTaskToTakeTheRunEndsAtOnce()
    {
        var result = await RunAsync();

        Assert.Equal(new RunResult(RunEnd.Emptied, 0), result);
        Assert.Equal(["✻ v0.2.0 · 0 tasks in the queue · the assistant's own model · high effort · auto mode", "✔ No task is left · 0 done", "  Worked 0 s"], log.Lines);
        Assert.Equal(["No task is left: 0 done"], machine.Notified);
    }

    [Fact]
    public async Task TheStatusCountsTheTasksOverBeforeATaskAndThoseThatFollowItAsTheQueueWasReadBeforeIt()
    {
        tracker.Add(3);
        tracker.Add(5);
        assistant.Session(3).Works(and: () => tracker.Add(9)).Ends(Done);
        assistant.Session(5).Works().Ends("Which key?\nTOOBUSY: owner");

        await RunAsync();

        // The task that the session of #3 makes is counted at the next task, and not while #3 is worked on. A task
        // left to the owner is over as one that is done is.
        Assert.Equal(
            [(3, 0, 1), (5, 1, 1), (9, 2, 0)],
            log.Statuses.Where(status => status.Phase == RunPhase.Working).Select(status => (status.Task!.Number, status.Behind, status.Queued)).Distinct());
    }

    [Fact]
    public async Task ATaskCountsWhenItIsDoneDoneInPartOrLeftToTheOwner()
    {
        plan = plan with { Most = 3 };
        tracker.Add(3);
        tracker.Add(5);
        tracker.Add(7);
        tracker.Add(8);
        assistant.Session(3).Works().Ends("Half of it; the rest is #9.\nTOOBUSY: partial");
        assistant.Session(5).Works().Ends("What is the colour?\nTOOBUSY: owner");

        var result = await RunAsync();

        Assert.Equal(new RunResult(RunEnd.Stopped, 1, "Stopped after 3 tasks, as you asked") { Partial = 1 }, result);
        Assert.Equal([3, 5, 7], assistant.Started.Select(start => start.Task));
        Assert.Contains("‖ Stopped after 3 tasks, as you asked · 1 done · 1 done in part", log.Lines);
    }

    [Fact]
    public async Task ATaskThatIsGoneOnWithCountsAsOne()
    {
        // The task of a record is gone on with, and the session of the next one asks and is told to go on alone.
        plan = plan with { Most = 2 };
        state.Record = Record(5);
        tracker.Add(3);
        tracker.Add(5, status: BoardStatus.InProgress);
        tracker.Add(7);
        assistant.Session(5).Works(2).Ends(Done);
        assistant.Session(3).Works().Asks().Then().Works().Ends(Done);

        var result = await RunAsync();

        Assert.Equal(new RunResult(RunEnd.Stopped, 2, "Stopped after 2 tasks, as you asked"), result);
        Assert.Equal([3], assistant.Started.Select(start => start.Task));
        Assert.Equal([(5, RunMark.Done), (3, RunMark.Done)], Ended);
    }

    [Fact]
    public async Task ARunOfANumberOfTasksTakesATaskThatASessionMakesInItsTurn()
    {
        plan = plan with { Most = 2 };
        tracker.Add(3);
        tracker.Add(5);
        assistant.Session(3).Works(and: () => tracker.Add(4)).Ends(Done);

        await RunAsync();

        Assert.Equal([3, 4], assistant.Started.Select(start => start.Task));
    }

    [Fact]
    public async Task ARunOfANumberOfTasksRunsOutOfTasksAsAnyOther()
    {
        plan = plan with { Most = 5 };
        tracker.Add(3);
        tracker.Add(5);

        var result = await RunAsync();

        Assert.Equal(new RunResult(RunEnd.Emptied, 2), result);
        Assert.Contains("✔ No task is left · 2 done", log.Lines);
    }

    [Fact]
    public async Task ARunPutsItsSignUpSaysOnItWhatItDoesAndTakesItDownWhenItEnds()
    {
        tracker.Add(3);
        assistant.Session(3).Works(4).Ends(Done);

        await RunAsync();

        var working = new RunSign(RunPhase.Working) { Task = 3, Title = "Task 3", Url = "https://example.com/3", Open = "fake attach 3" };
        Assert.Equal(new RunSign(RunPhase.Preparing), sign.Written[0]);
        Assert.Contains(new RunSign(RunPhase.Preparing) { Task = 3, Title = "Task 3", Url = "https://example.com/3" }, sign.Written);

        // The sign changes with what it says, not with every look at the session.
        Assert.Single(sign.Written, written => written == working);
        Assert.Equal(new RunSign(RunPhase.Preparing), sign.Written[^2]);
        Assert.Null(sign.Written[^1]);
        Assert.Single(sign.Written, written => written is null);
    }

    [Fact]
    public async Task ASupervisorMakesOneRunAndRefusesASecond()
    {
        tracker.Add(3);
        await RunAsync();
        tracker.Add(4);

        await Assert.ThrowsAsync<InvalidOperationException>(RunAsync);
        Assert.Equal([3], assistant.Started.Select(start => start.Task));
    }
}
