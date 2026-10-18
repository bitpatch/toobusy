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
    readonly FakeSign sign = new();
    readonly Log log = new();
    readonly FakeCommands shell;
    readonly CancellationTokenSource kill = new();

    RunPlan plan = new("v0.2.0", Rules, ModelChoice.AssistantsOwn, "high");
    RunPolicy policy = RunPolicy.Default;
    Supervisor? run;

    public SupervisorTests() => shell = new FakeCommands(clock);

    public void Dispose() => kill.Dispose();

    const string Free = "scripts/dev.sh ports free";
    const string Halt = "scripts/dev.sh server stop";

    static readonly TooBusy.Core.Settings.TaskCommands Around = new(Free, Halt);

    static readonly WorkspaceState OnDevelop = new(0, 0) { Branch = "develop" };

    [Fact]
    public async Task TheBranchIsBroughtUpToDateBeforeEveryTaskAndBeforeTheCommandBeforeIt()
    {
        plan = plan with { Branch = "develop", Commands = Around };
        workspace.State = OnDevelop;
        workspace.Sync = new BranchSync(SyncStatus.Moved, "origin/develop", 0, 3);
        var seen = new List<(int Commands, int Sessions, int Moved)>();
        workspace.Syncing = () => seen.Add((shell.Ran.Count, assistant.Started.Count, tracker.Did.Count));
        tracker.Add(3);
        tracker.Add(4);

        var result = await RunAsync();

        Assert.Equal(new RunResult(RunEnd.Emptied, 2), result);
        Assert.Equal([(0, 0, 0), (2, 1, 5)], seen);
        Assert.Equal(
            ["  develop is moved forward by 3 commits to origin/develop", "  Running the command before #3: scripts/dev.sh ports free"],
            log.Lines.Skip(1).Take(2));
        Assert.Equal(2, log.Lines.Count(line => line == "  develop is moved forward by 3 commits to origin/develop"));
        Assert.Contains(log.Statuses, status => status is { Phase: RunPhase.Preparing, Task.Number: 3, Step: "bringing the branch up to date" });
    }

    [Fact]
    public async Task ABranchThatHasWhatItsUpstreamHasIsNotSpokenOf()
    {
        plan = plan with { Branch = "develop" };
        workspace.State = OnDevelop;
        tracker.Add(3);

        var result = await RunAsync();

        Assert.Equal(1, result.Done);
        Assert.Equal(1, workspace.Synced);
        Assert.DoesNotContain(log.Lines, line => line.Contains("develop", StringComparison.Ordinal));
    }

    [Fact]
    public async Task ABranchWithoutAnUpstreamIsSaidOnceAndTheTasksGoOn()
    {
        plan = plan with { Branch = "develop" };
        workspace.State = OnDevelop;
        workspace.Sync = new BranchSync(SyncStatus.NoUpstream);
        tracker.Add(3);
        tracker.Add(4);

        var result = await RunAsync();

        Assert.Equal(2, result.Done);
        Assert.Equal(2, workspace.Synced);
        Assert.Single(log.Lines, line => line == "  develop has no upstream, so there is nothing to bring it up to date with");
    }

    [Theory]
    [InlineData(SyncStatus.Ahead, 1, 0, "", "develop has 1 commit that origin/develop lacks: push it first, or take it back")]
    [InlineData(SyncStatus.Ahead, 2, 0, "", "develop has 2 commits that origin/develop lacks: push them first, or take them back")]
    [InlineData(SyncStatus.Parted, 2, 3, "", "develop and origin/develop have parted: develop has 2 commits that origin/develop lacks, and lacks 3 commits of it: rebase or merge it first")]
    [InlineData(SyncStatus.Unreachable, 0, 0, "fatal: unable to access 'https://github.com/acme/rocket/': Could not resolve host: github.com", "origin/develop cannot be fetched: fatal: unable to access 'https://github.com/acme/rocket/': Could not resolve host: github.com")]
    [InlineData(SyncStatus.Failed, 0, 0, "origin/develop is not on the remote any more", "develop cannot be brought up to date with origin/develop: origin/develop is not on the remote any more")]
    public async Task ABranchThatCannotBeBroughtUpToDateStopsTheRunAndTheTaskIsNotTouched(SyncStatus status, int ahead, int behind, string said, string why)
    {
        plan = plan with { Branch = "develop", Commands = Around };
        workspace.State = OnDevelop;
        workspace.Sync = new BranchSync(status, "origin/develop", ahead, behind, said);
        tracker.Add(3);

        var result = await RunAsync();

        Assert.Equal(new RunResult(RunEnd.Problem, 0, $"{why} · #3 is not taken"), result);
        Assert.Empty(tracker.Did);
        Assert.Empty(assistant.Started);
        Assert.Empty(shell.Ran);
        Assert.Null(state.Record);
        Assert.Contains($"✖ Stopped: {why} · #3 is not taken · the next task did not start · 0 done", log.Lines);
        Assert.Empty(Ended);

        // A remote that refuses is not asked again.
        Assert.Equal(1, workspace.Synced);
    }

    [Fact]
    public async Task ARemoteThatDidNotAnswerIsFetchedAgainAfterAPause()
    {
        plan = plan with { Branch = "develop", Commands = Around };
        workspace.State = OnDevelop;
        workspace.Syncs.Enqueue(new BranchSync(SyncStatus.Unreachable, "origin/develop", Said: "fatal: unable to access 'https://github.com/acme/rocket/': Could not resolve host: github.com") { MayPass = true });
        workspace.Sync = new BranchSync(SyncStatus.Moved, "origin/develop", 0, 3);
        var paused = new List<RunStatus>();
        clock.At(TimeSpan.FromSeconds(5), () => paused.Add(log.Statuses[^1]));
        tracker.Add(3);

        var result = await RunAsync();

        Assert.Equal(new RunResult(RunEnd.Emptied, 1), result);
        Assert.Equal(2, workspace.Synced);
        Assert.Equal(
            ["▲ The remote did not answer: origin/develop is fetched again in 10 s", "  develop is moved forward by 3 commits to origin/develop", "  Running the command before #3: scripts/dev.sh ports free"],
            log.Lines.Skip(1).Take(3));

        // The page shows the task meanwhile, as it does while the branch is brought up to date.
        Assert.Equal((RunPhase.Preparing, 3, "bringing the branch up to date"), (paused[0].Phase, paused[0].Task?.Number, paused[0].Step));
    }

    [Fact]
    public async Task ARemoteThatDidNotAnswerThreeTimesStopsTheRunWithWhatGitSaidLast()
    {
        plan = plan with { Branch = "develop" };
        workspace.State = OnDevelop;
        workspace.Syncs.Enqueue(new BranchSync(SyncStatus.Unreachable, "origin/develop", Said: "fatal: early EOF") { MayPass = true });
        workspace.Syncs.Enqueue(new BranchSync(SyncStatus.Unreachable, "origin/develop", Said: "git did not end in 2 min") { MayPass = true });
        workspace.Sync = new BranchSync(SyncStatus.Unreachable, "origin/develop", Said: "ssh: connect to host github.com port 22: Operation timed out") { MayPass = true };
        var synced = new List<DateTimeOffset>();
        workspace.Syncing = () => synced.Add(clock.Now);
        tracker.Add(3);

        var result = await RunAsync();

        Assert.Equal(new RunResult(RunEnd.Problem, 0, "origin/develop cannot be fetched: ssh: connect to host github.com port 22: Operation timed out · #3 is not taken"), result);
        Assert.Equal([TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(30)], synced.Zip(synced.Skip(1), (before, after) => after - before));
        Assert.Equal(
            ["▲ The remote did not answer: origin/develop is fetched again in 10 s", "▲ The remote did not answer: origin/develop is fetched again in 30 s"],
            log.Lines.Where(line => line.StartsWith('▲')));
        Assert.Empty(tracker.Did);
        Assert.Empty(assistant.Started);
    }

    [Theory]
    [InlineData("feature/export", "the working copy is on feature/export, and the tasks are done on develop")]
    [InlineData(null, "the working copy is on no branch, and the tasks are done on develop")]
    public async Task AWorkingCopyOnAnotherBranchStopsTheRunBeforeTheQueueIsRead(string? branch, string why)
    {
        plan = plan with { Branch = "develop", Commands = Around };
        workspace.State = new WorkspaceState(0, 0) { Branch = branch };
        tracker.Add(3);

        var result = await RunAsync();

        Assert.Equal(new RunResult(RunEnd.Problem, 0, why), result);
        Assert.Empty(tracker.Read);
        Assert.Empty(tracker.Did);
        Assert.Empty(assistant.Started);
        Assert.Empty(shell.Ran);
        Assert.Equal(0, workspace.Synced);
        Assert.Equal($"✖ Stopped: {why} · the next task did not start · 0 done", log.Lines[0]);
    }

    [Fact]
    public async Task AWorkingCopyThatIsNotCleanOnAnotherBranchIsToldOfTheBranchFirst()
    {
        plan = plan with { Branch = "develop" };
        workspace.State = new WorkspaceState(2, 0) { Branch = "feature/export" };
        tracker.Add(3);

        var result = await RunAsync();

        Assert.Equal("the working copy is on feature/export, and the tasks are done on develop", result.Why);
    }

    [Fact]
    public async Task ATaskThatIsGoneOnWithIsCheckedForTheBranchAndNothingIsMovedUnderIt()
    {
        plan = plan with { Branch = "develop", Commands = Around };
        state.Record = Record(5);
        tracker.Add(5, status: BoardStatus.InProgress);
        workspace.Next.Enqueue(new WorkspaceState(2, 0) { Branch = "develop" });
        workspace.State = OnDevelop;
        var session = assistant.Session(5).Lost("I was about to").Then().Works().Ends(Done);

        var result = await RunAsync();

        Assert.Equal(1, result.Done);
        Assert.Equal([Briefing.AfterStop(5)], session.Told);
        Assert.Equal([Free, Halt], shell.Ran.Select(ran => ran.Command));
        Assert.Equal(0, workspace.Synced);
    }

    [Fact]
    public async Task ANewSessionForATaskWhoseConversationIsGoneIsNotBroughtUpToDateEither()
    {
        plan = plan with { Branch = "develop" };
        state.Record = Record(5);
        tracker.Add(5, status: BoardStatus.InProgress);
        tracker.Add(6);
        workspace.Next.Enqueue(new WorkspaceState(2, 0) { Branch = "develop" });
        workspace.State = OnDevelop;
        workspace.Sync = new BranchSync(SyncStatus.Moved, "origin/develop", 0, 1);
        assistant.Gone = true;

        var result = await RunAsync();

        Assert.Equal(2, result.Done);
        Assert.Equal(1, workspace.Synced);
        Assert.Equal(
            ["→ #5 Task 5 — taken again · a new session: the conversation of the one before is gone · fake attach 5", "  develop is moved forward by 1 commit to origin/develop"],
            log.Lines.Where(line => line.StartsWith('→') || line.Contains("moved forward", StringComparison.Ordinal)).Take(2));
    }

    [Fact]
    public async Task ATaskThatIsGoneOnWithOnAnotherBranchStopsTheRunAndKeepsItsRecord()
    {
        plan = plan with { Branch = "develop" };
        state.Record = Record(5);
        tracker.Add(5, status: BoardStatus.InProgress);
        workspace.State = new WorkspaceState(2, 0) { Branch = "feature/export" };

        var result = await RunAsync();

        Assert.Equal("the working copy is on feature/export, and the tasks are done on develop", result.Why);
        Assert.Equal(Record(5), state.Record);
        Assert.Empty(tracker.Did);
    }

    [Fact]
    public async Task AProjectThatNamesNoBranchHasItsTasksDoneOnWhateverIsCheckedOut()
    {
        workspace.State = new WorkspaceState(0, 0) { Branch = "feature/export" };
        tracker.Add(3);

        var result = await RunAsync();

        Assert.Equal(1, result.Done);
        Assert.Equal(0, workspace.Synced);
    }

    [Fact]
    public async Task TheCommandsOfTheProjectRunAroundATaskAndAreToldOfIt()
    {
        plan = plan with { Commands = Around };
        tracker.Add(3);
        tracker.Doing = did => Assert.True(did != "status 3 InProgress" || shell.Ran.Count == 1, "The task was moved before the command before it ran.");

        var result = await RunAsync();

        Assert.Equal(new RunResult(RunEnd.Emptied, 1), result);
        Assert.Equal([(Free, "3", policy.CommandTime), (Halt, "3", policy.CommandTime)], shell.Ran);
        Assert.Equal(
            [("TOOBUSY_TASK", "3"), ("TOOBUSY_TASK_TITLE", "Task 3"), ("TOOBUSY_TASK_URL", "https://example.com/3")],
            shell.Told[0].OrderBy(pair => pair.Key, StringComparer.Ordinal).Select(pair => (pair.Key, pair.Value)));
        Assert.Equal(
            """
            ✻ v0.2.0 · 1 task in the queue · the assistant's own model · high effort · auto mode
              Running the command before #3: scripts/dev.sh ports free
              the command before #3 passed in 2 s
            → #3 Task 3 — started · a new session · fake attach 3
              #3 is In Progress on the board and assigned to the current user; the session is told that toobusy keeps its task
            ✔ #3 Task 3 — done in 5 s
              #3 is closed, with the report of the session as its comment
              Running the command after #3: scripts/dev.sh server stop
              the command after #3 passed in 2 s
            ✔ No task is left · 1 done
              Worked 7 s
            """.ReplaceLineEndings("\n"),
            log.Text);
        Assert.Contains(log.Statuses, status => status is { Phase: RunPhase.Preparing, Task.Number: 3, Step: "the command before the task" });
        Assert.Contains(log.Statuses, status => status is { Phase: RunPhase.Preparing, Task.Number: 3, Step: "the command after the task" });
    }

    [Fact]
    public async Task ARunWithoutCommandsRunsNone()
    {
        tracker.Add(3);

        await RunAsync();

        Assert.Empty(shell.Ran);
        Assert.DoesNotContain(log.Lines, line => line.Contains("command", StringComparison.Ordinal));
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
    [InlineData(CommandStatus.TimedOut, "did not end in 10 min and was stopped")]
    [InlineData(CommandStatus.NotStarted, "did not start: the shell of the machine cannot be run")]
    public async Task ACommandThatIsGivenUpOrDoesNotStartFailsAsOneThatExitedWithAnError(CommandStatus status, string how)
    {
        plan = plan with { Commands = Around };
        shell.Ends[Free] = new CommandEnd(status, 0, "");
        tracker.Add(3);

        var result = await RunAsync();

        Assert.Equal($"the command before #3 {how} · #3 is not taken", result.Why);
        Assert.Empty(tracker.Did);
    }

    [Theory]
    [InlineData("Did a part, the rest is #9.\nTOOBUSY: partial", RunEnd.Emptied)]
    [InlineData("The owner must choose.\nTOOBUSY: owner", RunEnd.Emptied)]
    [InlineData("Undone.\nTOOBUSY: interrupted", RunEnd.Problem)]
    [InlineData("It broke.\nTOOBUSY: failed the build is broken", RunEnd.Problem)]
    [InlineData("I am done, I think.", RunEnd.Problem)]
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
    public async Task AKillRunsTheCommandAfterTheTaskAndDoesNotWaitLongForIt()
    {
        plan = plan with { Commands = Around };
        tracker.Add(3);
        var session = assistant.Session(3).Works();
        clock.At(TimeSpan.FromSeconds(10), kill.Cancel);

        var result = await RunAsync();

        Assert.Equal(new RunResult(RunEnd.Killed, 0, "#3 stays In Progress, and the next run goes on with its session · fake attach 3"), result);
        Assert.Equal(1, session.Stopped);
        Assert.Equal([(Free, "3", policy.CommandTime), (Halt, "3", policy.KilledCommandTime)], shell.Ran);
        Assert.Contains("  the command after #3 passed in 2 s", log.Lines);
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
        Assert.Equal(["status 3 InProgress", "assign 3", "comment 3", "close 3", "status 3 Done"], tracker.Did);
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

    [Fact]
    public async Task TheStatusesAreNamedToTheSessionAndInTheLogAsTheSettingsNameThem()
    {
        plan = plan with { Rules = Rules with { Statuses = new BoardStatuses("Ready", "Doing", "Shipped") } };
        tracker.Add(3);

        await RunAsync();

        var start = Assert.Single(assistant.Started);
        Assert.Contains("It has already moved the task to Doing, and it assigns the task to the user who runs it.", start.Message, StringComparison.Ordinal);
        Assert.Contains("  #3 is Doing on the board and assigned to the current user; the session is told that toobusy keeps its task", log.Lines);
        Assert.DoesNotContain("In Progress", start.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("Todo", start.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ATaskIsMovedBeforeItsSessionStartsAndClosedWithItsReportWhenItIsDone()
    {
        tracker.Add(3);

        var result = await RunAsync();

        Assert.Equal(new RunResult(RunEnd.Emptied, 1), result);
        Assert.Equal(["status 3 InProgress", "assign 3", "comment 3", "close 3", "status 3 Done"], tracker.Did);
        Assert.Equal((3, "**Done.**\n\nDid it.\n\n_Session: `fake attach 3`_"), Assert.Single(tracker.Comments));
        var start = Assert.Single(assistant.Started);
        Assert.Equal((3, "#3 Task 3", ModelChoice.AssistantsOwn, "high"), (start.Task, start.Name, start.Model, start.Effort));
        Assert.Contains("# Task #3: Task 3\n\nhttps://example.com/3\n\nRead the task and its comments before you start", start.Message, StringComparison.Ordinal);
        Assert.Contains("It has already moved the task to In Progress, and it assigns the task to the user who runs it.", start.Message, StringComparison.Ordinal);
        Assert.Equal(1, assistant.Session(3).Stopped);
        Assert.Equal(
            """
            ✻ v0.2.0 · 1 task in the queue · the assistant's own model · high effort · auto mode
            → #3 Task 3 — started · a new session · fake attach 3
              #3 is In Progress on the board and assigned to the current user; the session is told that toobusy keeps its task
            ✔ #3 Task 3 — done in 3 s
              #3 is closed, with the report of the session as its comment
            ✔ No task is left · 1 done
              Worked 3 s
            """.ReplaceLineEndings("\n"),
            log.Text);
    }

    [Fact]
    public async Task InAProjectWithASkillASessionStartsWithItsCommand()
    {
        plan = plan with { Skill = "task-auto" };
        tracker.Add(3);

        await RunAsync();

        Assert.StartsWith("/task-auto #3 Task 3\n\nYou are doing task #3 of this project without its owner.", Assert.Single(assistant.Started).Message, StringComparison.Ordinal);
        Assert.Contains("→ #3 Task 3 — started · a new session with the skill task-auto · fake attach 3", log.Lines);
    }

    [Fact]
    public async Task WithoutASkillTheFirstMessageBeginsWithTheWordsOfToobusy()
    {
        tracker.Add(3);

        await RunAsync();

        Assert.StartsWith("You are doing task #3 of this project without its owner.", Assert.Single(assistant.Started).Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task WithoutABoardATaskThatIsTakenIsAssignedAllTheSame()
    {
        plan = plan with { Rules = Rules with { Board = false } };
        tracker.Add(3);

        var result = await RunAsync();

        Assert.Equal(new RunResult(RunEnd.Emptied, 1), result);
        Assert.Contains("assign 3", tracker.Did);
        Assert.Contains("  #3 is assigned to the current user; the session is told that toobusy keeps its task", log.Lines);
    }

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
        Assert.Equal(["status 3 InProgress", "label 3 -interrupted", "comment 3", "close 3", "status 3 Done"], tracker.Did.Take(5));
        Assert.Empty(assistant.Session(3).Told);
        Assert.Contains("▲ The tracker refused: assign 3. · the run goes on", log.Lines);
        Assert.Contains("▲ The tracker refused: assign 5. · the run goes on", log.Lines);
        Assert.DoesNotContain(log.Lines, line => line.Contains("assigned to the current user", StringComparison.Ordinal));
        if (note is not null)
            Assert.Contains(note, log.Lines);
        else
            Assert.DoesNotContain(log.Lines, line => line.Contains("keeps its task", StringComparison.Ordinal));
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
    public async Task ARunThatIsStoppedCountsATaskDoneInPartApart()
    {
        tracker.Add(3);
        tracker.Add(5);
        assistant.Session(3).Works(4).Ends("Half of it; the rest is #9.\nTOOBUSY: partial");
        clock.At(TimeSpan.FromSeconds(4), () => Run.Send(RunCommand.Stop));

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
        Assert.Contains(log.Lines, line => line.StartsWith("‖ #5 is paused: the weekly limit is spent", StringComparison.Ordinal) && line.EndsWith("and the next run goes on with its session · 0 done · 1 done in part", StringComparison.Ordinal));
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
    public async Task TheLogSaysThePermissionModeOfTheSessionsWhereItSaysTheModelAndTheEffort()
    {
        plan = plan with { PermissionMode = "dontAsk" };
        tracker.Add(3);

        await RunAsync();

        Assert.Equal("✻ v0.2.0 · 1 task in the queue · the assistant's own model · high effort · dontAsk mode", log.Lines[0]);
    }

    [Fact]
    public async Task WithoutAMilestoneTheTasksAreReadWhateverTheirs()
    {
        plan = plan with { Milestone = null, Rules = Rules with { Board = false }, Model = new ModelChoice("opus"), Effort = "max" };
        tracker.Add(3, status: BoardStatus.Missing);

        await RunAsync();

        Assert.All(tracker.Read, Assert.Null);
        Assert.Equal("✻ No milestone · 1 task in the queue · opus · max effort · auto mode", log.Lines[0]);
        Assert.DoesNotContain(log.Lines, line => line.Contains("on the board", StringComparison.Ordinal));
    }

    [Fact]
    public async Task TheFirstMessageNamesWhatANewTaskNeeds()
    {
        plan = plan with { Board = "https://github.com/orgs/acme/projects/4" };
        tracker.Add(3, labels: ["Feature", "urgent"]);

        await RunAsync();

        var message = Assert.Single(assistant.Started).Message;
        Assert.Contains("- Put the label `needs-owner` on it, so that no run takes it before the owner answers, and the label `Feature`, as task #3 has, so that a run takes it after that.\n", message, StringComparison.Ordinal);
        Assert.Contains("- Put it into the milestone `v0.2.0`, the one the owner works on.\n- Put it on the board https://github.com/orgs/acme/projects/4 with the status `Todo`.\n", message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("v0.3.0", "- Put it into the milestone `v0.3.0`, the one of task #3.\n")]
    [InlineData(null, "- Put it into no milestone, as task #3 is in none.\n")]
    public async Task WithoutAMilestoneANewTaskGoesWhereTheTaskOfTheSessionIs(string? milestone, string told)
    {
        plan = plan with { Milestone = null };
        tracker.Add(3, milestone: milestone);

        await RunAsync();

        Assert.Contains(told, Assert.Single(assistant.Started).Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AProjectWithoutABoardNamesNoneToASession()
    {
        plan = plan with { Rules = Rules with { Board = false }, Board = null };
        tracker.Add(3);

        await RunAsync();

        Assert.DoesNotContain("on the board", Assert.Single(assistant.Started).Message, StringComparison.Ordinal);
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
            (3, "**Failed.** The run stopped over this task: its session left changes in the working tree. Look at what went wrong, take the label `needs-owner` off, and a run takes the task again.\n\nDid it.\n\n_Session: `fake attach 3`_"),
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
    [InlineData("Did the half.\n\nLeft: https://github.com/acme/rocket/issues/41")]
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

    [Theory]
    [InlineData("Half of it.")]
    [InlineData("Half of #3; the rest is for the owner.")]
    public async Task APartWhoseReportNamesNoOtherTaskStopsTheRunAndLeavesTheTaskToTheOwner(string report)
    {
        tracker.Add(3);
        tracker.Add(5);
        assistant.Session(3).Works().Ends($"{report}\nTOOBUSY: partial");

        var result = await RunAsync();

        Assert.Equal(RunEnd.Problem, result.End);
        Assert.Equal("#3 said that a part is done and named no task of what is left · fake attach 3", result.Why);
        Assert.Equal(["status 3 InProgress", "assign 3", "label 3 +needs-owner", "comment 3", "status 3 Todo"], tracker.Did);
        Assert.StartsWith(
            $"**Failed.** The run stopped over this task: its session said that a part is done and named no task of what is left. Look at what went wrong, take the label `needs-owner` off, and a run takes the task again.\n\n{report}",
            tracker.Comments[0].Text,
            StringComparison.Ordinal);
        Assert.Equal([3], assistant.Started.Select(start => start.Task));
    }

    [Fact]
    public async Task ATaskNothingCouldBeDoneOfGetsTheLabelOfTheOwnerAndGoesBack()
    {
        tracker.Add(3);
        tracker.Add(5);
        assistant.Session(3).Works().Ends("Which key is it?\nTOOBUSY: owner");

        var result = await RunAsync();

        Assert.Equal(new RunResult(RunEnd.Emptied, 1), result);
        Assert.Equal(["status 3 InProgress", "assign 3", "label 3 +needs-owner", "comment 3", "status 3 Todo"], tracker.Did.Take(5));
        Assert.StartsWith("**Waits for the owner.** Nothing could be done without you. Take the label `needs-owner` off", tracker.Comments[0].Text, StringComparison.Ordinal);
        Assert.Contains("◇ #3 Task 3 — waits for the owner after 3 s", log.Lines);
        Assert.Equal([3, 5], assistant.Started.Select(start => start.Task));
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
        Assert.Equal(["status 3 InProgress", "assign 3", "label 3 +needs-owner", "comment 3", "status 3 Todo"], tracker.Did);
        Assert.Equal(
            (3, "**Failed.** The run stopped over this task: its session failed: the tests do not pass. Look at what went wrong, take the label `needs-owner` off, and a run takes the task again.\n\nI tried twice.\n\n_Session: `fake attach 3`_"),
            Assert.Single(tracker.Comments));
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
        Assert.Equal(["status 3 InProgress", "assign 3", "label 3 +needs-owner", "comment 3", "status 3 Todo"], tracker.Did);
        Assert.Equal(
            (3, "**Failed.** The run stopped over this task: its session ended without saying how the task went. Look at what went wrong, take the label `needs-owner` off, and a run takes the task again.\n\nI was about to\n\n_Session: `fake attach 3`_"),
            Assert.Single(tracker.Comments));
    }

    [Theory]
    [InlineData("I tried twice.\nTOOBUSY: failed the tests do not pass", 0)]
    [InlineData("What now?", 0)]
    [InlineData(Done, 1)]
    public async Task ATaskThatARunStoppedOverIsNotTakenByTheNextRunInAProjectWithoutABoard(string reply, int changes)
    {
        plan = plan with { Rules = Rules with { Board = false } };
        policy = policy with { Messages = 0 };
        tracker.Add(3);
        tracker.Add(5);
        assistant.Session(3).Works().Ends(reply);
        workspace.Next.Enqueue(new WorkspaceState(0, 0));
        workspace.Next.Enqueue(new WorkspaceState(changes, 0));

        Assert.Equal(RunEnd.Problem, (await RunAsync()).End);
        Assert.Equal([3], assistant.Started.Select(start => start.Task));

        // The next run, once the owner has put the working copy right: the label holds the task, and the next one
        // is taken.
        run = null;
        log.Lines.Clear();
        var result = await RunAsync();

        Assert.Equal(new RunResult(RunEnd.Emptied, 1), result);
        Assert.Equal([3, 5], assistant.Started.Select(start => start.Task));
        Assert.Contains("  Held: #3 it waits for the owner: it has the needs-owner label", log.Lines);

        // The owner takes the label off, and a run takes the task as any other.
        await tracker.RemoveLabelAsync(3, "needs-owner", CancellationToken.None);
        run = null;
        await RunAsync();

        Assert.Equal([3, 5, 3], assistant.Started.Select(start => start.Task));
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
        Assert.Contains("✖ Stopped: #3 failed · fake attach 3 · the next task did not start · 0 done", log.Lines);
        Assert.Equal(did.Split('|'), tracker.Did);
    }

    [Fact]
    public async Task ASessionThatIsLostAfterItSaidHowTheTaskWentCounts()
    {
        tracker.Add(3);
        assistant.Session(3).Works().Lost(Done);

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

        // The record is forgotten, so the task is left to the owner: nothing else keeps the next run from it.
        Assert.Equal(["status 3 InProgress", "assign 3", "comment 3", "label 3 +needs-owner", "comment 3", "status 3 Todo"], tracker.Did);
        Assert.StartsWith("**Failed.** The run stopped over this task: the tracker refused something after its session: The tracker refused: close 3.", tracker.Comments[^1].Text, StringComparison.Ordinal);
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
        var session = assistant.Session(3).Works();

        var result = await RunAsync();

        var why = "the record of #3 cannot be written: No space left on device · the session of #3 is stopped · fake attach 3";
        Assert.Equal(new RunResult(RunEnd.Problem, 0, why), result);
        Assert.Equal(1, session.Stopped);
        Assert.Equal(0, session.Looks);
        Assert.Single(assistant.Started);
        Assert.Equal(["status 3 InProgress"], tracker.Did);
        Assert.Contains($"✖ Stopped: {why} · the next task did not start · 0 done", log.Lines);
        Assert.Equal([$"Stopped: {why}"], machine.Notified);

        // The command after the task runs as after any other end of it, and the sign comes down.
        Assert.Equal([Free, Halt], shell.Ran.Select(ran => ran.Command));
        Assert.Null(sign.Written[^1]);
        Assert.Equal(RunPhase.Ended, log.Statuses[^1].Phase);
        Assert.False(machine.Awake);
    }

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
        Assert.Contains($"✖ Stopped: {why} · the next task did not start · 0 done", log.Lines);

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
        Assert.Contains($"✖ Stopped: {why} · the next task did not start · 0 done", log.Lines);
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
        Assert.Contains($"✖ Stopped: {why} · the next task did not start · 0 done", log.Lines);
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
    [InlineData("read", "the open tasks are read")]
    [InlineData("status 3 InProgress", "#3 is moved to In Progress")]
    [InlineData("assign", "#3 is assigned to the current user")]
    [InlineData("close", "#3 is closed")]
    [InlineData("status 3 Done", "#3 is moved to Done")]
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
                "▲ GitHub did not answer: the label interrupted is taken off #3 again in 10 s",
                "▲ GitHub did not answer: #3 is read again in 10 s",
                "▲ GitHub did not answer: the label needs-owner is put on #3 again in 10 s",
                "▲ GitHub did not answer: #3 is moved to Todo again in 10 s",
            ],
            log.Lines.Where(line => line.StartsWith('▲')));
        Assert.Equal(["status 3 InProgress", "assign 3", "label 3 -interrupted", "label 3 +needs-owner", "comment 3", "status 3 Todo"], tracker.Did);
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
        Assert.Equal([TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(30)], tried.Zip(tried.Skip(1), (before, after) => after - before));
        Assert.Equal(
            ["▲ GitHub did not answer: #3 is closed again in 10 s", "▲ GitHub did not answer: #3 is closed again in 30 s"],
            log.Lines.Where(line => line.StartsWith('▲')));
        Assert.Contains("✖ Stopped: The tracker did not answer: close 3 (3). · the next task did not start · 0 done", log.Lines);
        Assert.Single(assistant.Started);

        // The task is left to the owner as over any tracker that refuses.
        Assert.Equal(["status 3 InProgress", "assign 3", "comment 3", "label 3 +needs-owner", "comment 3", "status 3 Todo"], tracker.Did);
    }

    [Fact]
    public async Task AFailureThatStaysIsToldAtOnce()
    {
        tracker.Add(3);
        tracker.Refused.Add("close");

        var result = await RunAsync();

        Assert.Equal("The tracker refused: close 3.", result.Why);
        Assert.Single(tracker.Asked, asked => asked == "close 3");
        Assert.DoesNotContain(log.Lines, line => line.Contains("did not answer", StringComparison.Ordinal));
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
        Assert.Equal(["status 3 InProgress", "assign 3", "label 3 +needs-owner", "comment 3", "status 3 Todo"], tracker.Did);
        Assert.StartsWith("**Failed.**", Assert.Single(tracker.Comments).Text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AKillEndsAPauseAtOnce()
    {
        tracker.Add(3);
        tracker.Unanswered["read"] = 1;
        var paused = new List<RunStatus>();
        clock.At(TimeSpan.FromSeconds(5), () =>
        {
            paused.Add(log.Statuses[^1]);
            kill.Cancel();
        });

        var result = await RunAsync();

        Assert.Equal(RunEnd.Killed, result.End);
        Assert.Single(tracker.Read);
        Assert.Empty(assistant.Started);
        Assert.Contains("▲ GitHub did not answer: the open tasks are read again in 10 s", log.Lines);

        // Between tasks the page says what the run does, as it did before the pause.
        Assert.Equal((RunPhase.Preparing, "Reading the queue…", null), (paused[0].Phase, paused[0].Text, paused[0].Task));
        Assert.Equal(RunPhase.Preparing, Assert.Single(sign.Written, written => written is not null)!.Phase);
    }

    [Fact]
    public async Task AKillInAPauseWhileATaskIsTakenStopsItsSession()
    {
        tracker.Add(3);
        tracker.Unanswered["assign"] = 1;
        clock.At(TimeSpan.FromSeconds(5), kill.Cancel);

        var result = await RunAsync();

        Assert.Equal(new RunResult(RunEnd.Killed, 0, "#3 was being taken, and the next run goes on with its session · fake attach 3"), result);
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
        clock.At(TimeSpan.FromMinutes(10), kill.Cancel);

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
                "‖ #3 waits for the owner: input needed · it is told to go on alone in 90 s: /nudge does it now, /hold never · fake attach 3",
                "  │ CSV or JSON?",
                "▶ #3 is told to go on without the owner: its turn is cut, and this is the next message of its conversation · fake attach 3",
                $"  “{Briefing.Alone(3, "needs-owner")}”",
                "✔ #3 Task 3 — done in 99 s",
            ],
            log.Lines.Skip(3).Take(5));
        Assert.Contains("#3 waits for an answer", machine.Notified);

        // What the session said stands in what the run is doing for as long as it waits, and no longer.
        Assert.Equal(["CSV or JSON?"], log.Statuses.First(status => status.Phase == RunPhase.WaitingForOwner).Reply);
        Assert.All(log.Statuses.Where(status => status.Phase != RunPhase.WaitingForOwner), status => Assert.Empty(status.Reply));
    }

    const string Half = "Half of it; the rest is #9.\nTOOBUSY: partial";

    [Fact]
    public async Task ASessionMarkedAsWaitingWhoseReplyEndsWithTheLineIsToldOnceAndIsOverAtItsNextReply()
    {
        tracker.Add(3);
        var session = assistant.Session(3).Works().Asks("input needed", Half).Then().Asks("input needed", Half);

        var result = await RunAsync();

        Assert.Equal((RunEnd.Emptied, 0, 1), (result.End, result.Done, result.Partial));
        Assert.Equal([Briefing.NoAnswer(3, "needs-owner")], session.Told);
        Assert.Equal(
            [
                "▲ #3 ended its turn with a line for toobusy, but Claude Code marks it as waiting for an answer · it is told that none will come · fake attach 3",
                "  │ Half of it; the rest is #9.",
                $"  “{Briefing.NoAnswer(3, "needs-owner")}”",
                "◐ #3 Task 3 — done in part in 6 s",
            ],
            log.Lines.Skip(3).Take(4));
        Assert.Equal(["status 3 InProgress", "assign 3", "comment 3", "close 3", "status 3 Done"], tracker.Did);
        Assert.Equal([(3, RunMark.Partial)], Ended);

        // Nothing is asked of the owner: no countdown, no commands for a session that waits, and no notification.
        Assert.DoesNotContain(log.Statuses, status => status.Phase == RunPhase.WaitingForOwner);
        Assert.DoesNotContain("#3 waits for an answer", machine.Notified);
    }

    [Fact]
    public async Task ASessionThatAsksAfterTheLineWaitsForTheOwnerAsBefore()
    {
        tracker.Add(3);
        var session = assistant.Session(3).Works().Asks("input needed", $"{Half}\n\nShall I take #9 too?").Then().Works().Ends(Half);

        var result = await RunAsync();

        Assert.Equal(1, result.Partial);
        Assert.Equal([Briefing.Alone(3, "needs-owner")], session.Told);
        Assert.Contains("‖ #3 waits for the owner: input needed · it is told to go on alone in 90 s: /nudge does it now, /hold never · fake attach 3", log.Lines);
        Assert.Contains("#3 waits for an answer", machine.Notified);
    }

    [Fact]
    public async Task AQuestionAfterTheMessageThatNoAnswerWillComeWaitsForTheOwnerAsBefore()
    {
        tracker.Add(3);
        var session = assistant.Session(3).Works().Asks("input needed", Half).Then().Works().Asks("input needed", "CSV or JSON?").Then().Works().Ends(Done);

        var result = await RunAsync();

        Assert.Equal(1, result.Done);
        Assert.Equal([Briefing.NoAnswer(3, "needs-owner"), Briefing.Alone(3, "needs-owner")], session.Told);
        Assert.Contains("‖ #3 waits for the owner: input needed · it is told to go on alone in 90 s: /nudge does it now, /hold never · fake attach 3", log.Lines);
    }

    [Fact]
    public async Task ASessionThatWasToldToGoOnAloneIsOverAtOnceWhenItEndsWithTheLineMarkedAsWaiting()
    {
        tracker.Add(3);
        var session = assistant.Session(3).Works().Asks("input needed", "CSV or JSON?").Then().Works().Asks("input needed", "JSON it is.\nTOOBUSY: done");

        var result = await RunAsync();

        Assert.Equal(new RunResult(RunEnd.Emptied, 1), result);
        Assert.Equal([Briefing.Alone(3, "needs-owner")], session.Told);
        Assert.Contains("✔ #3 Task 3 — done in 99 s", log.Lines);
        Assert.DoesNotContain(log.Lines, line => line.StartsWith("▲ #3 ended its turn with a line for toobusy", StringComparison.Ordinal));
    }

    [Fact]
    public async Task ASessionThatWrapsUpAfterAnAbortAndIsMarkedAsWaitingIsOverAtOnce()
    {
        tracker.Add(3);
        var session = assistant.Session(3).Works(20).WhenAsked().Works().Asks("input needed", "Undone: a.cs.\nTOOBUSY: interrupted");
        clock.At(TimeSpan.FromSeconds(4), () => Run.Send(RunCommand.Abort));

        var result = await RunAsync();

        Assert.Equal(new RunResult(RunEnd.Stopped, 0), result);
        Assert.Equal([Briefing.WrapUp(3)], session.Asked);
        Assert.Empty(session.Told);
        Assert.Contains("■ #3 Task 3 — interrupted after 9 s: the report is in the task, and the next run takes it first", log.Lines);
        Assert.Equal(Record(3, RecordState.WrappedUp), state.Record);
    }

    [Fact]
    public async Task ASessionThatWaitedWhenTheRunWasAbortedIsOverWhenItsWrapUpIsMarkedAsWaiting()
    {
        tracker.Add(3);
        var session = assistant.Session(3).Works().Asks().Then().Asks("input needed", "Undone.\nTOOBUSY: interrupted");
        clock.At(TimeSpan.FromSeconds(10), () => Run.Send(RunCommand.Abort));

        var result = await RunAsync();

        Assert.Equal(RunEnd.Stopped, result.End);
        Assert.Equal([Briefing.WrapUp(3)], session.Told);
        Assert.Contains("label 3 +interrupted", tracker.Did);
    }

    [Fact]
    public async Task ASessionThatReportsWhileTheRunAbortsIsSettledAndToldNothing()
    {
        tracker.Add(3);
        var session = assistant.Session(3).Works(3).Asks("input needed", Done);
        clock.At(TimeSpan.FromSeconds(4), () => Run.Send(RunCommand.Abort));

        var result = await RunAsync();

        Assert.Equal(new RunResult(RunEnd.Stopped, 1), result);
        Assert.Empty(session.Told);
        Assert.Equal(["status 3 InProgress", "assign 3", "comment 3", "close 3", "status 3 Done"], tracker.Did);
    }

    [Fact]
    public async Task ASessionThatHasReportedAndDoesNotGoOnWithTheMessageIsSettledByItsReport()
    {
        tracker.Add(3);
        var session = assistant.Session(3).Works().Asks("input needed", Half);
        session.Deaf = "it is deaf";

        var result = await RunAsync();

        Assert.Equal(1, result.Partial);
        Assert.Single(session.Told);
        Assert.Contains("✖ #3 did not go on with the message: it is deaf", log.Lines);
        Assert.Contains("close 3", tracker.Did);
    }

    [Fact]
    public async Task AUsageLimitComesBeforeAReplyThatEndsWithTheLine()
    {
        tracker.Add(3);
        var session = assistant.Session(3).Works().Limit(phase: SessionPhase.Asking, reply: Half).Then().Works().Ends(Done);

        var result = await RunAsync();

        Assert.Equal(new RunResult(RunEnd.Emptied, 1), result);
        Assert.Equal([Briefing.AfterLimit(3)], session.Told);
        Assert.Contains("‖ #3 ran into a usage limit: You have hit your usage limit.", log.Lines);
        Assert.DoesNotContain(log.Lines, line => line.Contains("it is told that none will come", StringComparison.Ordinal));
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
        session.Deaf = "it is deaf";

        var result = await RunAsync();

        Assert.Single(session.Told);
        Assert.Contains("✖ #3 did not go on with the message: it is deaf", log.Lines);
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
        Assert.Equal(["status 3 InProgress", "assign 3", "label 3 +interrupted", "comment 3", "status 3 Todo"], tracker.Did);
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
        Assert.Equal(["status 3 InProgress", "assign 3"], tracker.Did);
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
        Assert.Equal(["status 5 InProgress", "assign 5", "label 5 -interrupted"], tracker.Did.Take(3));
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
    public async Task AKillWhileASessionStartsSendsTheTaskBackAndStopsTheSessionTheStartLeft()
    {
        plan = plan with { Commands = Around };
        tracker.Add(3);
        tracker.Add(5);
        assistant.Starting = _ => kill.Cancel();
        assistant.StartsAnyway = true;

        var result = await RunAsync();

        const string Left = "#3 was being started: it is back in Todo, and the session that started for it all the same is stopped · fake attach 3";
        Assert.Equal(new RunResult(RunEnd.Killed, 0, Left), result);
        Assert.Contains($"■ Killed while {Left}", log.Lines);
        Assert.Equal(["status 3 InProgress", "status 3 Todo"], tracker.Did);

        // The session is looked for by the name the start gave it, since the start was made, after the command before.
        var (start, since) = Assert.Single(assistant.LookedFor);
        Assert.Equal(("#3 Task 3", FakeClock.Start + TimeSpan.FromSeconds(2)), (start.Name, since));
        Assert.Equal(1, assistant.Session(3).Stopped);

        // No record names a session that does not work, and what the task started is stopped by the command after it.
        Assert.Null(state.Record);
        Assert.Equal([(Free, "3", policy.CommandTime), (Halt, "3", policy.KilledCommandTime)], shell.Ran);
        Assert.Empty(log.Ends);
    }

    [Fact]
    public async Task AKillWhileASessionStartsSaysWhenNoSessionWasFound()
    {
        tracker.Add(3);
        assistant.Starting = _ => kill.Cancel();

        var result = await RunAsync();

        Assert.Equal(new RunResult(RunEnd.Killed, 0, "#3 was being started: it is back in Todo, and no session was found for it"), result);
        Assert.Equal(["status 3 InProgress", "status 3 Todo"], tracker.Did);
        Assert.Single(assistant.LookedFor);
        Assert.Empty(assistant.Started);
    }

    [Fact]
    public async Task AKillWhileATaskIsMovedToInProgressSendsItBackAndLooksForNoSession()
    {
        tracker.Add(3);
        tracker.Doing = what =>
        {
            if (what == "status 3 InProgress")
                kill.Cancel();
        };

        var result = await RunAsync();

        Assert.Equal(new RunResult(RunEnd.Killed, 0, "#3 was being started: it is back in Todo"), result);
        Assert.Equal(["status 3 Todo"], tracker.Did);
        Assert.Empty(assistant.LookedFor);
    }

    [Fact]
    public async Task ATaskThatCannotBeSentBackAfterAKillWhileItsSessionStartsIsSaid()
    {
        tracker.Add(3);
        tracker.Refused.Add("status 3 Todo");
        assistant.Starting = _ => kill.Cancel();

        var result = await RunAsync();

        Assert.Equal(new RunResult(RunEnd.Killed, 0, "#3 was being started: it could not be moved back to Todo, and no session was found for it"), result);
        Assert.Contains("▲ The tracker refused: status 3 Todo. · set it right by hand before the next run", log.Lines);
    }

    [Fact]
    public async Task AKillWhileASessionStartsWithoutABoardLeavesTheTaskAsItWas()
    {
        plan = plan with { Rules = Rules with { Board = false } };
        tracker.Add(3);
        assistant.Starting = _ => kill.Cancel();

        var result = await RunAsync();

        Assert.Equal(new RunResult(RunEnd.Killed, 0, "#3 was being started: it is left as it was, and no session was found for it"), result);
    }

    [Fact]
    public async Task TheNearLimitHoldsNoTaskBack()
    {
        tracker.Add(3);
        tracker.Add(5);
        assistant.Limits = new UsageLimits(new UsageWindow("5-hour", 99.5, FakeClock.Start + TimeSpan.FromHours(2)), new UsageWindow("weekly", 40, null));

        var result = await RunAsync();

        Assert.Equal(new RunResult(RunEnd.Emptied, 2), result);
        Assert.Equal([3, 5], assistant.Started.Select(started => started.Task));
        Assert.True(clock.Now - FakeClock.Start < TimeSpan.FromMinutes(1));
        Assert.DoesNotContain(log.Statuses, status => status.Phase == RunPhase.WaitingForLimit);
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
    public async Task AWindowWhoseResetHasPassedIsNotKnown()
    {
        tracker.Add(3);
        assistant.Limits = new UsageLimits(new UsageWindow("5-hour", 100, FakeClock.Start - TimeSpan.FromMinutes(1)), new UsageWindow("weekly", 100, FakeClock.Start));

        var result = await RunAsync();

        Assert.Equal(new RunResult(RunEnd.Emptied, 1), result);
        Assert.All(log.Statuses, status => Assert.Equal(UsageLimits.Unknown, status.Limits));
    }

    [Fact]
    public async Task TheLimitsAreReadWithEveryLookAtTheSession()
    {
        tracker.Add(3);
        var before = new UsageLimits(new UsageWindow("5-hour", 2, FakeClock.Start + TimeSpan.FromHours(4)), new UsageWindow("weekly", 3, FakeClock.Start + TimeSpan.FromDays(5)));
        var now = new UsageLimits(new UsageWindow("5-hour", 11, FakeClock.Start + TimeSpan.FromHours(4)), new UsageWindow("weekly", 4, FakeClock.Start + TimeSpan.FromDays(5)));
        assistant.Limits = before;
        assistant.Session(3)
            .Works()
            .Works(and: () => assistant.Limits = now)
            .Works()
            .Ends(Done);

        await RunAsync();

        Assert.Equal(before, log.Statuses.First(status => status is { Phase: RunPhase.Preparing, Text: "Starting #3…" }).Limits);
        Assert.Equal([before, now], log.Statuses.Where(status => status.Phase == RunPhase.Working).Select(status => status.Limits).Distinct());

        // After the task the limits are those read last.
        Assert.Equal(now, log.Statuses[^1].Limits);
    }

    [Fact]
    public async Task AWindowWhoseResetPassesWhileTheSessionWorksIsNotOnTheStatusFromThen()
    {
        tracker.Add(3);
        var far = new UsageWindow("weekly", 4, FakeClock.Start + TimeSpan.FromDays(5));
        assistant.Limits = new UsageLimits(new UsageWindow("5-hour", 11, FakeClock.Start + TimeSpan.FromMinutes(1)), far);
        assistant.Session(3).Works(looks: 30).Ends(Done);

        await RunAsync();

        Assert.Equal(
            [new UsageLimits(new UsageWindow("5-hour", 11, FakeClock.Start + TimeSpan.FromMinutes(1)), far), new UsageLimits(null, far)],
            log.Statuses.Where(status => status.Phase == RunPhase.Working).Select(status => status.Limits).Distinct());
    }

    [Fact]
    public async Task TheLimitsAreReadWhileTheSessionsCannotBe()
    {
        tracker.Add(3);
        var now = new UsageLimits(new UsageWindow("5-hour", 11, null), null);
        assistant.Session(3).Works().Unseen().Unseen(and: () => assistant.Limits = now).Unseen().Works().Ends(Done);

        await RunAsync();

        Assert.Contains(log.Statuses, status => status is { Phase: RunPhase.Working, Step: "its session cannot be read" } && status.Limits == now);
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
        Assert.Equal(["status 3 InProgress", "assign 3", "label 3 +interrupted", "status 3 Todo", "comment 3"], tracker.Did);
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
    public async Task AFarLimitWhoseResetHasPassedPausesNothing()
    {
        tracker.Add(3);
        assistant.Limits = new UsageLimits(null, new UsageWindow("weekly", 100, FakeClock.Start + TimeSpan.FromMinutes(1)));
        var session = assistant.Session(3).Works(looks: 30).Limit().Then().Works().Ends(Done);

        var result = await RunAsync();

        Assert.Equal(new RunResult(RunEnd.Emptied, 1), result);
        Assert.Equal([Briefing.AfterLimit(3)], session.Told);
        Assert.Contains("‖ #3 waits 30 min for the limit to reset, then goes on · /stop pauses it for the next run", log.Lines);
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
    public async Task AKillWhileTheSessionIsStoppedForALimitPausesTheTask()
    {
        tracker.Add(3);
        var session = assistant.Session(3).Works().Limit();
        session.Stopping = kill.Cancel;

        var result = await RunAsync();

        Assert.Equal(RunEnd.Killed, result.End);
        Assert.StartsWith("#3 is paused: toobusy was stopped while it waited for the limit.", result.Why, StringComparison.Ordinal);
        Assert.Equal(["status 3 InProgress", "assign 3", "label 3 +interrupted", "status 3 Todo", "comment 3"], tracker.Did);
        Assert.Equal(Record(3, RecordState.Paused), state.Record);
        Assert.Equal([(3, RunMark.Paused)], Ended);

        // The stop for the wait was cut, and the pause stops the session again.
        Assert.Equal(1, session.Stopped);
        Assert.DoesNotContain(log.Lines, line => line.Contains("to reset, then goes on", StringComparison.Ordinal));
    }

    [Fact]
    public async Task AKillInTheMiddleOfAPauseDoesNotCutItAndEndsTheRun()
    {
        tracker.Add(3);
        tracker.Add(5);
        assistant.Session(3)
            .Works(and: () => assistant.Limits = new UsageLimits(null, new UsageWindow("weekly", 100, FakeClock.Start + TimeSpan.FromDays(1))))
            .Limit();
        tracker.Doing = what =>
        {
            if (what == "label 3 +interrupted")
                kill.Cancel();
        };

        var result = await RunAsync();

        Assert.Equal(RunEnd.Killed, result.End);
        Assert.StartsWith("#3 is paused: the weekly limit is spent", result.Why, StringComparison.Ordinal);
        Assert.Equal(["status 3 InProgress", "assign 3", "label 3 +interrupted", "status 3 Todo", "comment 3"], tracker.Did);
        Assert.Equal(Record(3, RecordState.Paused), state.Record);
        Assert.Single(assistant.Started);
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
        assistant.Session(5).Works().Ends("Half of it; the rest is #9.\nTOOBUSY: partial");
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
        Assert.Equal(["status 5 InProgress", "assign 5", "comment 5", "close 5", "status 5 Done"], tracker.Did.Take(5));
        Assert.Equal(
            [
                "✻ v0.2.0 · 2 tasks in the queue · the assistant's own model · high effort · auto mode",
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
        var session = assistant.Session(5).Ends("Half of it; the rest is #9.\nTOOBUSY: partial");

        var result = await RunAsync();

        Assert.Equal(new RunResult(RunEnd.Emptied, 0) { Partial = 1 }, result);
        Assert.Equal(1, session.Looks);
        Assert.Empty(session.Told);
        Assert.Contains("→ #5 Task 5 — taken again · its session ended while nobody watched · fake attach 5", log.Lines);
        Assert.Contains("◐ #5 Task 5 — done in part in 0 s", log.Lines);
        Assert.Equal(["status 5 InProgress", "assign 5", "comment 5", "close 5", "status 5 Done"], tracker.Did);
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
        Assert.Equal([Briefing.Alone(5, "needs-owner")], session.Told);
        Assert.Contains("→ #5 Task 5 — taken again · its session still works, and is watched again · fake attach 5", log.Lines);
    }

    [Fact]
    public async Task ASessionOfAnEarlierRunMarkedAsWaitingAfterItsReportIsToldOnceAndIsOverAtItsNextReply()
    {
        state.Record = Record(5);
        tracker.Add(5, status: BoardStatus.InProgress);
        var session = assistant.Session(5).Asks("input needed", Half).Then().Asks("input needed", Half);

        var result = await RunAsync();

        Assert.Equal(1, result.Partial);
        Assert.Equal([Briefing.NoAnswer(5, "needs-owner")], session.Told);
        Assert.Contains("→ #5 Task 5 — taken again · its session still works, and is watched again · fake attach 5", log.Lines);
        Assert.Contains("▲ #5 ended its turn with a line for toobusy, but Claude Code marks it as waiting for an answer · it is told that none will come · fake attach 5", log.Lines);
        Assert.Null(state.Record);
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
        Assert.Equal(["status 5 InProgress", "assign 5", "label 5 -interrupted", "comment 5", "close 5", "status 5 Done"], tracker.Did.Take(6));
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
        Assert.Equal(["status 5 InProgress", "assign 5", "label 5 -interrupted"], tracker.Did.Take(3));
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
    public async Task ATaskOfTheRecordThatIsNotAmongTheTasksIsNotTakenAndTheRecordIsForgotten()
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

    [Fact]
    public async Task ATaskThatACommitOfItsSessionClosedIsSettledAllTheSame()
    {
        // The push of a commit with `Closes #3` closes the task while its session still works.
        tracker.Add(3);
        tracker.Add(5);
        assistant.Session(3).Works(and: () => tracker.Shut(3)).Ends(Done);

        var result = await RunAsync();

        Assert.Equal(new RunResult(RunEnd.Emptied, 2), result);
        Assert.Equal(["status 3 InProgress", "assign 3", "comment 3", "close 3", "status 3 Done"], tracker.Did.Take(5));
        Assert.Equal((3, "**Done.**\n\nDid it.\n\n_Session: `fake attach 3`_"), tracker.Comments[0]);
        Assert.Contains("✔ #3 Task 3 — done in 3 s", log.Lines);
        Assert.Equal([(3, RunMark.Done), (5, RunMark.Done)], Ended);

        // A task that is done is not asked for: it is closed, whoever closed it.
        Assert.Empty(tracker.Looked);
    }

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
    [InlineData(RecordState.Paused, false, "it is closed")]
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
        assistant.Session(5).Deaf = "it is deaf";

        var result = await RunAsync();

        Assert.Equal("the session of #5 did not go on: it is deaf · fake attach 5 · the next run tries again", result.Why);
        Assert.Equal(Record(5, RecordState.Paused), state.Record);
        Assert.Empty(tracker.Did);
    }

    // Claude Code may go on with a session as a copy, a new session with the whole conversation: the task is gone
    // on with by the copy, which the record names from then on.
    [Fact]
    public async Task ASessionThatGoesOnAsACopyIsTheSessionOfTheTaskFromThenOn()
    {
        state.Record = Record(5, RecordState.WrappedUp);
        tracker.Add(5, labels: ["feature", "interrupted"]);
        var session = assistant.Session(5);
        session.Copies = true;

        var result = await RunAsync();

        Assert.Equal(new RunResult(RunEnd.Emptied, 1), result);
        Assert.Equal([Briefing.AfterAbort(5)], session.Told);
        Assert.Contains("→ #5 Task 5 — taken again · its session goes on after /abort, as a copy of fake-5 · fake attach 5-copy", log.Lines);
        Assert.Contains(state.Written, record => record is { Session: { Id: "fake-5-copy", Conversation: "copy-5" } });
    }

    [Fact]
    public async Task ASessionThatGoesOnAsACopyInTheMiddleOfItsTaskIsNoted()
    {
        tracker.Add(3);
        var session = assistant.Session(3).Works().Asks().Then().Works().Ends(Done);
        session.Copies = true;

        var result = await RunAsync();

        Assert.Equal(new RunResult(RunEnd.Emptied, 1), result);
        Assert.Contains("  #3 goes on as a copy of fake-3: Claude Code started a new session with the whole conversation · fake attach 3-copy", log.Lines);
        Assert.Contains(state.Written, record => record is { Session.Id: "fake-3-copy" });
    }

    [Fact]
    public async Task ASessionThatGoesOnAsACopyAfterTheResetOfTheLimitIsSaidSo()
    {
        tracker.Add(3);
        var session = assistant.Session(3)
            .Works(and: () => assistant.Limits = new UsageLimits(new UsageWindow("5-hour", 100, FakeClock.Start + TimeSpan.FromHours(1)), null))
            .Limit("You have hit your limit · resets 10am")
            .Then().Works().Ends(Done);
        session.Copies = true;

        var result = await RunAsync();

        Assert.Equal(new RunResult(RunEnd.Emptied, 1), result);
        Assert.Contains("▶ #3 goes on after the reset of the limit, as a copy of fake-3 · fake attach 3-copy", log.Lines);
    }

    [Fact]
    public async Task ASessionThatDoesNotGoOnWithTheRequestToWrapUpSaysWhy()
    {
        tracker.Add(3);
        var session = assistant.Session(3).Works().Asks();
        session.Deaf = "it is deaf";
        clock.At(TimeSpan.FromSeconds(10), () => Run.Send(RunCommand.Abort));

        await RunAsync();

        Assert.Equal([Briefing.WrapUp(3)], session.Told);
        Assert.Contains("✖ #3 did not go on with the request to wrap up: it is deaf", log.Lines);
    }

    [Fact]
    public async Task ATrackerThatRefusesAfterASessionWasStartedStopsItAndTheNextRunGoesOnWithIt()
    {
        tracker.Add(5, labels: ["bug", "interrupted"]);
        tracker.Refused.Add("label 5 -interrupted");
        var session = assistant.Session(5).Lost("I was about to").Then().Works().Ends(Done);

        var result = await RunAsync();

        var why = "The tracker refused: label 5 -interrupted. · the session of #5 is stopped, and the next run goes on with it · fake attach 5";
        Assert.Equal(new RunResult(RunEnd.Problem, 0, why), result);
        Assert.Equal(1, session.Stopped);
        Assert.Equal(0, session.Looks);
        Assert.Contains($"✖ Stopped: {why} · the next task did not start · 0 done", log.Lines);
        Assert.Equal(Record(5), state.Record);

        tracker.Refused.Clear();
        run = null;
        result = await RunAsync();

        Assert.Equal(new RunResult(RunEnd.Emptied, 1), result);
        Assert.Single(assistant.Started);
        Assert.Equal([Briefing.AfterStop(5)], session.Told);
        Assert.Contains("→ #5 Task 5 — taken again · its session was stopped, and goes on · fake attach 5", log.Lines);
        Assert.Equal(["status 5 InProgress", "assign 5", "status 5 InProgress", "assign 5", "label 5 -interrupted", "comment 5", "close 5", "status 5 Done"], tracker.Did);
        Assert.Null(state.Record);
    }

    // Whatever the record said of the session, it was told to go on and stopped after that: its changes are its
    // own, and the next run tells it so.
    [Theory]
    [InlineData(RecordState.Working)]
    [InlineData(RecordState.Paused)]
    [InlineData(RecordState.WrappedUp)]
    public async Task ATrackerThatRefusesAfterASessionWasToldToGoOnStopsItAndTheNextRunGoesOnWithIt(RecordState left)
    {
        state.Record = Record(5, left);
        tracker.Add(5, labels: ["feature", "interrupted"]);
        tracker.Refused.Add("status 5 InProgress");
        var session = assistant.Session(5).Lost("I was about to").Then().Works().Ends(Done);
        var first = left switch
        {
            RecordState.Paused => Briefing.AfterLimit(5),
            RecordState.WrappedUp => Briefing.AfterAbort(5),
            _ => Briefing.AfterStop(5),
        };

        var result = await RunAsync();

        var why = "The tracker refused: status 5 InProgress. · the session of #5 is stopped, and the next run goes on with it · fake attach 5";
        Assert.Equal(new RunResult(RunEnd.Problem, 0, why), result);
        Assert.Equal([first], session.Told);
        Assert.Equal(1, session.Stopped);
        Assert.Empty(tracker.Did);
        Assert.Contains($"✖ Stopped: {why} · the next task did not start · 0 done", log.Lines);
        Assert.Equal(Record(5), state.Record);

        tracker.Refused.Clear();
        workspace.Next.Enqueue(new WorkspaceState(2, 0));
        run = null;
        result = await RunAsync();

        Assert.Equal(new RunResult(RunEnd.Emptied, 1), result);
        Assert.Empty(assistant.Started);
        Assert.Equal([first, Briefing.AfterStop(5)], session.Told);
        Assert.Equal(["status 5 InProgress", "assign 5", "label 5 -interrupted", "comment 5", "close 5", "status 5 Done"], tracker.Did);
        Assert.Null(state.Record);
    }

    [Fact]
    public async Task AKillWhileATaskIsTakenStopsItsSessionToo()
    {
        tracker.Add(5, labels: ["bug", "interrupted"]);
        tracker.Doing = what =>
        {
            if (what == "label 5 -interrupted")
            {
                kill.Cancel();
                kill.Token.ThrowIfCancellationRequested();
            }
        };

        var result = await RunAsync();

        Assert.Equal(new RunResult(RunEnd.Killed, 0, "#5 was being taken, and the next run goes on with its session · fake attach 5"), result);
        Assert.Equal(1, assistant.Session(5).Stopped);
        Assert.Contains("■ Killed the session of #5 while the task was taken: the next run goes on with its session · fake attach 5", log.Lines);
        Assert.Equal(Record(5), state.Record);
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
        Assert.Equal(["status 3 InProgress", "assign 3", "comment 3", "close 3", "status 3 Done"], tracker.Did);
        Assert.Null(state.Record);
        Assert.Equal([(3, RunMark.Done)], Ended);
        Assert.Equal(["  #3 is closed, with the report of the session as its comment", $"■ Killed while {Left}"], log.Lines.SkipWhile(line => !line.StartsWith("  #3 is closed", StringComparison.Ordinal)).Take(2));

        // The next task is not started, and the command after this one is not cut.
        Assert.Single(assistant.Started);
        Assert.Equal([(Free, "3", policy.CommandTime), (Halt, "3", policy.KilledCommandTime)], shell.Ran);
    }

    [Fact]
    public async Task AKillWhileATaskIsLeftToTheOwnerDoesNotCutItAndTheRunStopsOverTheTask()
    {
        tracker.Add(3);
        assistant.Session(3).Works().Ends("The tests do not build.\nTOOBUSY: failed the tests do not build");
        tracker.Doing = what =>
        {
            if (what == "label 3 +needs-owner")
                kill.Cancel();
        };

        var result = await RunAsync();

        Assert.Equal(new RunResult(RunEnd.Problem, 0, "#3 failed · fake attach 3"), result);
        Assert.Equal(["status 3 InProgress", "assign 3", "label 3 +needs-owner", "comment 3", "status 3 Todo"], tracker.Did);
        Assert.Contains("■ Killed while #3 was being settled, and the tracker was told how it went all the same", log.Lines);
    }

    [Fact]
    public async Task AKillInAPauseWhileTheOutcomeIsToldLeavesTheFailureOfTheCall()
    {
        tracker.Add(3);
        tracker.Unanswered["close"] = 1;
        tracker.Doing = what =>
        {
            if (what == "close 3")
                clock.At(clock.Now - FakeClock.Start + TimeSpan.FromSeconds(5), kill.Cancel);
        };

        var result = await RunAsync();

        // The close is not made again, and the task is left to the owner as over any tracker that does not answer.
        Assert.Equal(new RunResult(RunEnd.Problem, 0, "The tracker did not answer: close 3 (1)."), result);
        Assert.Single(tracker.Asked, asked => asked == "close 3");
        Assert.Equal(["status 3 InProgress", "assign 3", "comment 3", "label 3 +needs-owner", "comment 3", "status 3 Todo"], tracker.Did);
        Assert.Contains("■ Killed while #3 was being settled, and the tracker was told how it went all the same", log.Lines);
    }

    [Fact]
    public async Task AKillBeforeTheRecordOfASettledTaskIsForgottenLeavesTheTaskToTheNextRun()
    {
        tracker.Add(3);
        var session = assistant.Session(3);
        session.Stopping = kill.Cancel;

        var result = await RunAsync();

        Assert.Equal(new RunResult(RunEnd.Killed, 0, "#3 was being settled, and the next run settles it · fake attach 3"), result);
        Assert.Contains("■ Killed while #3 was being settled: nothing of how it went is told yet, and the next run settles it · fake attach 3", log.Lines);
        Assert.Equal(["status 3 InProgress", "assign 3"], tracker.Did);
        Assert.Equal(Record(3), state.Record);
        Assert.Equal(1, session.Stopped);
        Assert.Equal([(3, RunMark.Interrupted)], Ended);

        // The working copy is read before the task, and not after its session.
        Assert.Equal(1, workspace.Read);
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
        assistant.Session(5).Works().Ends("Half of it; the rest is #9.\nTOOBUSY: partial");
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

    [Fact]
    public async Task ARunOfOneTaskDoesItAndNoOtherAndEndsAsOneThatItsOwnerStopped()
    {
        plan = plan with { Only = 5 };
        tracker.Add(3);
        tracker.Add(5);
        tracker.Add(7);

        var result = await RunAsync();

        Assert.Equal(new RunResult(RunEnd.Stopped, 1, "Stopped after #5, as you asked"), result);
        Assert.Equal([5], assistant.Started.Select(start => start.Task));
        Assert.Equal(
            """
            ✻ v0.2.0 · #5 and no other task · the assistant's own model · high effort · auto mode
            → #5 Task 5 — started · a new session · fake attach 5
              #5 is In Progress on the board and assigned to the current user; the session is told that toobusy keeps its task
            ✔ #5 Task 5 — done in 3 s
              #5 is closed, with the report of the session as its comment
            ‖ Stopped after #5, as you asked · 1 done
              Worked 3 s
            """.ReplaceLineEndings("\n"),
            log.Text);
        Assert.Equal(["Stopped after #5, as you asked: 1 done"], machine.Notified);

        // The queue is not read again: nothing is taken after the task.
        Assert.Single(tracker.Read);
        Assert.Equal(0, log.Statuses.First(status => status.Phase == RunPhase.Working).Queued);
    }

    [Theory]
    [InlineData("Done.\nTOOBUSY: done", "1 done")]
    [InlineData("Half of it; the rest is #9.\nTOOBUSY: partial", "0 done · 1 done in part")]
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

    [Fact]
    public async Task ARunOfOneTaskDoesNotTakeATaskThatItsSessionMakes()
    {
        plan = plan with { Only = 3 };
        tracker.Add(3);
        assistant.Session(3).Works(and: () => tracker.Add(4)).Ends(Done);

        var result = await RunAsync();

        Assert.Equal(RunEnd.Stopped, result.End);
        Assert.Equal([3], assistant.Started.Select(start => start.Task));
    }

    [Theory]
    [InlineData(3, "it has the manual label")]
    [InlineData(4, "it waits for the owner: it has the needs-owner label")]
    [InlineData(5, "it has none of the labels to take: feature, bug")]
    [InlineData(6, "its status is InProgress")]
    [InlineData(7, "it waits for #8")]
    [InlineData(9, "it waits for #3 (it has the manual label)")]
    [InlineData(40, "it is not among the open tasks of the milestone v0.2.0")]
    public async Task ARunOfOneTaskStartsNothingWhenARunWouldNotTakeTheTaskAndSaysWhyInTheWordsOfTheQueue(int number, string why)
    {
        plan = plan with { Only = number };
        tracker.Add(3, labels: ["feature", "manual"]);
        tracker.Add(4, labels: ["feature", "needs-owner"]);
        tracker.Add(5, labels: ["docs"]);
        tracker.Add(6, status: BoardStatus.InProgress);
        tracker.Add(7, blockedBy: [8]);
        tracker.Add(8);
        tracker.Add(9, blockedBy: [3]);

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
    public async Task ARunOfANumberOfTasksIsStoppedByItsOwnerAsAnyOther()
    {
        plan = plan with { Most = 2 };
        tracker.Add(3);
        tracker.Add(5);
        assistant.Session(3).Works(4).Ends(Done);
        clock.At(TimeSpan.FromSeconds(4), () => Run.Send(RunCommand.Stop));

        var result = await RunAsync();

        Assert.Equal(new RunResult(RunEnd.Stopped, 1), result);
        Assert.Contains("‖ Stopped by /stop · 1 done", log.Lines);
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
    public async Task ARunDoesNotStartWhileAnotherGoesOnInTheWorkingCopy()
    {
        tracker.Add(3);
        sign.Other = new RunSign(RunPhase.Working) { Task = 12, Title = "Export the orders as a file", Open = "claude attach 1a2b3c4d" };

        var result = await RunAsync();

        var why = "a run is going on in this working copy already: #12 Export the orders as a file · claude attach 1a2b3c4d";
        Assert.Equal(new RunResult(RunEnd.Problem, 0, why), result);
        Assert.Equal([$"✖ Did not start: {why}"], log.Lines);
        Assert.Empty(assistant.Started);
        Assert.Empty(tracker.Did);

        // The sign of the other run is not this one's to take down.
        Assert.Empty(sign.Written);
        Assert.Equal(RunPhase.Ended, log.Statuses[^1].Phase);
    }

    [Fact]
    public async Task AKillTakesTheSignDownAndARunThatWaitsSaysSoOnIt()
    {
        tracker.Add(3);
        assistant.Session(3)
            .Works(and: () => assistant.Limits = new UsageLimits(new UsageWindow("5-hour", 100, FakeClock.Start + TimeSpan.FromHours(1)), null))
            .Limit();
        clock.At(TimeSpan.FromMinutes(5), kill.Cancel);

        var result = await RunAsync();

        Assert.Equal(RunEnd.Killed, result.End);
        Assert.Contains(
            new RunSign(RunPhase.WaitingForLimit) { Task = 3, Title = "Task 3", Url = "https://example.com/3", Open = "fake attach 3", Until = FakeClock.Start + TimeSpan.FromMinutes(61) },
            sign.Written);
        Assert.Null(sign.Written[^1]);
    }

    [Fact]
    public async Task ASessionThatWaitsForTheOwnerIsSaidOnTheSign()
    {
        tracker.Add(3);
        assistant.Session(3).Works().Asks().Then().Works().Ends(Done);

        await RunAsync();

        Assert.Contains(sign.Written, written => written is { Phase: RunPhase.WaitingForOwner, Task: 3, Open: "fake attach 3", Until: null });
    }

    [Theory]
    [InlineData(RunPhase.Preparing, null, null, false, "between tasks")]
    [InlineData(RunPhase.WaitingForLimit, null, null, false, "it waits for a usage limit")]
    [InlineData(RunPhase.Preparing, 12, null, false, "#12 Export the orders as a file")]
    [InlineData(RunPhase.Working, 12, "claude attach 1a2b3c4d", false, "#12 Export the orders as a file · claude attach 1a2b3c4d")]
    [InlineData(RunPhase.WaitingForOwner, 12, "claude attach 1a2b3c4d", false, "#12 Export the orders as a file · its session waits for the owner · claude attach 1a2b3c4d")]
    [InlineData(RunPhase.WaitingForLimit, 12, "claude attach 1a2b3c4d", false, "#12 Export the orders as a file · waits for a usage limit · claude attach 1a2b3c4d")]
    [InlineData(RunPhase.WaitingForLimit, 12, "claude attach 1a2b3c4d", true, "#12 Export the orders as a file · waits for a usage limit until 2030-01-01 10:01 UTC · claude attach 1a2b3c4d")]
    public void TheSignOfARunSaysInAFewWordsWhatTheRunDoes(RunPhase phase, int? task, string? open, bool until, string told)
    {
        var said = new RunSign(phase)
        {
            Task = task,
            Title = task is null ? null : "Export the orders as a file",
            Open = open,
            Until = until ? FakeClock.Start + TimeSpan.FromMinutes(61) : null,
        };

        Assert.Equal(told, said.Told);
    }

    // The tasks that were told to be over, each with its mark.
    IEnumerable<(int Task, RunMark Mark)> Ended => log.Ends.Select(ended => (ended.Task, ended.Mark));

    // The record of a task as a run writes it for the session the fake assistant gives the task.
    static TaskRecord Record(int task, RecordState left = RecordState.Working) =>
        new(task, new SessionTrace($"fake-{task}", $"conversation-{task}", FakeClock.Start), left);

    Supervisor Run => run ??= new Supervisor(tracker, assistant, workspace, state, sign, machine, clock, shell, plan, policy);

    Task<RunResult> RunAsync() => Run.RunAsync(log, kill.Token);
}
