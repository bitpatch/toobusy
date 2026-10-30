using TooBusy.Core.Assistant;
using TooBusy.Core.Queue;
using TooBusy.Core.Run;
using TooBusy.Core.Settings;

namespace TooBusy.Core.Tests;

// A run of several sessions, on `TurnClock`, which has its flows take turns and moves the time only when all of them wait:
// tasks side by side in worktrees of their own, what stops the run while others work, what the owner asks of one or all
// of them, and what earlier runs left in worktrees.
public sealed class SeveralSessionsTests : IDisposable
{
    const string Done = "Did it.\nTOOBUSY: done";
    const string Failed = "It does not work.\nTOOBUSY: failed the build breaks";
    const string Interrupted = "Undone, left the rest.\nTOOBUSY: interrupted";

    static readonly QueueRules Rules = new(new LabelSettings(["manual"], ["feature", "bug"], "needs-owner", "interrupted"), Board: true);

    readonly TurnClock clock = new();
    readonly FakeTracker tracker = new();
    readonly FakeAssistant assistant = new();
    readonly FakeWorkspace workspace = new() { State = new WorkspaceState(0, 0) { Branch = "develop" } };
    readonly FakeMachine machine = new();
    readonly FakeState state = new();
    readonly FakeSign sign = new();
    readonly Log log = new();
    readonly FakeCommands shell;
    readonly FakeCopies copies;
    readonly CancellationTokenSource kill = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);

    RunPlan plan = new("v0.2.0", Rules, ModelChoice.AssistantsOwn, "high") { Sessions = 2 };

    readonly RunPolicy policy = new()
    {
        Poll = TimeSpan.FromSeconds(3),
        Tick = TimeSpan.FromSeconds(1),
        OwnerWait = TimeSpan.FromSeconds(90),
        Messages = 10,
        IdleMessages = 2,
        AbortGrace = TimeSpan.FromMinutes(10),
        Spent = 99,
        ResetMargin = TimeSpan.FromMinutes(1),
        UnknownLimitWait = TimeSpan.FromMinutes(30),
        LimitWaits = 5,
        CommandTime = TimeSpan.FromMinutes(15),
        KilledCommandTime = TimeSpan.FromSeconds(45),
        RetryPauses = [TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(30)],
    };

    Supervisor? run;

    public SeveralSessionsTests()
    {
        shell = new FakeCommands(clock);
        copies = new FakeCopies(assistant, clock);
    }

    public void Dispose()
    {
        kill.Dispose();
        foreach (var unplayed in assistant.Sessions.Select(session => session.Unplayed).OfType<string>())
            Assert.Fail($"The play went further than the run: {unplayed}.");
    }

    Supervisor Run => run ??= new Supervisor(tracker, assistant, workspace, state, sign, machine, clock, shell, plan, policy, copies);

    RunResult Go() => clock.Run(() => Run.RunAsync(log, kill.Token));

    // The numbers of the tasks shown as worked on at once, for each thing that was shown.
    IEnumerable<int[]> Shown => log.Statuses.Select(status => status.Tasks.Select(task => task.Task!.Number).ToArray());

    IEnumerable<int> Started => assistant.Started.Select(start => start.Task);

    static TaskRecord Record(int task, RecordState left = RecordState.Working) =>
        new(task, new SessionTrace($"fake-{task}", $"conversation-{task}", FakeClock.Start), left);

    [Fact]
    public void TasksGoOnSideBySideEachInAWorktreeOfItsOwn()
    {
        foreach (var number in (int[])[1, 2, 3])
            tracker.Add(number);
        assistant.Session(1).Works(3).Ends(Done);
        assistant.Session(2).Works(5).Ends(Done);
        assistant.Session(3).Works().Ends(Done);

        var result = Go();

        Assert.Equal(new RunResult(RunEnd.Emptied, 3), result);
        Assert.Equal([1, 2, 3], copies.Made);
        Assert.Equal([1, 3, 2], copies.Removed);
        Assert.Equal([1, 2, 3], Started);
        Assert.Contains(Shown, tasks => tasks.SequenceEqual([1, 2]));
        Assert.Contains(Shown, tasks => tasks.SequenceEqual([2, 3]));
        Assert.DoesNotContain(Shown, tasks => tasks.Length > 2);
        Assert.Contains("2 sessions at once", log.Lines[0], StringComparison.Ordinal);
        Assert.Contains("No task is left · 3 done", log.Text, StringComparison.Ordinal);

        // The working copy is left alone, and each worktree is brought up to date for its own task.
        Assert.Equal(0, workspace.Synced);
        Assert.Empty(state.Written);
        Assert.Equal(1, copies.Workspace(2).Synced);
        Assert.Equal(["develop"], copies.Asked);
    }

    [Fact]
    public void ASessionIsToldWhereItsWorkGoes()
    {
        tracker.Add(1);

        Go();

        var message = Assert.Single(assistant.Started).Message;
        Assert.Contains("the branch `toobusy/1`, which follows `origin/develop`", message, StringComparison.Ordinal);
        Assert.Contains("`git push origin HEAD:develop`", message, StringComparison.Ordinal);
    }

    [Fact]
    public void TheCommandsOfTheProjectRunInTheWorktreeOfTheTask()
    {
        plan = plan with { Commands = new TaskCommands("before", "after") };
        tracker.Add(1);
        tracker.Add(2);

        Go();

        Assert.Equal(["before", "after"], copies.Shell(1).Ran.Select(ran => ran.Command));
        Assert.Equal(["1", "1"], copies.Shell(1).Ran.Select(ran => ran.Task));
        Assert.Equal(["2", "2"], copies.Shell(2).Ran.Select(ran => ran.Task));
        Assert.Empty(shell.Ran);
    }

    [Fact]
    public void WhatFollowsThoseThatAreWorkedOnIsCountedOnTheStatus()
    {
        foreach (var number in (int[])[1, 2, 3, 4])
            tracker.Add(number);

        Go();

        Assert.Contains(log.Statuses, status => status.Beside.Count == 1 && status.Queued == 2);
    }

    [Fact]
    public void ATaskThatStopsTheRunLeavesTheSessionBesideItToFinish()
    {
        foreach (var number in (int[])[1, 2, 3])
            tracker.Add(number);
        assistant.Session(1).Works().Ends(Failed);
        assistant.Session(2).Works(4).Ends(Done);

        var result = Go();

        Assert.Equal(RunEnd.Problem, result.End);
        Assert.Equal(1, result.Done);
        Assert.Contains("#1 failed", result.Why, StringComparison.Ordinal);
        Assert.Equal([1, 2], Started);
        Assert.Contains("the sessions beside it finish, and the next task does not start", log.Text, StringComparison.Ordinal);
        Assert.Contains("#2 Task 2 — done", log.Text, StringComparison.Ordinal);
        Assert.Contains(machine.Notified, text => text.Contains("#1 failed", StringComparison.Ordinal));
        Assert.Contains(log.Statuses, status => status.Stopping && status.Task?.Number == 2);
    }

    [Fact]
    public void TheFirstProblemIsWhatEndedTheRun()
    {
        tracker.Add(1);
        tracker.Add(2);
        assistant.Session(1).Works().Ends(Failed);
        assistant.Session(2).Works(3).Ends("It broke too.\nTOOBUSY: failed the tests break");

        var result = Go();

        Assert.Equal(RunEnd.Problem, result.End);
        Assert.Contains("#1 failed", result.Why, StringComparison.Ordinal);
        Assert.Contains("#2 failed", log.Text, StringComparison.Ordinal);
    }

    [Fact]
    public void AWorktreeThatCannotBeMadeStopsTheRun()
    {
        tracker.Add(1);
        copies.Unmakeable = "the worktree of #1 cannot be made: no room";

        var result = Go();

        Assert.Equal(RunEnd.Problem, result.End);
        Assert.Equal("the worktree of #1 cannot be made: no room", result.Why);
        Assert.Empty(assistant.Started);
    }

    [Fact]
    public void AWorktreeWithWorkThatIsNotPushedStaysForTheOwner()
    {
        tracker.Add(1);
        tracker.Add(2);
        assistant.Session(1).Works().Ends(Done);
        assistant.Session(2).Works(3).Ends(Done);
        copies.Workspace(1).State = new WorkspaceState(0, 2) { Branch = "toobusy/1" };

        var result = Go();

        Assert.Equal(RunEnd.Problem, result.End);
        Assert.Equal([2], copies.Removed);
        Assert.Contains("#1 left 2 commits that are not pushed", result.Why, StringComparison.Ordinal);
        Assert.Contains("A worktree is left with work that is not pushed: /worktrees/1", log.Text, StringComparison.Ordinal);
    }

    [Fact]
    public void TheStopOfTheOwnerLetsAllTheSessionsFinish()
    {
        foreach (var number in (int[])[1, 2, 3])
            tracker.Add(number);
        assistant.Session(1).Works(3).Ends(Done);
        assistant.Session(2).Works(3).Ends(Done);
        clock.At(policy.Poll, () => Run.Send(RunCommand.Stop));

        var result = Go();

        Assert.Equal(new RunResult(RunEnd.Stopped, 2), result);
        Assert.Equal([1, 2], Started);
        Assert.Contains("Will stop after #1, #2", log.Text, StringComparison.Ordinal);
    }

    [Fact]
    public void AnAbortAsksEverySessionToWrapUp()
    {
        tracker.Add(1);
        tracker.Add(2);
        foreach (var number in (int[])[1, 2])
            assistant.Session(number).Works().WhenAsked().Works().Ends(Interrupted);
        clock.At(policy.Poll, () => Run.Send(RunCommand.Abort));

        var result = Go();

        Assert.Equal(RunEnd.Stopped, result.End);
        Assert.Single(assistant.Session(1).Asked);
        Assert.Single(assistant.Session(2).Asked);

        // Each stays for the next run with its record.
        Assert.Equal(RecordState.WrappedUp, copies.State(1).Record?.State);
        Assert.Equal(RecordState.WrappedUp, copies.State(2).Record?.State);
        Assert.Empty(copies.Removed);
        Assert.Contains("#1 stays in its worktree for the next run: /worktrees/1", log.Text, StringComparison.Ordinal);
    }

    [Fact]
    public void ACommandForASessionThatWaitsGoesToThatSession()
    {
        tracker.Add(1);
        tracker.Add(2);
        assistant.Session(1).Works().Asks();
        assistant.Session(2).Works().Asks().Then().Works().Ends(Done);
        clock.At(2 * policy.Poll, () =>
        {
            Run.Send(RunCommand.Hold, 1);
            Run.Send(RunCommand.Nudge, 2);
        });
        clock.At(policy.OwnerWait * 3, kill.Cancel);

        var result = Go();

        Assert.Equal(RunEnd.Killed, result.End);
        Assert.Empty(assistant.Session(1).Told);
        Assert.Single(assistant.Session(2).Told);
        Assert.Contains(log.Statuses, status => status.Beside.Any(beside => beside.Phase == RunPhase.WaitingForOwner));
    }

    [Fact]
    public void WhatMayBeAskedOfASessionIsSaidOfThatSession()
    {
        tracker.Add(1);
        tracker.Add(2);
        assistant.Session(1).Works(9).Ends(Done);
        assistant.Session(2).Works().Asks().Then().Works().Ends(Done);

        Go();

        var waiting = log.Statuses.First(status => status.Beside.Any(beside => beside.Phase == RunPhase.WaitingForOwner));
        Assert.True(waiting.Available.HasFlag(RunCommands.Nudge));
        Assert.True(waiting.Beside[0].Available.HasFlag(RunCommands.Nudge));
    }

    [Fact]
    public void AKillStopsEverySessionAndEachIsLeftForTheNextRun()
    {
        tracker.Add(1);
        tracker.Add(2);
        foreach (var number in (int[])[1, 2])
            assistant.Session(number).Works();
        clock.At(2 * policy.Poll, kill.Cancel);

        var result = Go();

        Assert.Equal(RunEnd.Killed, result.End);
        Assert.Equal(1, assistant.Session(1).Stopped);
        Assert.Equal(1, assistant.Session(2).Stopped);
        Assert.Equal(1, copies.State(1).Record?.Number);
        Assert.Equal(2, copies.State(2).Record?.Number);
        Assert.Empty(copies.Removed);
        Assert.Contains("#1 stays In Progress", result.Why, StringComparison.Ordinal);
        Assert.Contains("#2 stays In Progress", result.Why, StringComparison.Ordinal);
    }

    [Fact]
    public void NoTaskIsStartedWhileOneWaitsForAUsageLimit()
    {
        foreach (var number in (int[])[1, 2, 3])
            tracker.Add(number);
        assistant.Session(1).Works().Limit().Then().Works().Ends(Done);
        assistant.Session(2).Works(3).Ends(Done);

        var result = Go();

        Assert.Equal(new RunResult(RunEnd.Emptied, 3), result);
        var said = log.Text;
        Assert.True(said.IndexOf("#1 goes on after the reset", StringComparison.Ordinal) < said.IndexOf("#3 Task 3 — started", StringComparison.Ordinal));
        Assert.True(said.IndexOf("#2 Task 2 — done", StringComparison.Ordinal) < said.IndexOf("#1 goes on after the reset", StringComparison.Ordinal));
    }

    [Fact]
    public void ALimitThatPausesATaskEndsTheRunWhenTheSessionBesideItIsOver()
    {
        tracker.Add(1);
        tracker.Add(2);
        tracker.Add(3);
        assistant.Limits = new UsageLimits(null, new UsageWindow("weekly", 100, null));
        assistant.Session(1).Works().Limit();
        assistant.Session(2).Works(3).Ends(Done);

        var result = Go();

        Assert.Equal(RunEnd.Limited, result.End);
        Assert.Equal(1, result.Done);
        Assert.Contains("#1 is paused", result.Why, StringComparison.Ordinal);
        Assert.Equal(RecordState.Paused, copies.State(1).Record?.State);
        Assert.Equal([2], copies.Removed);
    }

    [Fact]
    public void ARunGoesOnWithTheWorktreesOfAnEarlierRunFirst()
    {
        tracker.Add(1);
        tracker.Add(5, status: BoardStatus.InProgress);
        tracker.Add(6, status: BoardStatus.InProgress);
        copies.Left(5, Record(5));
        copies.Left(6, Record(6, RecordState.Paused));
        assistant.Session(5).Lost().Then().Works().Ends(Done);
        assistant.Session(6).Then().Works().Ends(Done);

        var result = Go();

        Assert.Equal(new RunResult(RunEnd.Emptied, 3), result);
        Assert.Equal([1], copies.Made);
        Assert.Equal([1], Started);
        Assert.Equal([5, 6], assistant.Found.Select(trace => int.Parse(trace.Conversation!.Split('-')[^1], System.Globalization.CultureInfo.InvariantCulture)));
        Assert.Equal([1, 5, 6], copies.Removed.Order());
    }

    [Fact]
    public void TheRecordOfTheWorkingCopyIsGoneOnWithThereBesideTheWorktrees()
    {
        tracker.Add(1);
        tracker.Add(5, status: BoardStatus.InProgress);
        state.Record = Record(5);
        assistant.Session(5).Lost().Then().Works().Ends(Done);

        var result = Go();

        Assert.Equal(2, result.Done);
        Assert.Equal([1], copies.Made);
        Assert.Null(state.Record);
    }

    [Fact]
    public void ARecordWhoseTaskIsNotGoneOnWithIsForgottenAndItsWorktreeTakenAway()
    {
        tracker.Add(5, labels: ["manual"], status: BoardStatus.InProgress);
        copies.Left(5, Record(5));

        var result = Go();

        Assert.Equal(RunEnd.Emptied, result.End);
        Assert.Null(copies.State(5).Record);
        Assert.Equal([5], copies.Removed);
        Assert.Contains("#5 was left with a session by an earlier run, but it is not gone on with", log.Text, StringComparison.Ordinal);
    }

    [Fact]
    public void AClosedTaskOfAWorktreeIsSettledFromThere()
    {
        tracker.Add(5);
        tracker.Shut(5);
        copies.Left(5, Record(5));
        assistant.Session(5).Ends(Done);

        var result = Go();

        Assert.Equal(new RunResult(RunEnd.Emptied, 1), result);
        Assert.Equal([5], copies.Removed);
    }

    [Fact]
    public void WhatTasksThatAreOverLeftIsSeenTo()
    {
        copies.Left(7);
        copies.Left(8);
        copies.Workspace(8).State = new WorkspaceState(3, 0);

        Go();

        Assert.Equal([7], copies.Removed);
        Assert.Contains("A worktree is left with work that is not pushed: /worktrees/8", log.Text, StringComparison.Ordinal);
    }

    [Fact]
    public void ARunOfOneSessionFinishesWhatIsInWorktreesAndGoesOnInTheWorkingCopy()
    {
        plan = plan with { Sessions = 1 };
        tracker.Add(5, status: BoardStatus.InProgress);
        tracker.Add(6);
        copies.Left(5, Record(5));
        assistant.Session(5).Lost().Then().Works().Ends(Done);

        var result = Go();

        Assert.Equal(new RunResult(RunEnd.Emptied, 2), result);
        Assert.Empty(copies.Made);
        Assert.Equal([5], copies.Removed);
        Assert.Equal([6], Started);
    }

    [Fact]
    public void ARunOfOneSessionWithoutWorktreesAsksNothingOfThem()
    {
        plan = plan with { Sessions = 1 };
        tracker.Add(1);

        Assert.Equal(new RunResult(RunEnd.Emptied, 1), Go());

        Assert.Empty(copies.Made);
        Assert.Empty(copies.Asked);
    }

    [Fact]
    public void ABranchWithoutAnUpstreamHasNowhereToBringTheWorkBack()
    {
        copies.Followed = null;
        tracker.Add(1);

        var result = Go();

        Assert.Equal(RunEnd.Problem, result.End);
        Assert.Equal("the sessions of a run of several push to the remote, and develop has no upstream", result.Why);
        Assert.Empty(assistant.Started);
    }

    [Fact]
    public void AWorkingCopyOnNoBranchHasNothingToStartFrom()
    {
        workspace.State = new WorkspaceState(0, 0);
        tracker.Add(1);

        var result = Go();

        Assert.Equal(RunEnd.Problem, result.End);
        Assert.Equal("a run of several sessions starts from a branch, and the working copy is on none", result.Why);
    }

    [Fact]
    public void TheBranchOfTheSettingsIsWhatTheWorktreesStartFromWhateverIsCheckedOut()
    {
        plan = plan with { Branch = "main" };
        workspace.State = new WorkspaceState(4, 0) { Branch = "scratch" };
        tracker.Add(1);

        Assert.Equal(new RunResult(RunEnd.Emptied, 1), Go());

        Assert.Equal(["main"], copies.Asked);
    }

    [Fact]
    public void ARunOfANumberOfTasksStartsNoMoreThanThat()
    {
        plan = plan with { Most = 3 };
        foreach (var number in (int[])[1, 2, 3, 4, 5])
            tracker.Add(number);

        var result = Go();

        Assert.Equal(new RunResult(RunEnd.Stopped, 3, "Stopped after 3 tasks, as you asked"), result);
        Assert.Equal([1, 2, 3], Started);
    }

    [Fact]
    public void ARunOfOneTaskIsThatTaskAlone()
    {
        plan = plan with { Only = 2 };
        tracker.Add(1);
        tracker.Add(2);

        var result = Go();

        Assert.Equal(new RunResult(RunEnd.Stopped, 1, "Stopped after #2, as you asked"), result);
        Assert.Equal([2], Started);
    }

    [Fact]
    public void ARunOfOneTaskRefusesItWhileAWorktreeHoldsAnother()
    {
        plan = plan with { Only = 2 };
        tracker.Add(2);
        tracker.Add(5, status: BoardStatus.InProgress);
        copies.Left(5, Record(5));

        var result = Go();

        Assert.Equal(RunEnd.Problem, result.End);
        Assert.Contains("an earlier run left #5 with a session", result.Why, StringComparison.Ordinal);
        Assert.Empty(assistant.Started);
    }

    [Fact]
    public void ATaskThatComesUpAgainAfterItsSessionStopsTheRun()
    {
        plan = plan with { Rules = Rules with { Board = false } };
        tracker.ForgetsClosing = true;
        tracker.Add(1);
        tracker.Add(2);
        assistant.Session(1).Works().Ends(Done);
        assistant.Session(2).Works().Ends(Done);

        var result = Go();

        Assert.Equal(RunEnd.Problem, result.End);
        Assert.Contains("came up again after its session", result.Why, StringComparison.Ordinal);
    }

    [Fact]
    public void TheSignSaysEveryTaskThatIsWorkedOn()
    {
        tracker.Add(1);
        tracker.Add(2);
        assistant.Session(1).Works(3).Ends(Done);
        assistant.Session(2).Works(3).Ends(Done);

        Go();

        var both = sign.Written.OfType<RunSign>().First(written => written.Beside.Count == 1);
        Assert.Equal(1, both.Task);
        Assert.Equal(2, both.Beside[0].Task);
        Assert.Contains("#1 Task 1", both.Told, StringComparison.Ordinal);
        Assert.Contains("#2 Task 2", both.Told, StringComparison.Ordinal);
        Assert.Null(sign.Written[^1]);
    }

    [Fact]
    public void ASignChangesWithWhatItSaysAndNotWithEveryStepOfASession()
    {
        tracker.Add(1);
        tracker.Add(2);
        assistant.Session(1).Works(20).Ends(Done);
        assistant.Session(2).Works(20).Ends(Done);

        Go();

        Assert.True(sign.Written.Count < 20, $"The sign was written {sign.Written.Count} times.");
    }
}
