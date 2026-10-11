using TooBusy.Core.Assistant;
using TooBusy.Core.Queue;
using TooBusy.Core.Run;

namespace TooBusy.Core.Tests;

public sealed class SupervisorTests : IDisposable
{
    const string Done = "Did it.\nTOOBUSY: done";

    static readonly QueueRules Rules = new(["manual"], ["feature", "bug"], "needs-owner", "interrupted", Board: true);

    readonly FakeClock clock = new();
    readonly FakeTracker tracker = new();
    readonly FakeAssistant assistant = new();
    readonly FakeWorkspace workspace = new();
    readonly FakeMachine machine = new();
    readonly FakeState state = new();
    readonly Log log = new();
    readonly CancellationTokenSource kill = new();

    RunPlan plan = new("v0.2.0", Rules, ModelChoice.AssistantsOwn, "high");
    RunPolicy policy = RunPolicy.Default;
    Supervisor? run;

    public void Dispose() => kill.Dispose();

    [Fact]
    public async Task ATaskIsMovedBeforeItsSessionStartsAndClosedWithItsReportWhenItIsDone()
    {
        tracker.Add(3);

        var result = await RunAsync();

        Assert.Equal(new RunResult(RunEnd.Emptied, 1), result);
        Assert.Equal(["status 3 InProgress", "comment 3", "close 3", "status 3 Done"], tracker.Did);
        Assert.Equal((3, "**Done.**\n\nDid it.\n\n_Session: `fake attach 3`_"), Assert.Single(tracker.Comments));
        var start = Assert.Single(assistant.Started);
        Assert.Equal((3, "#3 Task 3", ModelChoice.AssistantsOwn, "high"), (start.Task, start.Name, start.Model, start.Effort));
        Assert.Contains("# Task #3: Task 3\n\nhttps://example.com/3\n\nRead the task and its comments before you start", start.Message, StringComparison.Ordinal);
        Assert.Contains("It has already moved the task to In Progress.", start.Message, StringComparison.Ordinal);
        Assert.Equal(1, assistant.Session(3).Stopped);
        Assert.Equal(
            """
            ✻ v0.2.0 · 1 task in the queue · the assistant's own model · high effort
            → #3 Task 3 — started · a new session · fake attach 3
              #3 is In Progress on the board; the session is told that toobusy keeps the tracker
            ✔ #3 Task 3 — done in 3 s
              #3 is closed, with the report of the session as its comment
            ✔ No task is left · 1 done
              Worked 3 s
            """.ReplaceLineEndings("\n"),
            log.Text);
    }

    [Fact]
    public async Task TheTasksAreTakenOneAfterAnotherAndTheQueueIsReadBeforeEach()
    {
        tracker.Add(5);
        tracker.Add(3);

        var result = await RunAsync();

        Assert.Equal(2, result.Done);
        Assert.Equal([3, 5], assistant.Started.Select(start => start.Task));
        Assert.Equal(["v0.2.0", "v0.2.0", "v0.2.0"], tracker.Read);
        Assert.Equal(3, workspace.Read - 2);
    }

    [Fact]
    public async Task ATaskThatOpensAfterAnotherIsTakenOnceThatOneIsClosed()
    {
        tracker.Add(3, blockedBy: [5]);
        tracker.Add(5);

        await RunAsync();

        Assert.Equal([5, 3], assistant.Started.Select(start => start.Task));
        Assert.Contains("✻ v0.2.0 · 2 tasks in the queue · the assistant's own model · high effort", log.Lines);
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
    public async Task WithNoTaskToTakeTheRunEndsAtOnce()
    {
        var result = await RunAsync();

        Assert.Equal(new RunResult(RunEnd.Emptied, 0), result);
        Assert.Equal(["✻ v0.2.0 · 0 tasks in the queue · the assistant's own model · high effort", "✔ No task is left · 0 done", "  Worked 0 s"], log.Lines);
        Assert.Equal(["No task is left: 0 done"], machine.Notified);
    }

    [Fact]
    public async Task WithoutAMilestoneTheTasksAreReadWhateverTheirs()
    {
        plan = plan with { Milestone = null, Rules = Rules with { Board = false }, Model = new ModelChoice("opus"), Effort = "max" };
        tracker.Add(3, status: BoardStatus.Missing);

        await RunAsync();

        Assert.All(tracker.Read, Assert.Null);
        Assert.Equal("✻ No milestone · 1 task in the queue · opus · max effort", log.Lines[0]);
        Assert.DoesNotContain(log.Lines, line => line.Contains("on the board", StringComparison.Ordinal));
    }

    [Fact]
    public async Task ADirtyTreeStopsTheRunBeforeATask()
    {
        tracker.Add(3);
        workspace.State = new WorkspaceState(2, 0);

        var result = await RunAsync();

        Assert.Equal(RunEnd.Problem, result.End);
        Assert.Equal("the working tree is not clean: commit or stash the changes first", result.Why);
        Assert.Contains("✖ Stopped: the working tree is not clean: commit or stash the changes first · the next task did not start · 0 done", log.Lines);
        Assert.Empty(assistant.Started);
        Assert.Empty(tracker.Did);
        Assert.Equal(["Stopped: the working tree is not clean: commit or stash the changes first"], machine.Notified);
    }

    [Fact]
    public async Task ATreeThatCannotBeReadStopsTheRun()
    {
        tracker.Add(3);
        workspace.State = null;

        Assert.Equal("the working tree cannot be read", (await RunAsync()).Why);
    }

    [Fact]
    public async Task ChangesLeftAfterATaskStopTheRunAndTheTaskStaysAsItIs()
    {
        tracker.Add(3);
        tracker.Add(5);
        workspace.Next.Enqueue(new WorkspaceState(0, 0));
        workspace.State = new WorkspaceState(1, 0);

        var result = await RunAsync();

        Assert.Equal("#3 left changes in the working tree · fake attach 3", result.Why);
        Assert.Null(state.Record);
        Assert.Equal(["status 3 InProgress"], tracker.Did);
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
        Assert.Equal(["status 3 InProgress"], tracker.Did);
    }

    [Fact]
    public async Task WhatIsLeftOfATaskDoneInPartBecomesANewTaskForTheOwner()
    {
        tracker.Add(3, labels: ["Feature", "urgent"]);
        tracker.Descriptions[3] = "Export the data.";
        assistant.Session(3).Works().Ends("Did the half.\nTOOBUSY-REST: Choose the format\nThe dates are left.\n- [ ] ISO?\nTOOBUSY: partial");

        var result = await RunAsync();

        Assert.Equal(new RunResult(RunEnd.Emptied, 1), result);
        var made = Assert.Single(tracker.Made);
        Assert.Equal("Choose the format", made.Title);
        Assert.Equal(["needs-owner", "Feature"], made.Labels);
        Assert.Equal("v0.2.0", made.Milestone);
        Assert.StartsWith("The dates are left.\n- [ ] ISO?\n\n---\n\nLeft from #3 “Task 3”", made.Description, StringComparison.Ordinal);
        Assert.Contains("<summary>The description of #3</summary>\n\nExport the data.", made.Description, StringComparison.Ordinal);
        Assert.Equal(["status 3 InProgress", "create 100", "comment 3", "close 3", "status 3 Done"], tracker.Did);
        Assert.StartsWith("**Done in part.** What is left is #100, which waits for the owner.\n\nDid the half.\n\n", tracker.Comments[0].Text, StringComparison.Ordinal);
        Assert.Contains("◐ #3 Task 3 — done in part in 3 s", log.Lines);
        Assert.Contains("  #3 is closed; what is left is #100 “Choose the format”, which waits for the owner with the needs-owner label", log.Lines);

        // The new task waits for the owner: the run does not take it.
        Assert.Equal([3], assistant.Started.Select(start => start.Task));
    }

    [Fact]
    public async Task APartThatDoesNotNameWhatIsLeftStillMakesTheTask()
    {
        tracker.Add(3);
        assistant.Session(3).Ends("Half of it.\nTOOBUSY: partial");

        await RunAsync();

        var made = Assert.Single(tracker.Made);
        Assert.Equal("What is left of “Task 3”", made.Title);
        Assert.StartsWith("Half of it.\n\n---", made.Description, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ATaskNothingCouldBeDoneOfGetsTheLabelOfTheOwnerAndGoesBack()
    {
        tracker.Add(3);
        tracker.Add(5);
        assistant.Session(3).Works().Ends("Which key is it?\nTOOBUSY: owner");

        var result = await RunAsync();

        Assert.Equal(new RunResult(RunEnd.Emptied, 1), result);
        Assert.Equal(["status 3 InProgress", "label 3 +needs-owner", "comment 3", "status 3 Todo"], tracker.Did.Take(4));
        Assert.StartsWith("**Waits for the owner.** Nothing could be done without you. Take the label `needs-owner` off", tracker.Comments[0].Text, StringComparison.Ordinal);
        Assert.Contains("◇ #3 Task 3 — waits for the owner after 3 s", log.Lines);
        Assert.Equal([3, 5], assistant.Started.Select(start => start.Task));
        Assert.Empty(tracker.Made);
    }

    [Fact]
    public async Task AFailedTaskStopsTheRunWithItsReason()
    {
        tracker.Add(3);
        tracker.Add(5);
        assistant.Session(3).Works().Ends("I tried twice.\nTOOBUSY: failed the tests do not pass");

        var result = await RunAsync();

        Assert.Equal(new RunResult(RunEnd.Problem, 0, "#3 failed · fake attach 3"), result);
        Assert.Contains("✖ #3 Task 3 — failed after 3 s: the tests do not pass", log.Lines);
        Assert.Contains("  │ I tried twice.", log.Lines);
        Assert.Equal(["status 3 InProgress"], tracker.Did);
        Assert.Single(assistant.Started);
        Assert.Equal([(3, RunMark.Failed, 3)], log.Ends);

        // A failure is for the owner: no run goes on with the task by itself.
        Assert.Equal([Record(3), null], state.Written);
    }

    [Fact]
    public async Task ASessionThatIsLostStopsTheRun()
    {
        tracker.Add(3);
        assistant.Session(3).Works().Lost("I was about to");

        var result = await RunAsync();

        Assert.Equal("the session of #3 did not say how the task went · fake attach 3", result.Why);
        Assert.Contains("✖ #3 Task 3 — ended after 3 s without saying how the task went", log.Lines);
        Assert.Null(state.Record);
    }

    [Fact]
    public async Task ASessionThatIsLostAfterItSaidHowTheTaskWentCounts()
    {
        tracker.Add(3);
        assistant.Session(3).Works().Lost(Done);

        Assert.Equal(new RunResult(RunEnd.Emptied, 1), await RunAsync());
    }

    [Fact]
    public async Task TheDescriptionOfATaskIsNotReadForATaskThatIsDone()
    {
        tracker.Add(3);
        tracker.Refused.Add("description");

        Assert.Equal(new RunResult(RunEnd.Emptied, 1), await RunAsync());
    }

    [Fact]
    public async Task ATrackerThatRefusesStopsTheRun()
    {
        tracker.Add(3);
        tracker.Add(5);
        tracker.Refused.Add("close");

        var result = await RunAsync();

        Assert.Equal(new RunResult(RunEnd.Problem, 0, "The tracker refused: close 3."), result);
        Assert.Single(assistant.Started);
        Assert.Equal([(3, RunMark.Failed)], Ended);
    }

    [Fact]
    public async Task ATrackerThatCannotBeReadStopsTheRun()
    {
        tracker.Refused.Add("read");

        Assert.Equal("The tracker refused: read.", (await RunAsync()).Why);
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
        Assert.Equal([Briefing.Alone(3)], session.Told);
        Assert.Equal(
            [
                "‖ #3 waits for the owner: input needed · it is told to go on alone in 90 s: /nudge does it now, /hold never · fake attach 3",
                "  │ CSV or JSON?",
                "▶ #3 is told to go on without the owner: its turn is cut, and this is the next message of its conversation · fake attach 3",
                $"  “{Briefing.Alone(3)}”",
                "✔ #3 Task 3 — done in 99 s",
            ],
            log.Lines.Skip(3).Take(5));
        Assert.Contains("#3 waits for an answer", machine.Notified);

        // What the session said stands in what the run is doing for as long as it waits, and no longer.
        Assert.Equal(["CSV or JSON?"], log.Statuses.First(status => status.Phase == RunPhase.WaitingForOwner).Reply);
        Assert.All(log.Statuses.Where(status => status.Phase != RunPhase.WaitingForOwner), status => Assert.Empty(status.Reply));
    }

    [Fact]
    public async Task ATurnThatEndsWithoutSayingHowTheTaskWentIsToldToo()
    {
        tracker.Add(3);
        var session = assistant.Session(3).Works().Ends("Shall I go on?").Then().Works().Ends(Done);

        var result = await RunAsync();

        Assert.Equal(1, result.Done);
        Assert.Single(session.Told);
        Assert.Contains("‖ #3 ended its turn without saying how the task went · it is told to go on alone in 90 s: /nudge does it now, /hold never · fake attach 3", log.Lines);
    }

    [Fact]
    public async Task NudgeTellsTheSessionAtOnce()
    {
        tracker.Add(3);
        var session = assistant.Session(3).Works().Asks().Then().Works().Ends(Done);
        clock.At(TimeSpan.FromSeconds(5), () => Run.Send(RunCommand.Nudge));

        await RunAsync();

        Assert.Single(session.Told);
        Assert.True(clock.Now - FakeClock.Start < TimeSpan.FromSeconds(20), "The session was told only after the countdown.");
    }

    [Fact]
    public async Task HoldCancelsTheCountdownAndTheSessionWaitsForTheOwner()
    {
        tracker.Add(3);
        var session = assistant.Session(3).Works().Asks().Then().Works().Ends(Done);
        clock.At(TimeSpan.FromSeconds(5), () => Run.Send(RunCommand.Hold));
        clock.At(TimeSpan.FromMinutes(20), () => Run.Send(RunCommand.Nudge));

        await RunAsync();

        Assert.Single(session.Told);
        Assert.True(clock.Now - FakeClock.Start >= TimeSpan.FromMinutes(20), "The session was told while it was held.");
        Assert.Contains("‖ #3 gets no message and waits for the owner · /nudge tells it to go on alone · fake attach 3", log.Lines);
    }

    [Fact]
    public async Task ASessionThatGoesBackToWorkIsNotTold()
    {
        tracker.Add(3);
        var session = assistant.Session(3).Works().Asks().Works(2).Ends(Done);

        var result = await RunAsync();

        Assert.Equal(1, result.Done);
        Assert.Empty(session.Told);
    }

    [Fact]
    public async Task ATaskIsToldToGoOnAloneOnlySoManyTimes()
    {
        policy = policy with { Messages = 2 };
        tracker.Add(3);
        var session = assistant.Session(3).Works().Asks().Then().Works().Asks().Then().Works().Asks(what: null);
        clock.At(TimeSpan.FromHours(1), kill.Cancel);

        var result = await RunAsync();

        Assert.Equal(RunEnd.Killed, result.End);
        Assert.Equal(2, session.Told.Count);
        Assert.Contains("‖ #3 waits for the owner: an answer is needed · it is left to the owner: it was told to go on alone 2 times · fake attach 3", log.Lines);
    }

    [Fact]
    public async Task MessagesThatBringNoStepOfItsOwnLeaveTheTaskToTheOwner()
    {
        tracker.Add(3);
        var session = assistant.Session(3).Works().Ends("What now?").Then().Ends("And now?").Then().Ends("Still here.");

        var result = await RunAsync();

        Assert.Equal(2, session.Told.Count);
        Assert.Equal("the session of #3 did not say how the task went · fake attach 3", result.Why);
        Assert.Contains("‖ #3 ended its turn without saying how the task went · it is left to the owner: 2 messages in a row brought no step of its own · fake attach 3", log.Lines);
    }

    [Fact]
    public async Task ASessionThatDoesNotGoOnWithTheMessageIsLost()
    {
        tracker.Add(3);
        var session = assistant.Session(3).Works().Asks();
        session.Deaf = true;

        var result = await RunAsync();

        Assert.Single(session.Told);
        Assert.Contains("✖ #3 did not go on with the message", log.Lines);
        Assert.Equal(RunEnd.Problem, result.End);
    }

    [Fact]
    public async Task StopFinishesTheTaskAndTakesNoMore()
    {
        tracker.Add(3);
        tracker.Add(5);
        assistant.Session(3).Works(4).Ends(Done);
        clock.At(TimeSpan.FromSeconds(4), () => Run.Send(RunCommand.Stop));

        var result = await RunAsync();

        Assert.Equal(new RunResult(RunEnd.Stopped, 1), result);
        Assert.Equal([3], assistant.Started.Select(start => start.Task));
        Assert.Contains("‖ Will stop after #3 · /continue takes tasks again", log.Lines);
        Assert.Contains("‖ Stopped by /stop · 1 done", log.Lines);
        Assert.Contains("Stopped: 1 done", machine.Notified);
    }

    [Fact]
    public async Task ContinueTakesTheStopBack()
    {
        tracker.Add(3);
        tracker.Add(5);
        assistant.Session(3).Works(4).Ends(Done);
        clock.At(TimeSpan.FromSeconds(4), () => Run.Send(RunCommand.Stop));
        clock.At(TimeSpan.FromSeconds(5), () => Run.Send(RunCommand.Stop));
        clock.At(TimeSpan.FromSeconds(7), () => Run.Send(RunCommand.Continue));
        clock.At(TimeSpan.FromSeconds(8), () => Run.Send(RunCommand.Continue));

        var result = await RunAsync();

        Assert.Equal(new RunResult(RunEnd.Emptied, 2), result);
        Assert.Contains("  Already stopping after the current task", log.Lines);
        Assert.Contains("▶ The queue goes on after the current task", log.Lines);
        Assert.Contains("  Nothing to take back: the queue goes on", log.Lines);
    }

    [Fact]
    public async Task AbortAsksTheSessionToWrapUpAndTheTaskIsMarkedAsInterrupted()
    {
        tracker.Add(3);
        tracker.Add(5);
        var session = assistant.Session(3).Works(20).WhenAsked().Works().Ends("Undone: a.cs.\nLeft: the export.\nTOOBUSY: interrupted");
        clock.At(TimeSpan.FromSeconds(4), () => Run.Send(RunCommand.Abort));

        var result = await RunAsync();

        Assert.Equal(new RunResult(RunEnd.Stopped, 0), result);
        Assert.Equal([Briefing.WrapUp(3)], session.Asked);
        Assert.Empty(session.Told);
        Assert.Equal(["status 3 InProgress", "label 3 +interrupted", "comment 3", "status 3 Todo"], tracker.Did);
        Assert.StartsWith("**Interrupted.** Nothing is committed; a run takes the task again.\n\nUndone: a.cs.\nLeft: the export.", tracker.Comments[0].Text, StringComparison.Ordinal);
        Assert.Equal([3], assistant.Started.Select(start => start.Task));
        Assert.Contains("■ Asked #3 to wrap up after its current step: nothing committed, a report in the task, the interrupted label", log.Lines);
        Assert.Contains("■ #3 Task 3 — interrupted after 9 s: the report is in the task, and the next run takes it first", log.Lines);
        Assert.Contains("  │ Undone: a.cs.", log.Lines);
        Assert.Equal("■ Stopped after /abort · 0 done", log.Lines[^2]);

        // The record stays, and says that the session wrapped up: the next run goes on with its conversation.
        Assert.Equal(Record(3, RecordState.WrappedUp), state.Record);
    }

    [Fact]
    public async Task AnAbortThatIsNotHeardCutsTheTurnOnceAfterAWhile()
    {
        tracker.Add(3);
        var session = assistant.Session(3).Works().Then().Works().Ends("Undone.\nTOOBUSY: interrupted");
        clock.At(TimeSpan.FromSeconds(4), () => Run.Send(RunCommand.Abort));

        var result = await RunAsync();

        Assert.Equal(RunEnd.Stopped, result.End);
        Assert.Single(session.Asked);
        Assert.Equal([Briefing.WrapUp(3)], session.Told);
        Assert.Contains("■ #3 did not wrap up in 10 min: its turn is cut, and it is asked again", log.Lines);
        Assert.Contains("label 3 +interrupted", tracker.Did);
    }

    [Fact]
    public async Task AnAbortOfASessionThatWaitsForTheOwnerCutsItsTurnAtOnce()
    {
        tracker.Add(3);
        var session = assistant.Session(3).Works().Asks().Then().Ends("Undone.\nTOOBUSY: interrupted");
        clock.At(TimeSpan.FromSeconds(10), () => Run.Send(RunCommand.Abort));

        var result = await RunAsync();

        Assert.Equal(RunEnd.Stopped, result.End);
        Assert.Empty(session.Asked);
        Assert.Equal([Briefing.WrapUp(3)], session.Told);
        Assert.Contains("■ #3 waits for the owner: its turn is cut, and it is told to wrap up", log.Lines);
        Assert.True(clock.Now - FakeClock.Start < TimeSpan.FromSeconds(60), "The abort waited for the countdown.");
    }

    [Fact]
    public async Task AnAbortedSessionThatEndsWithoutSayingSoIsNotToldToGoOnAlone()
    {
        tracker.Add(3);
        var session = assistant.Session(3).Works(20).WhenAsked().Ends("I stopped.");
        clock.At(TimeSpan.FromSeconds(4), () => Run.Send(RunCommand.Abort));

        var result = await RunAsync();

        Assert.Empty(session.Told);
        Assert.Equal("the session of #3 did not say how the task went · fake attach 3", result.Why);
    }

    [Fact]
    public async Task AnAbortBeforeAnythingRunsStopsTheQueue()
    {
        tracker.Add(3);
        Run.Send(RunCommand.Abort);

        var result = await RunAsync();

        Assert.Equal(new RunResult(RunEnd.Stopped, 0), result);
        Assert.Empty(assistant.Started);
        Assert.Equal(["■ Nothing is running: the queue stops before the next task", "■ Stopped after /abort · 0 done", "  Worked 0 s"], log.Lines);
    }

    [Fact]
    public async Task AnAbortCannotBeTakenBackAndIsAskedForOnce()
    {
        tracker.Add(3);
        assistant.Session(3).Works(20).WhenAsked().Works().Ends("Undone.\nTOOBUSY: interrupted");
        clock.At(TimeSpan.FromSeconds(4), () => Run.Send(RunCommand.Abort));
        clock.At(TimeSpan.FromSeconds(5), () =>
        {
            Run.Send(RunCommand.Continue);
            Run.Send(RunCommand.Abort);
            Run.Send(RunCommand.Stop);
            Run.Send(RunCommand.Nudge);
        });

        await RunAsync();

        Assert.Equal(
            ["  An /abort cannot be taken back", "  Already asked to wrap up", "  Already aborting", "  /nudge means nothing now"],
            log.Lines.Where(line => line.StartsWith("  A", StringComparison.Ordinal) || line.StartsWith("  /", StringComparison.Ordinal)));
    }

    [Fact]
    public async Task ASessionThatInterruptsItselfStopsTheRun()
    {
        tracker.Add(3);
        tracker.Add(5);
        assistant.Session(3).Works().Ends("I gave up.\nTOOBUSY: interrupted");

        var result = await RunAsync();

        Assert.Equal("#3 was interrupted, and nobody asked for it · fake attach 3", result.Why);
        Assert.Contains("label 3 +interrupted", tracker.Did);
        Assert.Single(assistant.Started);
    }

    [Fact]
    public async Task KillStopsTheSessionAndLeavesTheTaskAsItIs()
    {
        tracker.Add(3);
        var session = assistant.Session(3).Works();
        clock.At(TimeSpan.FromSeconds(10), kill.Cancel);

        var result = await RunAsync();

        Assert.Equal(new RunResult(RunEnd.Killed, 0, "#3 stays In Progress, and the next run goes on with its session · fake attach 3"), result);
        Assert.Equal(1, session.Stopped);
        Assert.Equal(["status 3 InProgress"], tracker.Did);
        Assert.Contains("■ Killed the session of #3: the task stays In Progress, and the next run goes on with its session · fake attach 3", log.Lines);

        // The record of the task stays: it is what the next run goes on by.
        Assert.Equal(Record(3), state.Record);
        Assert.False(machine.Awake);
        Assert.Equal([(3, RunMark.Interrupted)], Ended);
    }

    [Fact]
    public async Task ATaskThatWasInterruptedGoesFirstLosesTheLabelAndItsSessionIsToldSo()
    {
        tracker.Add(3);
        tracker.Add(5, labels: ["bug", "interrupted"]);

        await RunAsync();

        Assert.Equal([5, 3], assistant.Started.Select(start => start.Task));
        Assert.Equal(["status 5 InProgress", "label 5 -interrupted"], tracker.Did.Take(2));
        Assert.Contains("- This task was interrupted before.", assistant.Started[0].Message, StringComparison.Ordinal);
        Assert.DoesNotContain("interrupted before", assistant.Started[1].Message, StringComparison.Ordinal);
        Assert.Contains("→ #5 Task 5 — started · a new session; the task was interrupted before · fake attach 5", log.Lines);
    }

    [Fact]
    public async Task AnAssistantThatDoesNotStartStopsTheRunAndTheTaskGoesBack()
    {
        tracker.Add(3);
        assistant.RefuseToStart = "claude did not start #3: not logged in";

        var result = await RunAsync();

        Assert.Equal(new RunResult(RunEnd.Problem, 0, "claude did not start #3: not logged in"), result);
        Assert.Equal(["status 3 InProgress", "status 3 Todo"], tracker.Did);

        // A task whose session never started is not one that is over.
        Assert.Empty(log.Ends);
    }

    [Fact]
    public async Task BeforeATaskTheNearLimitIsWaitedOut()
    {
        tracker.Add(3);
        assistant.Limits = new UsageLimits(new UsageWindow("5-hour", 97.2, FakeClock.Start + TimeSpan.FromHours(2)), new UsageWindow("weekly", 40, null));

        var result = await RunAsync();

        Assert.Equal(1, result.Done);
        Assert.Contains("‖ The 5-hour limit is at 97%: waiting 2 h 01 min for its reset", log.Lines);
        Assert.Contains("▶ The 5-hour limit has reset", log.Lines);
        Assert.True(clock.Now - FakeClock.Start >= TimeSpan.FromMinutes(121));
        Assert.Contains(log.Statuses, status => status is { Phase: RunPhase.WaitingForLimit, Task: null } && status.Until == FakeClock.Start + TimeSpan.FromMinutes(121));
    }

    [Fact]
    public async Task ALimitBelowTheLineOrWithoutAKnownResetIsNotWaitedFor()
    {
        tracker.Add(3);
        tracker.Add(5);
        assistant.Limits = new UsageLimits(new UsageWindow("5-hour", 95.9, FakeClock.Start + TimeSpan.FromHours(2)), null);
        assistant.Session(3).Works(and: () => assistant.Limits = new UsageLimits(new UsageWindow("5-hour", 99, null), null)).Ends(Done);

        var result = await RunAsync();

        Assert.Equal(2, result.Done);
        Assert.True(clock.Now - FakeClock.Start < TimeSpan.FromMinutes(1));
    }

    [Fact]
    public async Task TheFarLimitHoldsNoTaskBack()
    {
        tracker.Add(3);
        tracker.Add(5);
        assistant.Limits = new UsageLimits(null, new UsageWindow("weekly", 100, FakeClock.Start + TimeSpan.FromDays(3)));

        var result = await RunAsync();

        Assert.Equal(new RunResult(RunEnd.Emptied, 2), result);
        Assert.Equal([3, 5], assistant.Started.Select(started => started.Task));
    }

    [Fact]
    public async Task AWindowWhoseResetHasPassedIsNotUsedAtAll()
    {
        tracker.Add(3);
        assistant.Limits = new UsageLimits(new UsageWindow("5-hour", 100, FakeClock.Start - TimeSpan.FromMinutes(1)), new UsageWindow("weekly", 100, FakeClock.Start));

        var result = await RunAsync();

        Assert.Equal(new RunResult(RunEnd.Emptied, 1), result);
    }

    [Fact]
    public async Task StopBreaksTheWaitForALimitBeforeATask()
    {
        tracker.Add(3);
        assistant.Limits = new UsageLimits(new UsageWindow("5-hour", 97, FakeClock.Start + TimeSpan.FromHours(2)), null);
        clock.At(TimeSpan.FromMinutes(10), () => Run.Send(RunCommand.Stop));

        var result = await RunAsync();

        Assert.Equal(new RunResult(RunEnd.Stopped, 0), result);
        Assert.Empty(assistant.Started);
        Assert.Contains("‖ The queue stops before the next task", log.Lines);
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
    public async Task AResetThatIsNotKnownIsWaitedForHalfAnHour()
    {
        tracker.Add(3);
        assistant.Session(3).Works().Limit().Then().Works().Ends(Done);

        await RunAsync();

        Assert.Contains("‖ #3 waits 30 min for the limit to reset, then goes on · /stop pauses it for the next run", log.Lines);
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
        Assert.Equal(["status 3 InProgress", "label 3 +interrupted", "status 3 Todo", "comment 3"], tracker.Did);
        Assert.Equal(Record(3, RecordState.Paused), state.Record);
        Assert.Equal([(3, RunMark.Paused)], Ended);
        Assert.StartsWith("**Stopped by a usage limit** (the limit did not reset after 2 waits).", tracker.Comments[0].Text, StringComparison.Ordinal);
        Assert.Contains("‖ #3 is paused: the limit did not reset after 2 waits. Its changes stay in the working tree, and the next run goes on with its session · 0 done", log.Lines);
        Assert.Contains("#3 is paused by a usage limit", machine.Notified);
        Assert.Equal([3], assistant.Started.Select(start => start.Task));
    }

    [Fact]
    public async Task AFarLimitThatIsSpentPausesTheTaskAtOnce()
    {
        tracker.Add(3);
        assistant.Session(3)
            .Works(and: () => assistant.Limits = new UsageLimits(null, new UsageWindow("weekly", 100, FakeClock.Start + TimeSpan.FromDays(1))))
            .Limit();

        var result = await RunAsync();

        Assert.Equal(RunEnd.Limited, result.End);
        Assert.Contains("‖ #3 is paused: the weekly limit is spent, it resets 2030-01-02 09:00 UTC. Its changes stay in the working tree, and the next run goes on with its session · 0 done", log.Lines);
        Assert.Equal(RecordState.Paused, state.Record?.State);
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
        assistant.Session(3).Works().Limit();
        clock.At(TimeSpan.FromMinutes(10), kill.Cancel);

        var result = await RunAsync();

        Assert.Equal(RunEnd.Killed, result.End);
        Assert.Equal(Record(3, RecordState.Paused), state.Record);
        Assert.Contains("label 3 +interrupted", tracker.Did);
        Assert.DoesNotContain(log.Lines, line => line.StartsWith("■ Killed", StringComparison.Ordinal));
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
        assistant.Session(5).Works().Ends("Half of it.\nTOOBUSY: partial");
        assistant.Session(7).Works().Ends("Which key?\nTOOBUSY: owner");

        await RunAsync();

        Assert.Equal(Record(3), atWork);
        Assert.Equal([Record(3), null, Record(5), null, Record(7), null], state.Written);
    }

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
        Assert.Equal(["status 5 InProgress", "comment 5", "close 5", "status 5 Done"], tracker.Did.Take(4));
        Assert.Equal(
            [
                "✻ v0.2.0 · 2 tasks in the queue · the assistant's own model · high effort",
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
        var session = assistant.Session(5).Ends("Half of it.\nTOOBUSY: partial");

        var result = await RunAsync();

        Assert.Equal(new RunResult(RunEnd.Emptied, 1), result);
        Assert.Equal(1, session.Looks);
        Assert.Empty(session.Told);
        Assert.Contains("→ #5 Task 5 — taken again · its session ended while nobody watched · fake attach 5", log.Lines);
        Assert.Contains("◐ #5 Task 5 — done in part in 0 s", log.Lines);
        Assert.Equal(["status 5 InProgress", "create 100", "comment 5", "close 5", "status 5 Done"], tracker.Did);
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
        Assert.Contains("✔ #5 Task 5 — done in 3 s", log.Lines);
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
        Assert.Equal([Briefing.Alone(5)], session.Told);
        Assert.Contains("→ #5 Task 5 — taken again · its session still works, and is watched again · fake attach 5", log.Lines);
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
        Assert.Equal(["status 5 InProgress", "label 5 -interrupted", "comment 5", "close 5", "status 5 Done"], tracker.Did.Take(5));
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
        Assert.Equal(["status 5 InProgress", "label 5 -interrupted"], tracker.Did.Take(2));
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
    public async Task ATaskOfTheRecordThatIsClosedIsNotTakenAndTheRecordIsForgotten()
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

    [Theory]
    [InlineData("needs-owner", "it waits for the owner: it has the needs-owner label")]
    [InlineData("manual", "it has the manual label")]
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

    [Fact]
    public async Task ASessionThatDoesNotGoOnIsLeftForTheNextRun()
    {
        state.Record = Record(5, RecordState.Paused);
        tracker.Add(5, labels: ["feature", "interrupted"]);
        workspace.State = new WorkspaceState(1, 0);
        assistant.Session(5).Deaf = true;

        var result = await RunAsync();

        Assert.Equal("the session of #5 did not go on · fake attach 5 · the next run tries again", result.Why);
        Assert.Equal(Record(5, RecordState.Paused), state.Record);
        Assert.Empty(tracker.Did);
    }

    [Fact]
    public async Task WhileTheSessionsCannotBeReadTheSessionOfTheRecordIsLeftAsItIs()
    {
        state.Record = Record(5);
        tracker.Add(5, status: BoardStatus.InProgress);
        var session = assistant.Session(5).Unseen();

        var result = await RunAsync();

        Assert.Equal("the sessions of the assistant cannot be read, so the session of #5 is left as it is · fake attach 5 · the next run looks again", result.Why);
        Assert.Equal(Record(5), state.Record);
        Assert.Empty(session.Told);
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

    [Fact]
    public async Task EveryTaskThatIsOverIsToldOnceWithHowItWentAndHowLongItTook()
    {
        tracker.Add(3);
        tracker.Add(5);
        tracker.Add(7);
        assistant.Session(3).Works().Ends(Done);
        assistant.Session(5).Works().Ends("Half of it.\nTOOBUSY: partial");
        assistant.Session(7).Works().Asks("input needed", "Which key is it?").Then().Works().Ends("Still: which key?\nTOOBUSY: owner");

        await RunAsync();

        Assert.Equal([(3, RunMark.Done, 3), (5, RunMark.Partial, 3), (7, RunMark.Owner, 99)], log.Ends);
    }

    [Fact]
    public async Task ATaskThatIsAbortedIsToldAsInterrupted()
    {
        tracker.Add(3);
        assistant.Session(3).Works(20).WhenAsked().Works().Ends("Undone.\nTOOBUSY: interrupted");
        clock.At(TimeSpan.FromSeconds(5), () => Run.Send(RunCommand.Abort));

        await RunAsync();

        Assert.Equal([(3, RunMark.Interrupted)], Ended);
    }

    [Fact]
    public async Task SessionsThatCannotBeReadAreSaidOnce()
    {
        tracker.Add(3);
        assistant.Session(3).Works().Unseen(3).Works().Ends(Done);

        var result = await RunAsync();

        Assert.Equal(1, result.Done);
        Assert.Single(log.Lines, line => line == "‖ The sessions of the assistant cannot be read: #3 may still be working · fake attach 3");
        Assert.Contains("▶ The sessions are read again: #3 is watched", log.Lines);

        // While they cannot be read, what the run is doing says so in the place of the step of the session.
        var steps = log.Statuses.Where(status => status.Phase == RunPhase.Working).Select(status => status.Step).ToList();
        Assert.Contains("its session cannot be read", steps);
        Assert.NotEqual("its session cannot be read", steps[^1]);
    }

    [Fact]
    public async Task ThePlanOfTheSessionIsInTheStatusAsItIsAtEveryLook()
    {
        tracker.Add(3);
        assistant.Session(3)
            .Works()
            .Works(plan: [PlanStep.Done, PlanStep.Active, PlanStep.Pending])
            .Works(plan: [PlanStep.Done, PlanStep.Done, PlanStep.Active, PlanStep.Pending])
            .Ends(Done);

        await RunAsync();

        var plans = log.Statuses.Where(status => status.Phase == RunPhase.Working).Select(status => string.Join(' ', status.Plan)).Distinct().ToList();
        Assert.Equal(["", "Done Active Pending", "Done Done Active Pending"], plans);
    }

    [Fact]
    public async Task TheStatusSaysWhatIsGoingOnAndWhichCommandsMeanSomething()
    {
        tracker.Add(3);
        tracker.Add(5);
        assistant.Session(3).Works(2).Asks().Then().Works().Ends(Done);
        clock.At(TimeSpan.FromSeconds(20), () => Run.Send(RunCommand.Stop));

        await RunAsync();

        Assert.Equal("Checking the working tree…", log.Statuses[0].Text);
        Assert.Contains(log.Statuses, status => status is { Phase: RunPhase.Preparing, Text: "Reading the queue…" });
        Assert.Contains(log.Statuses, status => status is { Phase: RunPhase.Preparing, Text: "Starting #3…", Task.Number: 3 });

        var working = log.Statuses.First(status => status.Phase == RunPhase.Working);
        Assert.Equal((3, "Editing a file", 1000L, 1, "fake attach 3"), (working.Task!.Number, working.Step, working.Context, working.Queued, working.Open));
        Assert.Equal(FakeClock.Start, working.Since);
        Assert.Equal(RunCommands.Stop | RunCommands.Abort, working.Available);

        var waiting = log.Statuses.First(status => status.Phase == RunPhase.WaitingForOwner);
        Assert.Equal(FakeClock.Start + TimeSpan.FromSeconds(96), waiting.Until);
        Assert.Equal(RunCommands.Stop | RunCommands.Abort | RunCommands.Nudge | RunCommands.Hold, waiting.Available);

        var stopping = log.Statuses.First(status => status.Stopping);
        Assert.Equal(RunCommands.Continue | RunCommands.Abort | RunCommands.Nudge | RunCommands.Hold, stopping.Available);

        Assert.Equal((RunPhase.Ended, RunCommands.None), (log.Statuses[^1].Phase, log.Statuses[^1].Available));
    }

    // The tasks that were told to be over, each with its mark.
    IEnumerable<(int Task, RunMark Mark)> Ended => log.Ends.Select(ended => (ended.Task, ended.Mark));

    // The record of a task as a run writes it for the session the fake assistant gives the task.
    static TaskRecord Record(int task, RecordState left = RecordState.Working) =>
        new(task, new SessionTrace($"fake-{task}", $"conversation-{task}", FakeClock.Start), left);

    Supervisor Run => run ??= new Supervisor(tracker, assistant, workspace, state, machine, clock, plan, policy);

    Task<RunResult> RunAsync() => Run.RunAsync(log, kill.Token);
}
