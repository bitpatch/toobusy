using TooBusy.Core.Processes;
using TooBusy.Core.Run;
using TooBusy.Infrastructure.Git;

namespace TooBusy.Infrastructure.Tests;

public class GitWorkspaceTests
{
    [Fact]
    public async Task ACleanTreeThatIsPushedHasNothingToTellOfButItsBranch()
    {
        var processes = new OneAnswer(Exited("# branch.oid 1a2b3c\n# branch.head develop\n# branch.upstream origin/develop\n# branch.ab +0 -0\n"));

        var state = await new GitWorkspace(processes, "/work/rocket").ReadAsync(TestContext.Current.CancellationToken);

        Assert.Equal(new WorkspaceState(0, 0) { Branch = "develop" }, state);
        Assert.Equal("git", processes.Command);
        Assert.Equal(["-C", "/work/rocket", "status", "--porcelain=v2", "--branch"], processes.Arguments);
    }

    [Fact]
    public async Task ChangedAndUnknownFilesAreChangesAndCommitsAheadAreNotPushed()
    {
        var processes = new OneAnswer(Exited(
            "# branch.oid 1a2b3c\r\n# branch.head develop\r\n# branch.upstream origin/develop\r\n# branch.ab +3 -1\r\n"
            + "1 .M N... 100644 100644 100644 1a2b 3c4d src/a.cs\r\n? notes.txt\r\n"));

        Assert.Equal(new WorkspaceState(2, 3) { Branch = "develop" }, await new GitWorkspace(processes, "/work/rocket").ReadAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task ABranchWithoutAnUpstreamHasNothingThatIsNotPushed()
    {
        var processes = new OneAnswer(Exited("# branch.oid 1a2b3c\n# branch.head scratch\n"));

        Assert.Equal(new WorkspaceState(0, 0) { Branch = "scratch" }, await new GitWorkspace(processes, "/work/rocket").ReadAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task ADetachedHeadIsNoBranch()
    {
        var processes = new OneAnswer(Exited("# branch.oid 1a2b3c\n# branch.head (detached)\n"));

        Assert.Equal(new WorkspaceState(0, 0), await new GitWorkspace(processes, "/work/rocket").ReadAsync(TestContext.Current.CancellationToken));
    }

    [Theory]
    [InlineData(ProcessStatus.Exited, 128)]
    [InlineData(ProcessStatus.NotFound, 0)]
    [InlineData(ProcessStatus.TimedOut, 0)]
    public async Task ATreeThatGitCannotTellOfIsNotRead(ProcessStatus status, int exitCode)
    {
        var processes = new OneAnswer(new ProcessResult(status, exitCode, "", "fatal: not a git repository"));

        Assert.Null(await new GitWorkspace(processes, "/work/rocket").ReadAsync(TestContext.Current.CancellationToken));
    }

    const string Behind = "# branch.oid 1a2b3c\n# branch.head develop\n# branch.upstream origin/develop\n# branch.ab +0 -2\n";

    [Fact]
    public async Task ASyncFetchesTheRemoteOfTheBranchAndMovesItForwardOnlyAsAFastForward()
    {
        var processes = new Answers(Exited(Behind.Replace("-2", "-0", StringComparison.Ordinal)), Exited(""), Exited(Behind), Exited(""));

        var sync = await new GitWorkspace(processes, "/work/rocket").SyncAsync(TestContext.Current.CancellationToken);

        Assert.Equal(new BranchSync(SyncStatus.Moved, "origin/develop", 0, 2), sync);
        Assert.Equal(
            [
                "git -C /work/rocket status --porcelain=v2 --branch",
                "git -C /work/rocket fetch --quiet",
                "git -C /work/rocket status --porcelain=v2 --branch",
                "git -C /work/rocket merge --ff-only --quiet @{upstream}",
            ],
            processes.Asked);
        Assert.Equal(TimeSpan.FromMinutes(2), processes.Timeouts[1]);
    }

    [Fact]
    public async Task ABranchWithoutAnUpstreamIsNotFetched()
    {
        var processes = new Answers(Exited("# branch.oid 1a2b3c\n# branch.head scratch\n"));

        Assert.Equal(new BranchSync(SyncStatus.NoUpstream), await new GitWorkspace(processes, "/work/rocket").SyncAsync(TestContext.Current.CancellationToken));
        Assert.Single(processes.Asked);
    }

    [Theory]
    [InlineData(ProcessStatus.Exited, 128, "ssh: Could not resolve hostname github.com: nodename nor servname provided\nfatal: Could not read from remote repository.\n", "ssh: Could not resolve hostname github.com: nodename nor servname provided")]
    [InlineData(ProcessStatus.Exited, 1, "", "git exited with 1")]
    [InlineData(ProcessStatus.TimedOut, 0, "", "git did not end in 2 min")]
    [InlineData(ProcessStatus.NotFound, 0, "", "git cannot be run")]
    public async Task AFetchThatFailsLeavesTheBranchAsItIsAndSaysWhatGitSaid(ProcessStatus status, int exitCode, string error, string said)
    {
        var processes = new Answers(Exited(Behind), new ProcessResult(status, exitCode, "", error));

        var sync = await new GitWorkspace(processes, "/work/rocket").SyncAsync(TestContext.Current.CancellationToken);

        Assert.Equal(new BranchSync(SyncStatus.Unreachable, "origin/develop", Said: said), sync);
        Assert.Equal(2, processes.Asked.Count);
    }

    [Fact]
    public async Task AnUpstreamThatTheRemoteDoesNotHaveAnyMoreIsSaid()
    {
        var processes = new Answers(Exited(Behind), Exited(""), Exited("# branch.oid 1a2b3c\n# branch.head develop\n# branch.upstream origin/develop\n"));

        var sync = await new GitWorkspace(processes, "/work/rocket").SyncAsync(TestContext.Current.CancellationToken);

        Assert.Equal(new BranchSync(SyncStatus.Failed, "origin/develop", Said: "origin/develop is not on the remote any more"), sync);
    }

    [Fact]
    public async Task AMoveThatGitRefusesIsSaid()
    {
        var processes = new Answers(Exited(Behind), Exited(""), Exited(Behind), new ProcessResult(ProcessStatus.Exited, 128, "", "error: Your local changes to the following files would be overwritten by merge:\n\tsrc/a.cs\n"));

        var sync = await new GitWorkspace(processes, "/work/rocket").SyncAsync(TestContext.Current.CancellationToken);

        Assert.Equal(new BranchSync(SyncStatus.Failed, "origin/develop", 0, 2, "error: Your local changes to the following files would be overwritten by merge:"), sync);
    }

    static ProcessResult Exited(string output) => new(ProcessStatus.Exited, 0, output, "");

    // Gives the results it is told to, one after another, and remembers what it was asked to run.
    sealed class Answers(params ProcessResult[] results) : IProcessRunner
    {
        public List<string> Asked { get; } = [];

        public List<TimeSpan> Timeouts { get; } = [];

        public Task<ProcessResult> RunAsync(string command, IReadOnlyList<string> arguments, TimeSpan timeout, CancellationToken cancellationToken)
        {
            Asked.Add(string.Join(' ', [command, .. arguments]));
            Timeouts.Add(timeout);
            return Task.FromResult(results[Asked.Count - 1]);
        }

        public IProcessRunner Inside(string folder) => this;
    }

    sealed class OneAnswer(ProcessResult result) : IProcessRunner
    {
        public string? Command { get; private set; }

        public IReadOnlyList<string> Arguments { get; private set; } = [];

        public Task<ProcessResult> RunAsync(string command, IReadOnlyList<string> arguments, TimeSpan timeout, CancellationToken cancellationToken)
        {
            (Command, Arguments) = (command, arguments);
            return Task.FromResult(result);
        }

        public IProcessRunner Inside(string folder) => this;
    }
}
