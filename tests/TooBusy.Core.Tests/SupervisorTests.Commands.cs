using TooBusy.Core.Queue;
using TooBusy.Core.Run;

namespace TooBusy.Core.Tests;

// The commands of the project that run before and after a task.
public sealed partial class SupervisorTests
{
    [Fact]
    public async Task TheCommandsOfTheProjectRunAroundATaskAndAreToldOfIt()
    {
        plan = plan with { Commands = Around };
        tracker.Add(3);
        tracker.Doing = did =>
        {
            Assert.True(did != "status 3 InProgress" || shell.Ran.Count == 1, "The task was moved before the command before it ran.");
            Assert.True(did != "status 3 Done" || shell.Ran.Count == 1, "The command after the task ran before the tracker was told.");
        };

        var result = await RunAsync();

        Assert.Equal(new RunResult(RunEnd.Emptied, 1), result);
        Assert.Equal([(Free, "3", policy.CommandTime), (Halt, "3", policy.CommandTime)], shell.Ran);
        Assert.Equal(
            [("TOOBUSY_TASK", "3"), ("TOOBUSY_TASK_TITLE", "Task 3"), ("TOOBUSY_TASK_URL", "https://example.com/3")],
            shell.Told[0].OrderBy(pair => pair.Key, StringComparer.Ordinal).Select(pair => (pair.Key, pair.Value)));
        Assert.Equal(
            ["  Running the command before #3: scripts/dev.sh ports free", $"  the command before #3 passed in {Spoken.Time(shell.Takes)}", "  Running the command after #3: scripts/dev.sh server stop", $"  the command after #3 passed in {Spoken.Time(shell.Takes)}"],
            log.Lines.Where(line => line.Contains("the command", StringComparison.Ordinal)));

        // The time of the task counts from the command before it.
        Assert.Equal([(3, RunMark.Done, Seconds(shell.Takes + policy.Poll))], log.Ends);
        Assert.Contains(log.Statuses, status => status is { Phase: RunPhase.Preparing, Task.Number: 3, Step: "the command before the task" });
        Assert.Contains(log.Statuses, status => status is { Phase: RunPhase.Preparing, Task.Number: 3, Step: "the command after the task" });
    }

    [Fact]
    public async Task AProjectMayHaveOneOfTheTwoCommands()
    {
        plan = plan with { Commands = new(null, Halt) };
        tracker.Add(3);

        await RunAsync();

        Assert.Equal([Halt], shell.Ran.Select(ran => ran.Command));
    }

    [Fact]
    public async Task ACommandBeforeATaskThatFailsStopsTheRunAndTheTaskIsNotTouched()
    {
        plan = plan with { Commands = Around };
        shell.Ends[Free] = new CommandEnd(CommandStatus.Exited, 3, "checking the ports\n\nport 3000 is taken by pid 71\n");
        tracker.Add(3);

        var result = await RunAsync();

        Assert.Equal(new RunResult(RunEnd.Problem, 0, "the command before #3 exited with 3 · #3 is not taken"), result);
        Assert.Empty(tracker.Did);
        Assert.Empty(assistant.Started);
        Assert.Null(state.Record);
        Assert.Equal([Free], shell.Ran.Select(ran => ran.Command));
        Assert.Equal(
            [
                "  Running the command before #3: scripts/dev.sh ports free",
                "✖ the command before #3 exited with 3: scripts/dev.sh ports free",
                "  │ checking the ports",
                "  │ port 3000 is taken by pid 71",
                "✖ Stopped: the command before #3 exited with 3 · #3 is not taken · the next task did not start · 0 done",
            ],
            log.Lines.Skip(1).Take(5));
        Assert.Empty(Ended);
    }

    [Fact]
    public async Task OnlyTheEndOfWhatAFailedCommandPrintedIsShown()
    {
        plan = plan with { Commands = Around };
        shell.Ends[Free] = new CommandEnd(CommandStatus.Exited, 1, string.Join('\n', Enumerable.Range(1, 15).Select(line => $"line {line}")));
        tracker.Add(3);

        await RunAsync();

        Assert.Contains("  │ … 3 more lines before these", log.Lines);
        Assert.DoesNotContain("  │ line 3", log.Lines);
        Assert.Contains("  │ line 4", log.Lines);
        Assert.Contains("  │ line 15", log.Lines);
    }

    [Theory]
    [InlineData(CommandStatus.TimedOut)]
    [InlineData(CommandStatus.NotStarted)]
    public async Task ACommandThatIsGivenUpOrDoesNotStartFailsAsOneThatExitedWithAnError(CommandStatus status)
    {
        var how = status == CommandStatus.TimedOut ? $"did not end in {Spoken.Time(policy.CommandTime)} and was stopped" : "did not start: the shell of the machine cannot be run";
        plan = plan with { Commands = Around };
        shell.Ends[Free] = new CommandEnd(status, 0, "");
        tracker.Add(3);

        var result = await RunAsync();

        Assert.Equal($"the command before #3 {how} · #3 is not taken", result.Why);
        Assert.Empty(tracker.Did);
    }

    [Theory]
    [InlineData("Did a part, the rest is #9.\nTOOBUSY: partial", RunEnd.Emptied)]
    [InlineData("It broke.\nTOOBUSY: failed the build is broken", RunEnd.Problem)]
    public async Task TheCommandAfterATaskRunsHoweverTheTaskWent(string reply, RunEnd end)
    {
        plan = plan with { Commands = Around };
        policy = policy with { Messages = 0 };
        tracker.Add(3);
        assistant.Session(3).Ends(reply);

        var result = await RunAsync();

        Assert.Equal(end, result.End);
        Assert.Equal([Free, Halt], shell.Ran.Select(ran => ran.Command));
        Assert.DoesNotContain(log.Lines, line => line.StartsWith("▲ the command", StringComparison.Ordinal));
    }

    [Fact]
    public async Task TheCommandAfterATaskRunsWhenItsSessionDoesNotStart()
    {
        plan = plan with { Commands = Around };
        tracker.Add(3);
        assistant.RefuseToStart = "Claude Code does not trust the folder";

        var result = await RunAsync();

        Assert.Equal(RunEnd.Problem, result.End);
        Assert.Equal([Free, Halt], shell.Ran.Select(ran => ran.Command));
    }

    [Fact]
    public async Task AKillCutsTheCommandBeforeATaskAndNothingIsTakenOrRunAfterIt()
    {
        plan = plan with { Commands = Around };
        tracker.Add(3);
        shell.Running = _ => kill.Cancel();

        var result = await RunAsync();

        Assert.Equal(new RunResult(RunEnd.Killed, 0, "the command before #3 was cut by the kill, and the task was not taken"), result);
        Assert.Empty(tracker.Did);
        Assert.Equal([Free], shell.Ran.Select(ran => ran.Command));
        Assert.Contains("■ Killed the command before #3: scripts/dev.sh ports free", log.Lines);
    }

    [Fact]
    public async Task ACommandAfterATaskThatFailsStopsTheRunAndTheTaskStaysAsItEnded()
    {
        plan = plan with { Commands = Around };
        shell.Ends[Halt] = new CommandEnd(CommandStatus.Exited, 1, "the server does not stop\n");
        tracker.Add(3);
        tracker.Add(4);

        var result = await RunAsync();

        Assert.Equal(new RunResult(RunEnd.Problem, 1, "the command after #3 exited with 1 · #3 stays as it ended"), result);
        AssertDid("status 3 InProgress", "assign 3", "comment 3", "close 3", "status 3 Done");
        Assert.Equal([3], assistant.Started.Select(started => started.Task));
        Assert.Equal([(3, RunMark.Done)], Ended);
        Assert.Equal(
            [
                "✖ the command after #3 exited with 1: scripts/dev.sh server stop",
                "  │ the server does not stop",
                "✖ Stopped: the command after #3 exited with 1 · #3 stays as it ended · the next task did not start · 1 done",
            ],
            log.Lines.SkipWhile(line => line[0] != '✖').Take(3));
    }

    [Fact]
    public async Task ACommandAfterATaskThatFailsIsOnlySaidWhenTheRunStopsOverTheTaskAnyway()
    {
        plan = plan with { Commands = Around };
        shell.Ends[Halt] = new CommandEnd(CommandStatus.Exited, 1, "");
        tracker.Add(3);
        assistant.Session(3).Ends("It broke.\nTOOBUSY: failed the build is broken");

        var result = await RunAsync();

        Assert.Equal("#3 failed · fake attach 3", result.Why);
        Assert.Contains("▲ the command after #3 exited with 1 · see to it by hand before the next run", log.Lines);
    }

    [Fact]
    public async Task ASessionThatStillWorksIsNotDisturbedByTheCommandBeforeItsTask()
    {
        plan = plan with { Commands = Around };
        state.Record = Record(5);
        tracker.Add(5, status: BoardStatus.InProgress);
        assistant.Session(5).Works(2).Ends(Done);

        var result = await RunAsync();

        Assert.Equal(new RunResult(RunEnd.Emptied, 1), result);
        Assert.Equal([Halt], shell.Ran.Select(ran => ran.Command));
    }

    [Fact]
    public async Task ASessionThatIsToldToGoOnGetsTheCommandBeforeItsTaskFirst()
    {
        plan = plan with { Commands = Around };
        state.Record = Record(5, RecordState.Paused);
        tracker.Add(5, labels: ["feature", "interrupted"]);
        var session = assistant.Session(5).Ends(Done);
        shell.Running = command => Assert.True(command != Free || session.Told.Count == 0, "The session was told to go on before the command ran.");

        var result = await RunAsync();

        Assert.Equal(new RunResult(RunEnd.Emptied, 1), result);
        Assert.Single(session.Told);
        Assert.Equal([Free, Halt], shell.Ran.Select(ran => ran.Command));
    }

    [Fact]
    public async Task ASessionIsNotToldToGoOnWhenTheCommandBeforeItsTaskFails()
    {
        plan = plan with { Commands = Around };
        shell.Ends[Free] = new CommandEnd(CommandStatus.Exited, 2, "");
        state.Record = Record(5, RecordState.Paused);
        tracker.Add(5, labels: ["feature", "interrupted"]);
        var session = assistant.Session(5);

        var result = await RunAsync();

        Assert.Equal("the command before #5 exited with 2 · the session of #5 is not told to go on, and the next run tries again", result.Why);
        Assert.Empty(session.Told);
        Assert.Empty(tracker.Did);
        Assert.Equal(Record(5, RecordState.Paused), state.Record);
        Assert.Equal([Free], shell.Ran.Select(ran => ran.Command));
    }

    [Fact]
    public async Task TheCommandsDoNotRunAroundAWaitForAUsageLimit()
    {
        plan = plan with { Commands = Around };
        tracker.Add(3);
        assistant.Limits = new UsageLimits(new UsageWindow("5-hour", 100, FakeClock.Start + TimeSpan.FromMinutes(30)), null);
        assistant.Session(3).Limit().Ends(Done);

        var result = await RunAsync();

        Assert.Equal(new RunResult(RunEnd.Emptied, 1), result);
        Assert.Equal([Free, Halt], shell.Ran.Select(ran => ran.Command));
    }
}
