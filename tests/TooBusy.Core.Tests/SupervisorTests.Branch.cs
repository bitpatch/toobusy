using TooBusy.Core.Queue;
using TooBusy.Core.Run;

namespace TooBusy.Core.Tests;

// The branch of the working copy: checked before the queue is read, and brought up to date before every task.
public sealed partial class SupervisorTests
{
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
        Assert.Contains(log.Lines, line => line.StartsWith($"✖ Stopped: {why} · #3 is not taken · ", StringComparison.Ordinal));
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
        Assert.StartsWith($"✖ Stopped: {why} · ", log.Lines[0], StringComparison.Ordinal);
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
}
