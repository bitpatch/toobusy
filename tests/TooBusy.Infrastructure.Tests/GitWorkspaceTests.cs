using TooBusy.Core.Processes;
using TooBusy.Core.Run;
using TooBusy.Infrastructure.Git;

namespace TooBusy.Infrastructure.Tests;

public class GitWorkspaceTests
{
    [Fact]
    public async Task ACleanTreeThatIsPushedHasNothingToTellOfButItsBranch()
    {
        var processes = new ScriptedProcesses(Exited("# branch.oid 1a2b3c\n# branch.head develop\n# branch.upstream origin/develop\n# branch.ab +0 -0\n"));

        var state = await new GitWorkspace(processes, "/work/rocket").ReadAsync(TestContext.Current.CancellationToken);

        Assert.Equal(new WorkspaceState(0, 0) { Branch = "develop" }, state);
        var (command, arguments, _, _, _) = Assert.Single(processes.Asked);
        Assert.Equal("git", command);
        Assert.Equal(["-C", "/work/rocket", "status", "--porcelain=v2", "--branch"], arguments);
    }

    [Fact]
    public async Task ChangedAndUnknownFilesAreChangesAndCommitsAheadAreNotPushed()
    {
        var processes = new ScriptedProcesses(Exited(
            "# branch.oid 1a2b3c\r\n# branch.head develop\r\n# branch.upstream origin/develop\r\n# branch.ab +3 -1\r\n"
            + "1 .M N... 100644 100644 100644 1a2b 3c4d src/a.cs\r\n? notes.txt\r\n"));

        Assert.Equal(new WorkspaceState(2, 3) { Branch = "develop" }, await new GitWorkspace(processes, "/work/rocket").ReadAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task ABranchWithoutAnUpstreamHasNothingThatIsNotPushed()
    {
        var processes = new ScriptedProcesses(Exited("# branch.oid 1a2b3c\n# branch.head scratch\n"));

        Assert.Equal(new WorkspaceState(0, 0) { Branch = "scratch" }, await new GitWorkspace(processes, "/work/rocket").ReadAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task ADetachedHeadIsNoBranch()
    {
        var processes = new ScriptedProcesses(Exited("# branch.oid 1a2b3c\n# branch.head (detached)\n"));

        Assert.Equal(new WorkspaceState(0, 0), await new GitWorkspace(processes, "/work/rocket").ReadAsync(TestContext.Current.CancellationToken));
    }

    [Theory]
    [InlineData(ProcessStatus.Exited, 128)]
    [InlineData(ProcessStatus.NotFound, 0)]
    [InlineData(ProcessStatus.TimedOut, 0)]
    public async Task ATreeThatGitCannotTellOfIsNotRead(ProcessStatus status, int exitCode)
    {
        var processes = new ScriptedProcesses(new ProcessResult(status, exitCode, "", "fatal: not a git repository"));

        Assert.Null(await new GitWorkspace(processes, "/work/rocket").ReadAsync(TestContext.Current.CancellationToken));
    }

    const string Behind = "# branch.oid 1a2b3c\n# branch.head develop\n# branch.upstream origin/develop\n# branch.ab +0 -2\n";

    [Fact]
    public async Task ASyncFetchesTheRemoteOfTheBranchAndMovesItForwardOnlyAsAFastForward()
    {
        var processes = new ScriptedProcesses(Exited(Behind.Replace("-2", "-0", StringComparison.Ordinal)), Exited(""), Exited(Behind), Exited(""));

        var sync = await new GitWorkspace(processes, "/work/rocket").SyncAsync(TestContext.Current.CancellationToken);

        Assert.Equal(new BranchSync(SyncStatus.Moved, "origin/develop", 0, 2), sync);
        Assert.Equal(
            [
                "git -C /work/rocket status --porcelain=v2 --branch",
                "git -C /work/rocket fetch --quiet",
                "git -C /work/rocket status --porcelain=v2 --branch",
                "git -C /work/rocket merge --ff-only --quiet @{upstream}",
            ],
            processes.Asked.Select(asked => string.Join(' ', [asked.Command, .. asked.Arguments])));
        Assert.Equal(TimeSpan.FromMinutes(2), processes.Asked[1].Timeout);
    }

    [Fact]
    public async Task ABranchWithoutAnUpstreamIsNotFetched()
    {
        var processes = new ScriptedProcesses(Exited("# branch.oid 1a2b3c\n# branch.head scratch\n"));

        Assert.Equal(new BranchSync(SyncStatus.NoUpstream), await new GitWorkspace(processes, "/work/rocket").SyncAsync(TestContext.Current.CancellationToken));
        Assert.Single(processes.Asked);
    }

    [Theory]
    [InlineData(ProcessStatus.Exited, 128, "ssh: Could not resolve hostname github.com: nodename nor servname provided\nfatal: Could not read from remote repository.\n", "ssh: Could not resolve hostname github.com: nodename nor servname provided", true)]
    [InlineData(ProcessStatus.Exited, 128, "warning: redirecting to https://github.com/acme/rocket.git/\nfatal: Cannot prompt because user interactivity has been disabled.\nfatal: could not read Username for 'https://github.com': terminal prompts disabled\n", "fatal: could not read Username for 'https://github.com': terminal prompts disabled", false)]
    [InlineData(ProcessStatus.Exited, 1, "", "git exited with 1", false)]
    [InlineData(ProcessStatus.TimedOut, 0, "", "git did not end in 2 min", true)]
    [InlineData(ProcessStatus.NotFound, 0, "", "git cannot be run", false)]
    [InlineData(ProcessStatus.NotStarted, 0, "Permission denied", "git could not be started: Permission denied", false)]
    public async Task AFetchThatFailsLeavesTheBranchAsItIsAndSaysWhatGitSaid(ProcessStatus status, int exitCode, string error, string said, bool mayPass)
    {
        var processes = new ScriptedProcesses(Exited(Behind), new ProcessResult(status, exitCode, "", error));

        var sync = await new GitWorkspace(processes, "/work/rocket").SyncAsync(TestContext.Current.CancellationToken);

        Assert.Equal(new BranchSync(SyncStatus.Unreachable, "origin/develop", Said: said) { MayPass = mayPass }, sync);
        Assert.Equal(2, processes.Asked.Count);
    }

    // A remote that did not answer may answer a moment later; one that refused will refuse again.
    [Theory]
    [InlineData("fatal: unable to access 'https://github.com/acme/rocket/': Could not resolve host: github.com", true)]
    [InlineData("fatal: unable to access 'https://github.com/acme/rocket/': Failed to connect to github.com port 443 after 75003 ms: Couldn't connect to server", true)]
    [InlineData("fatal: unable to access 'https://github.com/acme/rocket/': Recv failure: Connection reset by peer", true)]
    [InlineData("ssh: connect to host github.com port 22: Operation timed out\nfatal: Could not read from remote repository.", true)]
    [InlineData("kex_exchange_identification: Connection closed by remote host\nfatal: Could not read from remote repository.", true)]
    [InlineData("fatal: unable to access 'https://github.com/acme/rocket/': The requested URL returned error: 502", true)]
    [InlineData("error: RPC failed; HTTP 429 curl 22 The requested URL returned error: 429\nfatal: the remote end hung up unexpectedly", true)]
    [InlineData("error: RPC failed; curl 18 transfer closed with outstanding read data remaining\nfatal: early EOF", true)]
    [InlineData("git@github.com: Permission denied (publickey).\nfatal: Could not read from remote repository.", false)]
    [InlineData("remote: Repository not found.\nfatal: repository 'https://github.com/acme/rocket/' not found", false)]
    [InlineData("fatal: Authentication failed for 'https://github.com/acme/rocket/'", false)]
    [InlineData("fatal: could not read Username for 'https://github.com': terminal prompts disabled", false)]
    [InlineData("fatal: Cannot prompt because user interactivity has been disabled.\nfatal: could not read Username for 'https://github.com': terminal prompts disabled", false)]
    [InlineData("fatal: Cannot prompt because terminal prompts have been disabled.\nerror: RPC failed; HTTP 503 curl 22 The requested URL returned error: 503", false)]
    [InlineData("error: RPC failed; HTTP 403 curl 22 The requested URL returned error: 403\nfatal: the remote end hung up unexpectedly", false)]
    [InlineData("fatal: 'origin' does not appear to be a git repository", false)]
    public async Task AFetchThatFailedMayPassWhenTheRemoteDidNotAnswer(string error, bool mayPass)
    {
        var processes = new ScriptedProcesses(Exited(Behind), new ProcessResult(ProcessStatus.Exited, 128, "", error));

        var sync = await new GitWorkspace(processes, "/work/rocket").SyncAsync(TestContext.Current.CancellationToken);

        Assert.Equal((SyncStatus.Unreachable, mayPass), (sync.Status, sync.MayPass));
    }

    [Fact]
    public async Task AnUpstreamThatTheRemoteDoesNotHaveAnyMoreIsSaid()
    {
        var processes = new ScriptedProcesses(Exited(Behind), Exited(""), Exited("# branch.oid 1a2b3c\n# branch.head develop\n# branch.upstream origin/develop\n"));

        var sync = await new GitWorkspace(processes, "/work/rocket").SyncAsync(TestContext.Current.CancellationToken);

        Assert.Equal(new BranchSync(SyncStatus.Failed, "origin/develop", Said: "origin/develop is not on the remote any more"), sync);
    }

    [Fact]
    public async Task AMoveThatGitRefusesIsSaid()
    {
        var processes = new ScriptedProcesses(Exited(Behind), Exited(""), Exited(Behind), new ProcessResult(ProcessStatus.Exited, 128, "", "error: Your local changes to the following files would be overwritten by merge:\n\tsrc/a.cs\n"));

        var sync = await new GitWorkspace(processes, "/work/rocket").SyncAsync(TestContext.Current.CancellationToken);

        Assert.Equal(new BranchSync(SyncStatus.Failed, "origin/develop", 0, 2, "error: Your local changes to the following files would be overwritten by merge:"), sync);
    }

    static ProcessResult Exited(string output) => new(ProcessStatus.Exited, 0, output, "");
}
