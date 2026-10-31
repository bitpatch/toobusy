using TooBusy.Core.Run;

namespace TooBusy.Core.Tests;

// A tracker that refuses or does not answer: the pauses before a call is made again, and what is left to the
// owner.
public sealed partial class SupervisorTests
{
    [Theory]
    [InlineData(true, "  #3 is In Progress on the board; the session is told that toobusy keeps its task")]
    [InlineData(false, null)]
    public async Task ATrackerThatRefusesTheAssignmentDoesNotStopTheRun(bool board, string? note)
    {
        plan = plan with { Rules = Rules with { Board = board } };
        tracker.Add(3, labels: ["feature", "interrupted"]);
        tracker.Add(5);
        tracker.Refused.Add("assign");

        var result = await RunAsync();

        // What follows the assignment is done as it is with one: the label is taken off, and the tasks are done.
        Assert.Equal(new RunResult(RunEnd.Emptied, 2), result);
        AssertDid(3, "status 3 InProgress", "label 3 -interrupted", "comment 3", "close 3", "status 3 Done");
        Assert.Empty(assistant.Session(3).Told);
        Assert.Contains("▲ The tracker refused: assign 3. · the run goes on", log.Lines);
        Assert.Contains("▲ The tracker refused: assign 5. · the run goes on", log.Lines);
        Assert.DoesNotContain(log.Lines, line => line.Contains("assigned to the current user", StringComparison.Ordinal));
        if (note is not null)
            Assert.Contains(note, log.Lines);
        else
            Assert.DoesNotContain(log.Lines, line => line.Contains("keeps its task", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("label", "▲ #3 is not left to the owner as it should be: The tracker refused: label 3 +needs-owner. · set it right by hand before the next run", "status 3 InProgress|assign 3|comment 3")]
    [InlineData("comment", "▲ #3 is not left to the owner as it should be: The tracker refused: comment 3. · set it right by hand before the next run", "status 3 InProgress|assign 3|label 3 +needs-owner|status 3 Todo")]
    public async Task ATrackerThatRefusesToLeaveATaskToTheOwnerDoesNotHideWhyTheRunStopped(string refused, string said, string did)
    {
        tracker.Add(3);
        tracker.Refused.Add(refused);
        assistant.Session(3).Works().Ends("I tried twice.\nTOOBUSY: failed the tests do not pass");

        var result = await RunAsync();

        Assert.Equal(new RunResult(RunEnd.Problem, 0, "#3 failed · fake attach 3"), result);
        Assert.Contains(said, log.Lines);
        Assert.Contains(log.Lines, line => line.StartsWith("✖ Stopped: #3 failed · fake attach 3 · ", StringComparison.Ordinal));
        AssertDid(did.Split('|'));
    }

    [Fact]
    public async Task ATrackerThatCannotBeReadStopsTheRun()
    {
        tracker.Refused.Add("read");

        Assert.Equal("The tracker refused: read.", (await RunAsync()).Why);
    }

    [Fact]
    public async Task ARecordThatCannotBeWrittenStopsTheSessionOfTheTaskAndTheRun()
    {
        plan = plan with { Commands = Around };
        tracker.Add(3);
        tracker.Add(5);
        state.Unwritable = new LocalStateException("the record of #3 cannot be written: No space left on device");
        var session = assistant.Session(3);

        var result = await RunAsync();

        var why = "the record of #3 cannot be written: No space left on device · the session of #3 is stopped · fake attach 3";
        Assert.Equal(new RunResult(RunEnd.Problem, 0, why), result);
        Assert.Equal(1, session.Stopped);
        Assert.Equal(0, session.Looks);
        Assert.Single(assistant.Started);
        Assert.Equal(["status 3 InProgress"], tracker.Did);
        Assert.Contains(log.Lines, line => line.StartsWith($"✖ Stopped: {why} · ", StringComparison.Ordinal));
        Assert.Equal([$"Stopped: {why}"], machine.Notified);

        // The command after the task runs as after any other end of it, and the sign comes down.
        Assert.Equal([Free, Halt], shell.Ran.Select(ran => ran.Command));
        Assert.Null(sign.Written[^1]);
        Assert.Equal(RunPhase.Ended, log.Statuses[^1].Phase);
        Assert.False(machine.Awake);
    }

    [Fact]
    public async Task TheLabelsTheStatusAndTheReadingOfATaskAreMadeAgainInTheWordsOfWhatTheyDo()
    {
        tracker.Add(3, labels: ["feature", "interrupted"]);
        tracker.Unanswered["label 3 -interrupted"] = 1;
        tracker.Unanswered["look"] = 1;
        tracker.Unanswered["label 3 +needs-owner"] = 1;
        tracker.Unanswered["status 3 Todo"] = 1;
        assistant.Session(3).Works().Ends("Which key?\nTOOBUSY: owner");

        var result = await RunAsync();

        Assert.Equal(new RunResult(RunEnd.Emptied, 0), result);
        Assert.Equal(
            [
                $"▲ GitHub did not answer: the label interrupted is taken off #3 again in {Spoken.Time(policy.RetryPauses[0])}",
                $"▲ GitHub did not answer: #3 is read again in {Spoken.Time(policy.RetryPauses[0])}",
                $"▲ GitHub did not answer: the label needs-owner is put on #3 again in {Spoken.Time(policy.RetryPauses[0])}",
                $"▲ GitHub did not answer: #3 is moved to Todo again in {Spoken.Time(policy.RetryPauses[0])}",
            ],
            log.Lines.Where(line => line.StartsWith('▲')));
        AssertDid("status 3 InProgress", "assign 3", "label 3 -interrupted", "label 3 +needs-owner", "comment 3", "status 3 Todo");
    }

    [Fact]
    public async Task ACallThatDidNotAnswerThreeTimesStopsTheRunWithTheWordsOfTheLastFailure()
    {
        tracker.Add(3);
        tracker.Add(5);
        tracker.Unanswered["close"] = 3;
        var tried = new List<DateTimeOffset>();
        tracker.Doing = what =>
        {
            if (what == "close 3")
                tried.Add(clock.Now);
        };

        var result = await RunAsync();

        Assert.Equal(new RunResult(RunEnd.Problem, 0, "The tracker did not answer: close 3 (3)."), result);
        Assert.Equal(policy.RetryPauses, tried.Zip(tried.Skip(1), (before, after) => after - before));
        Assert.Equal(
            policy.RetryPauses.Select(pause => $"▲ GitHub did not answer: #3 is closed again in {Spoken.Time(pause)}"),
            log.Lines.Where(line => line.StartsWith('▲')));
        Assert.Contains(log.Lines, line => line.StartsWith("✖ Stopped: The tracker did not answer: close 3 (3). · ", StringComparison.Ordinal));
        Assert.Single(assistant.Started);

        // The task is left to the owner as over any tracker that refuses.
        AssertDid("status 3 InProgress", "assign 3", "comment 3", "label 3 +needs-owner", "comment 3", "status 3 Todo");
    }

    [Fact]
    public async Task ACommentThatDidNotAnswerIsNotWrittenAgain()
    {
        tracker.Add(3);
        tracker.Unanswered["comment"] = 1;

        var result = await RunAsync();

        // Its answer may be lost and the comment written: the only one that is written for sure is the one that
        // leaves the task to the owner.
        Assert.Equal(new RunResult(RunEnd.Problem, 0, "The tracker did not answer: comment 3 (1)."), result);
        Assert.DoesNotContain(log.Lines, line => line.StartsWith('▲'));
        AssertDid("status 3 InProgress", "assign 3", "label 3 +needs-owner", "comment 3", "status 3 Todo");
        Assert.StartsWith("**Failed.**", Assert.Single(tracker.Comments).Text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AKillEndsAPauseAtOnce()
    {
        tracker.Add(3);
        tracker.Unanswered["read"] = 1;
        var paused = new List<RunStatus>();
        clock.At(policy.RetryPauses[0] / 2, () =>
        {
            paused.Add(log.Statuses[^1]);
            kill.Cancel();
        });

        var result = await RunAsync();

        Assert.Equal(RunEnd.Killed, result.End);
        Assert.Single(tracker.Read);
        Assert.Empty(assistant.Started);
        Assert.Contains($"▲ GitHub did not answer: the open tasks are read again in {Spoken.Time(policy.RetryPauses[0])}", log.Lines);

        // Between tasks the page says what the run does, as it did before the pause.
        Assert.Equal((RunPhase.Preparing, "Reading the queue…", null), (paused[0].Phase, paused[0].Text, paused[0].Task));
        Assert.Equal(RunPhase.Preparing, Assert.Single(sign.Written, written => written is not null)!.Phase);
    }

    [Fact]
    public async Task AKillInAPauseWhileATaskIsTakenStopsItsSession()
    {
        tracker.Add(3);
        tracker.Unanswered["assign"] = 1;
        clock.At(policy.RetryPauses[0] / 2, kill.Cancel);

        var result = await RunAsync();

        Assert.Equal(new RunResult(RunEnd.Killed, 0, "#3 was being taken, and the next run goes on with its session · fake attach 3"), result);
        Assert.Contains("■ Killed the session of #3 while the task was taken: the next run goes on with its session · fake attach 3", log.Lines);
        Assert.Equal(1, assistant.Session(3).Stopped);
        Assert.Single(tracker.Asked, asked => asked == "assign 3");
        Assert.Equal(Record(3), state.Record);
    }

    [Fact]
    public async Task AfterAKillNothingIsMadeAgain()
    {
        tracker.Add(3);
        tracker.Unanswered["label"] = 1;
        assistant.Session(3).Works().Limit();
        clock.At(policy.UnknownLimitWait / 2, kill.Cancel);

        var result = await RunAsync();

        // The owner waits for the pause of the task, so what is not answered then is said and not waited for.
        Assert.Equal(RunEnd.Killed, result.End);
        Assert.Single(tracker.Asked, asked => asked == "label 3 +interrupted");
        Assert.DoesNotContain(log.Lines, line => line.StartsWith("▲ GitHub did not answer", StringComparison.Ordinal));
        Assert.Contains("▲ The pause of #3 is not whole: The tracker did not answer: label 3 +interrupted (1). · set it right by hand before the next run", log.Lines);
        Assert.Equal(Record(3, RecordState.Paused), state.Record);
    }

    [Fact]
    public async Task ABoardThatCannotTakeADoneTaskDoesNotStopTheRun()
    {
        tracker.Add(3);
        tracker.Refused.Add("status 3 Done");

        var result = await RunAsync();

        Assert.Equal(new RunResult(RunEnd.Emptied, 1), result);
        Assert.Contains("▲ The tracker refused: status 3 Done.", log.Lines);
    }

    [Fact]
    public async Task ALabelThatCannotBePutStopsTheRun()
    {
        tracker.Add(3);
        tracker.Add(5);
        tracker.Refused.Add("label");
        assistant.Session(3).Ends("Which key?\nTOOBUSY: owner");

        var result = await RunAsync();

        Assert.Equal("The tracker refused: label 3 +needs-owner.", result.Why);
        Assert.Single(assistant.Started);
    }

    // An interrupted task keeps its record while the tracker is told how it went, as its session goes on with the
    // same conversation. A tracker that refuses the label stops the run with its words, before the comment and the
    // move: the next run finds the task as the session left it, and goes on with the session after /abort.
    [Fact]
    public async Task AnInterruptedTaskWhoseLabelIsRefusedKeepsItsRecordAndTheNextRunGoesOnWithItsSession()
    {
        tracker.Add(3);
        tracker.Refused.Add("label");
        var session = assistant.Session(3).Works().Ends("Undone: a.cs.\nTOOBUSY: interrupted").Then().Works().Ends(Done);

        var result = await RunAsync();

        Assert.Equal(new RunResult(RunEnd.Problem, 0, "The tracker refused: label 3 +interrupted."), result);
        AssertDid("status 3 InProgress", "assign 3");
        Assert.Empty(tracker.Comments);
        Assert.Equal(Record(3, RecordState.WrappedUp), state.Record);
        Assert.Contains(log.Lines, line => line.StartsWith("✖ Stopped: The tracker refused: label 3 +interrupted. · ", StringComparison.Ordinal));

        tracker.Refused.Clear();
        run = null;
        result = await RunAsync();

        Assert.Equal(new RunResult(RunEnd.Emptied, 1), result);
        Assert.Single(assistant.Started);
        Assert.Equal([Briefing.AfterAbort(3)], session.Told);
        Assert.Contains("→ #3 Task 3 — taken again · its session goes on after /abort · fake attach 3", log.Lines);
        AssertDid("status 3 InProgress", "assign 3", "status 3 InProgress", "assign 3", "comment 3", "close 3", "status 3 Done");
        Assert.Null(state.Record);
    }

    [Fact]
    public async Task ATaskThatComesUpAgainAfterItsSessionStopsTheRun()
    {
        // Without a board nothing but its closing takes a done task out of the queue, and this tracker forgets it.
        plan = plan with { Rules = Rules with { Board = false } };
        tracker.Add(3);
        tracker.ForgetsClosing = true;

        var result = await RunAsync();

        Assert.Equal(new RunResult(RunEnd.Problem, 1, "#3 came up again after its session"), result);
        Assert.Single(assistant.Started);
    }

    [Fact]
    public async Task ASessionThatAsksIsToldToGoOnAloneAfterTheCountdown()
    {
        tracker.Add(3);
        var session = assistant.Session(3).Works().Asks("input needed", "CSV or JSON?").Then().Works().Ends("JSON it is.\nTOOBUSY: done");

        var result = await RunAsync();

        Assert.Equal(new RunResult(RunEnd.Emptied, 1), result);
        Assert.Equal([Briefing.Alone(3, "needs-owner")], session.Told);
        Assert.Equal(
            [
                $"‖ #3 waits for the owner: input needed · it is told to go on alone in {Spoken.Time(policy.OwnerWait)}: /nudge does it now, /hold never · fake attach 3",
                "  │ CSV or JSON?",
                "▶ #3 is told to go on without the owner: its turn is cut, and this is the next message of its conversation · fake attach 3",
                $"  “{Briefing.Alone(3, "needs-owner")}”",
                $"✔ #3 Task 3 — done in {Spoken.Time(OwnerWaited)}",
            ],
            log.Lines.Skip(3).Take(5));
        Assert.Contains("#3 waits for an answer", machine.Notified);

        // What the session said stands in what the run is doing for as long as it waits, and no longer.
        Assert.Equal(["CSV or JSON?"], log.Statuses.First(status => status.Phase == RunPhase.WaitingForOwner).Reply);
        Assert.All(log.Statuses.Where(status => status.Phase != RunPhase.WaitingForOwner), status => Assert.Empty(status.Reply));
    }

    // Whatever the record said of the session, it was told to go on and stopped after that: its changes are its
    // own, and the next run tells it so.
    [Theory]
    [InlineData(RecordState.Working)]
    [InlineData(RecordState.Paused)]
    public async Task ATrackerThatRefusesAfterASessionWasToldToGoOnStopsItAndTheNextRunGoesOnWithIt(RecordState left)
    {
        state.Record = Record(5, left);
        tracker.Add(5, labels: ["feature", "interrupted"]);
        tracker.Refused.Add("status 5 InProgress");
        var session = assistant.Session(5).Lost("I was about to").Then().Works().Ends(Done);
        var first = left == RecordState.Paused ? Briefing.AfterLimit(5) : Briefing.AfterStop(5);

        var result = await RunAsync();

        var why = "The tracker refused: status 5 InProgress. · the session of #5 is stopped, and the next run goes on with it · fake attach 5";
        Assert.Equal(new RunResult(RunEnd.Problem, 0, why), result);
        Assert.Equal([first], session.Told);
        Assert.Equal(1, session.Stopped);
        Assert.Empty(tracker.Did);
        Assert.Contains(log.Lines, line => line.StartsWith($"✖ Stopped: {why} · ", StringComparison.Ordinal));
        Assert.Equal(Record(5), state.Record);

        tracker.Refused.Clear();
        workspace.Next.Enqueue(new WorkspaceState(2, 0));
        run = null;
        result = await RunAsync();

        Assert.Equal(new RunResult(RunEnd.Emptied, 1), result);
        Assert.Empty(assistant.Started);
        Assert.Equal([first, Briefing.AfterStop(5)], session.Told);
        AssertDid("status 5 InProgress", "assign 5", "label 5 -interrupted", "comment 5", "close 5", "status 5 Done");
        Assert.Null(state.Record);
    }

    [Fact]
    public async Task AKillWhileTheOutcomeIsToldDoesNotCutItAndEndsTheRun()
    {
        plan = plan with { Commands = Around };
        tracker.Add(3);
        tracker.Add(5);
        tracker.Doing = what =>
        {
            if (what == "close 3")
                kill.Cancel();
        };

        var result = await RunAsync();

        // The record is forgotten before the tracker is told, so the task is closed and moved all the same.
        const string Left = "#3 was being settled, and the tracker was told how it went all the same";
        Assert.Equal(new RunResult(RunEnd.Killed, 1, Left), result);
        AssertDid("status 3 InProgress", "assign 3", "comment 3", "close 3", "status 3 Done");
        Assert.Null(state.Record);
        Assert.Equal([(3, RunMark.Done)], Ended);
        Assert.Equal(["  #3 is closed, with the report of the session as its comment", $"■ Killed while {Left}"], log.Lines.SkipWhile(line => !line.StartsWith("  #3 is closed", StringComparison.Ordinal)).Take(2));

        // The next task is not started, and the command after this one is not cut.
        Assert.Single(assistant.Started);
        Assert.Equal([(Free, "3", policy.CommandTime), (Halt, "3", policy.KilledCommandTime)], shell.Ran);
    }
}
