using TooBusy.Core.Queue;
using TooBusy.Core.Run;

namespace TooBusy.Core.Tests;

// A run of one task, `--task`.
public sealed partial class SupervisorTests
{
    [Theory]
    [InlineData("What is the colour?\nTOOBUSY: owner", "0 done")]
    public async Task ARunOfOneTaskEndsSoWhateverTheOutcomeOfTheTask(string reply, string went)
    {
        plan = plan with { Only = 3 };
        tracker.Add(3);
        tracker.Add(5);
        assistant.Session(3).Works().Ends(reply);

        var result = await RunAsync();

        Assert.Equal((RunEnd.Stopped, "Stopped after #3, as you asked"), (result.End, result.Why));
        Assert.Equal([3], assistant.Started.Select(start => start.Task));
        Assert.Contains($"‖ Stopped after #3, as you asked · {went}", log.Lines);
    }

    [Theory]
    [InlineData(3, "it has the manual label")]
    [InlineData(40, "it is not among the open tasks of the milestone v0.2.0")]
    public async Task ARunOfOneTaskStartsNothingWhenARunWouldNotTakeTheTaskAndSaysWhyInTheWordsOfTheQueue(int number, string why)
    {
        plan = plan with { Only = number };
        tracker.Add(3, labels: ["feature", "manual"]);
        tracker.Add(5);

        var result = await RunAsync();

        Assert.Equal(new RunResult(RunEnd.Problem, 0, $"#{number} is not taken: {why}"), result);
        Assert.Empty(assistant.Started);
        Assert.Empty(tracker.Did);
        Assert.Empty(log.Ends);
        Assert.Equal([$"✖ Stopped: #{number} is not taken: {why} · no task was started", "  Worked 0 s"], log.Lines);
        Assert.Equal([$"Stopped: #{number} is not taken: {why}"], machine.Notified);
    }

    [Fact]
    public async Task ARunOfOneTaskDoesNotTakeATaskWithALabelItSkips()
    {
        plan = plan with { Only = 3, Rules = Rules with { Skip = ["design"] } };
        tracker.Add(3, labels: ["feature", "design"]);
        tracker.Add(5);

        var result = await RunAsync();

        Assert.Equal(new RunResult(RunEnd.Problem, 0, "#3 is not taken: it has the design label, which this run skips"), result);
        Assert.Empty(assistant.Started);
        Assert.Equal(["✖ Stopped: #3 is not taken: it has the design label, which this run skips · no task was started", "  Worked 0 s"], log.Lines);
    }

    [Fact]
    public async Task ARunOfOneTaskWithoutAMilestoneSaysThatTheNumberIsNoOpenTask()
    {
        plan = plan with { Milestone = null, Only = 40 };
        tracker.Add(3);

        var result = await RunAsync();

        Assert.Equal(new RunResult(RunEnd.Problem, 0, "#40 is not taken: it is not among the open tasks"), result);
        Assert.Empty(assistant.Started);
    }

    [Fact]
    public async Task ARunOfOneTaskIsNotStartedOverTheTaskAnEarlierRunLeftWithASession()
    {
        plan = plan with { Only = 3 };
        state.Record = Record(5);
        tracker.Add(3);
        tracker.Add(5, status: BoardStatus.InProgress);
        var session = assistant.Session(5);

        var result = await RunAsync();

        Assert.Equal(new RunResult(RunEnd.Problem, 0, "#3 is not taken: an earlier run left #5 with a session, and a run goes on with that task before any other"), result);
        Assert.Empty(assistant.Started);
        Assert.Empty(tracker.Did);

        // The session of the other task and its record are left as they are, for the run that goes on with it.
        Assert.Empty(assistant.Found);
        Assert.Equal((0, 0), (session.Stopped, session.Told.Count));
        Assert.Equal(Record(5), state.Record);
    }

    [Fact]
    public async Task ARunOfOneTaskGoesOnWithTheSessionAnEarlierRunLeftForThatTask()
    {
        plan = plan with { Only = 5 };
        state.Record = Record(5);
        tracker.Add(3);
        tracker.Add(5, status: BoardStatus.InProgress);
        assistant.Session(5).Works(2).Ends(Done);

        var result = await RunAsync();

        Assert.Equal(new RunResult(RunEnd.Stopped, 1, "Stopped after #5, as you asked"), result);
        Assert.Empty(assistant.Started);
        Assert.Contains("→ #5 Task 5 — taken again · its session still works, and is watched again · fake attach 5", log.Lines);
        Assert.Null(state.Record);
    }

    [Fact]
    public async Task ARunOfOneTaskTakesItOnceTheRecordOfATaskThatIsNotGoneOnWithIsForgotten()
    {
        // The task of the record was closed since: its record is forgotten, as in any run, and holds nothing back.
        plan = plan with { Only = 3 };
        state.Record = Record(5);
        tracker.Add(3);

        var result = await RunAsync();

        Assert.Equal(new RunResult(RunEnd.Stopped, 1, "Stopped after #3, as you asked"), result);
        Assert.Equal([3], assistant.Started.Select(start => start.Task));
    }

    [Fact]
    public async Task ARunOfOneTaskThatIsStoppedOrFailsEndsAsAnyOther()
    {
        plan = plan with { Only = 3 };
        tracker.Add(3);
        assistant.Session(3).Works().Ends("No way.\nTOOBUSY: failed the tests do not pass");

        var result = await RunAsync();

        Assert.Equal(RunEnd.Problem, result.End);
        Assert.Contains("✖ Stopped: #3 failed · fake attach 3 · the next task did not start · 0 done", log.Lines);
        Assert.DoesNotContain(log.Lines, line => line.Contains("as you asked", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData(1, "1 task")]
    [InlineData(2, "2 tasks")]
    public async Task ARunOfANumberOfTasksTakesSoManyAndEndsAsOneThatItsOwnerStopped(int most, string tasks)
    {
        plan = plan with { Most = most };
        tracker.Add(3);
        tracker.Add(5);
        tracker.Add(7);

        var result = await RunAsync();

        Assert.Equal(new RunResult(RunEnd.Stopped, most, $"Stopped after {tasks}, as you asked"), result);
        Assert.Equal(most == 1 ? [3] : [3, 5], assistant.Started.Select(start => start.Task));
        Assert.Equal($"✻ v0.2.0 · 3 tasks in the queue, {most} at most · the assistant's own model · high effort · auto mode", log.Lines[0]);
        Assert.Equal($"‖ Stopped after {tasks}, as you asked · {most} done", log.Lines[^2]);
        Assert.Equal([$"Stopped after {tasks}, as you asked: {most} done"], machine.Notified);

        // The queue is read before each task, and not after the last one. A task is followed by those the run
        // still takes, and by no more.
        Assert.Equal(most, tracker.Read.Count);
        Assert.Equal(
            Enumerable.Range(0, most).Select(behind => (behind, most - 1 - behind)),
            log.Statuses.Where(status => status.Phase == RunPhase.Working).Select(status => (status.Behind, status.Queued)).Distinct());
    }
}
