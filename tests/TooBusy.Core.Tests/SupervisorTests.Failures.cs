using TooBusy.Core.Run;

namespace TooBusy.Core.Tests;

// What fails beside the tracker: the files of the run, the working tree, and errors nobody foresaw.
public sealed partial class SupervisorTests
{
    [Fact]
    public async Task ASignThatCannotBeWrittenStopsTheSessionOfTheTaskAndTheRun()
    {
        tracker.Add(3);
        tracker.Add(5);
        var session = assistant.Session(3).Works(and: () => sign.Unwritable = new LocalStateException("the sign of the run cannot be written: Permission denied")).Works().Ends(Done);

        var result = await RunAsync();

        var why = "the sign of the run cannot be written: Permission denied · the session of #3 is stopped · fake attach 3";
        Assert.Equal(new RunResult(RunEnd.Problem, 0, why), result);
        Assert.Equal(1, session.Stopped);
        Assert.Equal([(3, RunMark.Failed)], Ended);
        Assert.Contains(log.Lines, line => line.StartsWith($"✖ Stopped: {why} · ", StringComparison.Ordinal));

        // The record stays, so the next run goes on with the session. The sign is not tried again, and comes down.
        Assert.Equal(Record(3), state.Record);
        Assert.Equal(1, sign.Refused);
        Assert.Null(sign.Written[^1]);
    }

    [Fact]
    public async Task ARunWhoseSignCannotBePutUpStartsNothing()
    {
        tracker.Add(3);
        sign.Unraisable = new LocalStateException("the sign of the run cannot be written: Read-only file system");

        var result = await RunAsync();

        Assert.Equal(new RunResult(RunEnd.Problem, 0, "the sign of the run cannot be written: Read-only file system"), result);
        Assert.Equal(["✖ Did not start: the sign of the run cannot be written: Read-only file system"], log.Lines);
        Assert.Empty(tracker.Read);
        Assert.Empty(assistant.Started);
        Assert.Equal(0, machine.Kept);
    }

    [Fact]
    public async Task ASignThatCannotBeTakenDownIsSaidAndTheRunEndsAsItWould()
    {
        tracker.Add(3);
        sign.Unlowerable = new LocalStateException("the sign of the run cannot be taken down: Permission denied");

        var result = await RunAsync();

        Assert.Equal(new RunResult(RunEnd.Emptied, 1), result);
        Assert.Contains("▲ the sign of the run cannot be taken down: Permission denied · it counts until toobusy ends", log.Lines);
        Assert.DoesNotContain(machine.Notified, said => said.StartsWith("Stopped", StringComparison.Ordinal));
    }

    [Fact]
    public async Task ARecordThatCannotBeForgottenStopsTheRunBeforeTheTrackerIsToldAndTheNextRunSettlesTheTaskOnce()
    {
        tracker.Add(3);
        tracker.Add(5);
        state.Unclearable = new LocalStateException("the record of the run cannot be taken away: Permission denied");
        var session = assistant.Session(3).Works().Ends(Done);

        var result = await RunAsync();

        Assert.Equal(new RunResult(RunEnd.Problem, 0, "the record of the run cannot be taken away: Permission denied"), result);
        Assert.Equal(1, session.Stopped);
        Assert.Equal([(3, RunMark.Failed)], Ended);
        Assert.Equal(["status 3 InProgress", "assign 3"], tracker.Did);
        Assert.Equal(Record(3), state.Record);

        state.Unclearable = null;
        run = null;
        result = await RunAsync();

        Assert.Equal(new RunResult(RunEnd.Emptied, 2), result);
        Assert.Single(tracker.Did, did => did == "close 3");
        Assert.Single(tracker.Comments, comment => comment.Number == 3);
        Assert.Single(assistant.Started, start => start.Task == 3);
    }

    [Fact]
    public async Task AnErrorNobodyForesawStopsTheSessionOfTheTaskAndTheRunAndIsNamed()
    {
        plan = plan with { Commands = Around };
        tracker.Add(3);
        tracker.Add(5);
        var session = assistant.Session(3).Works(and: () => throw new InvalidOperationException("The list of the sessions\nis gone."));

        var result = await RunAsync();

        var why = "an error toobusy did not foresee: InvalidOperationException: The list of the sessions is gone.";
        Assert.Equal(new RunResult(RunEnd.Problem, 0, why), result);
        Assert.Equal(1, session.Stopped);
        Assert.Equal([(3, RunMark.Failed)], Ended);
        Assert.Equal(
            ["■ Stopped the session of #3: the run cannot go on · fake attach 3", "  Running the command after #3: scripts/dev.sh server stop", "  the command after #3 passed in 2 s", $"✖ Stopped: {why} · the next task did not start · 0 done"],
            log.Lines.SkipWhile(line => !line.StartsWith('■')).Take(4));
        Assert.Equal([$"Stopped: {why}"], machine.Notified);

        // The whole error, with where it happened, is kept for the log on disk, which a screen does not show.
        var detail = log.Said.Single(line => line.Text.StartsWith("Stopped:", StringComparison.Ordinal)).Detail;
        Assert.StartsWith("System.InvalidOperationException: The list of the sessions", detail, StringComparison.Ordinal);
        Assert.Contains("   at ", detail, StringComparison.Ordinal);

        // The record stays, so the next run goes on with the session; the command after the task has its whole time.
        Assert.Equal(Record(3), state.Record);
        Assert.Equal([(Free, "3", policy.CommandTime), (Halt, "3", policy.CommandTime)], shell.Ran);
        Assert.Null(sign.Written[^1]);
        Assert.Equal(RunPhase.Ended, log.Statuses[^1].Phase);
        Assert.False(machine.Awake);
    }

    [Fact]
    public async Task AWorkingTreeThatThrowsBeforeATaskStopsTheRunAndNothingIsStarted()
    {
        tracker.Add(3);
        workspace.Failure = new InvalidOperationException("git said nothing");

        var result = await RunAsync();

        var why = "an error toobusy did not foresee: InvalidOperationException: git said nothing";
        Assert.Equal(new RunResult(RunEnd.Problem, 0, why), result);
        Assert.Empty(assistant.Started);
        Assert.Contains(log.Lines, line => line.StartsWith($"✖ Stopped: {why} · ", StringComparison.Ordinal));
        Assert.Null(sign.Written[^1]);
    }

    [Fact]
    public async Task AWorkingTreeThatThrowsAfterTheSessionStopsTheRunAndNothingOfTheOutcomeIsTold()
    {
        tracker.Add(3);
        var session = assistant.Session(3).Works(and: () => workspace.Failure = new InvalidOperationException("git said nothing")).Ends(Done);

        var result = await RunAsync();

        var why = "an error toobusy did not foresee: InvalidOperationException: git said nothing";
        Assert.Equal(new RunResult(RunEnd.Problem, 0, why), result);

        // The outcome stopped the session before the working tree was read, and it is not stopped again.
        Assert.Equal(1, session.Stopped);
        Assert.Equal(["status 3 InProgress", "assign 3"], tracker.Did);
        Assert.Equal(Record(3), state.Record);
        Assert.Contains(log.Lines, line => line.StartsWith($"✖ Stopped: {why} · ", StringComparison.Ordinal));
    }

    [Fact]
    public async Task ACancellationThatIsNotTheKillStopsTheRunAsAProblemAndNotAsAKill()
    {
        plan = plan with { Commands = Around };
        tracker.Add(3);
        var session = assistant.Session(3).Works(and: () => throw new OperationCanceledException("The command timed out."));

        var result = await RunAsync();

        var why = "an error toobusy did not foresee: OperationCanceledException: The command timed out.";
        Assert.Equal(new RunResult(RunEnd.Problem, 0, why), result);
        Assert.Equal(1, session.Stopped);
        Assert.Equal([(3, RunMark.Failed)], Ended);
        Assert.DoesNotContain(log.Lines, line => line.StartsWith("■ Killed", StringComparison.Ordinal));

        // Nobody killed the run, so the command after the task has its whole time.
        Assert.Equal([(Free, "3", policy.CommandTime), (Halt, "3", policy.CommandTime)], shell.Ran);
    }

    [Theory]
    [InlineData("status 3 InProgress", "#3 is moved to In Progress")]
    [InlineData("assign", "#3 is assigned to the current user")]
    [InlineData("close", "#3 is closed")]
    public async Task ACallOfTheTrackerThatDidNotAnswerIsMadeAgainAfterAPause(string silent, string again)
    {
        tracker.Add(3);
        tracker.Unanswered[silent] = 1;

        var result = await RunAsync();

        Assert.Equal(new RunResult(RunEnd.Emptied, 1), result);
        Assert.Equal([$"▲ GitHub did not answer: {again} again in 10 s"], log.Lines.Where(line => line.StartsWith('▲')));
        Assert.Equal(["status 3 InProgress", "assign 3", "comment 3", "close 3", "status 3 Done"], tracker.Did);
        Assert.Equal(1, tracker.Silences);
    }
}
