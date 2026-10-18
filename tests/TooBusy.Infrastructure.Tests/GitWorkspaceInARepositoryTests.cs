using System.Diagnostics;
using TooBusy.Core.Run;
using TooBusy.Infrastructure.Git;
using TooBusy.Infrastructure.Processes;

namespace TooBusy.Infrastructure.Tests;

// The working copy through the real `git`: a remote that is a bare repository of the test, the working copy of a
// run cloned from it, and another clone that pushes to it meanwhile.
public sealed class GitWorkspaceInARepositoryTests : IDisposable
{
    readonly DirectoryInfo folder = Directory.CreateTempSubdirectory("toobusy-git-");
    readonly string remote;
    readonly string work;
    readonly string other;
    readonly GitWorkspace workspace;

    public GitWorkspaceInARepositoryTests()
    {
        remote = Path.Combine(folder.FullName, "remote.git");
        work = Path.Combine(folder.FullName, "work");
        other = Path.Combine(folder.FullName, "other");

        Git(folder.FullName, "init", "--quiet", "--bare", "--initial-branch=develop", remote);
        Git(folder.FullName, "init", "--quiet", "--initial-branch=develop", other);
        Configure(other);
        Commit(other, "first");
        Git(other, "remote", "add", "origin", remote);
        Git(other, "push", "--quiet", "--set-upstream", "origin", "develop");
        Git(folder.FullName, "clone", "--quiet", remote, work);
        Configure(work);
        workspace = new GitWorkspace(new ProcessRunner(), work);
    }

    public void Dispose() => folder.Delete(recursive: true);

    [Fact]
    public async Task ABranchThatHasWhatItsUpstreamHasStaysAsItIs()
    {
        var sync = await workspace.SyncAsync(TestContext.Current.CancellationToken);

        Assert.Equal(new BranchSync(SyncStatus.Level, "origin/develop"), sync);
        Assert.Equal(new WorkspaceState(0, 0) { Branch = "develop" }, await workspace.ReadAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task ABranchThatLacksWhatWasPushedIsMovedForwardToIt()
    {
        Commit(other, "second");
        Commit(other, "third");
        Git(other, "push", "--quiet");

        var sync = await workspace.SyncAsync(TestContext.Current.CancellationToken);

        Assert.Equal(new BranchSync(SyncStatus.Moved, "origin/develop", 0, 2), sync);
        Assert.Equal(Git(other, "rev-parse", "HEAD"), Git(work, "rev-parse", "HEAD"));
        Assert.Equal(new WorkspaceState(0, 0) { Branch = "develop" }, await workspace.ReadAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task ABranchWithCommitsOfItsOwnIsNotMoved()
    {
        Commit(work, "mine");
        var head = Git(work, "rev-parse", "HEAD");

        var sync = await workspace.SyncAsync(TestContext.Current.CancellationToken);

        Assert.Equal(new BranchSync(SyncStatus.Ahead, "origin/develop", 1, 0), sync);
        Assert.Equal(head, Git(work, "rev-parse", "HEAD"));
    }

    [Fact]
    public async Task ABranchThatPartedFromItsUpstreamIsNotMoved()
    {
        Commit(work, "mine");
        var head = Git(work, "rev-parse", "HEAD");
        Commit(other, "theirs");
        Commit(other, "theirs again");
        Git(other, "push", "--quiet");

        var sync = await workspace.SyncAsync(TestContext.Current.CancellationToken);

        Assert.Equal(new BranchSync(SyncStatus.Parted, "origin/develop", 1, 2), sync);
        Assert.Equal(head, Git(work, "rev-parse", "HEAD"));
    }

    [Fact]
    public async Task ABranchWithoutAnUpstreamHasNothingToBeBroughtUpToDateWith()
    {
        Git(work, "switch", "--quiet", "--create", "scratch");

        Assert.Equal(new BranchSync(SyncStatus.NoUpstream), await workspace.SyncAsync(TestContext.Current.CancellationToken));
        Assert.Equal(new WorkspaceState(0, 0) { Branch = "scratch" }, await workspace.ReadAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task ARemoteThatIsNotThereLeavesTheBranchAsItIsAndSaysWhatGitSaid()
    {
        Directory.Delete(remote, recursive: true);

        var sync = await workspace.SyncAsync(TestContext.Current.CancellationToken);

        Assert.Equal(SyncStatus.Unreachable, sync.Status);
        Assert.Equal("origin/develop", sync.Upstream);
        Assert.NotEqual("", sync.Said);
    }

    [Fact]
    public async Task ADetachedHeadIsOnNoBranch()
    {
        Git(work, "switch", "--quiet", "--detach");

        Assert.Null((await workspace.ReadAsync(TestContext.Current.CancellationToken))!.Branch);
    }

    void Configure(string repository)
    {
        Git(repository, "config", "user.name", "toobusy");
        Git(repository, "config", "user.email", "toobusy@example.com");
        Git(repository, "config", "commit.gpgsign", "false");
        Git(repository, "config", "core.hooksPath", Path.Combine(folder.FullName, "no-hooks"));
    }

    static void Commit(string repository, string message) => Git(repository, "commit", "--quiet", "--allow-empty", "--message", message);

    // Runs git and gives what it printed, without the line break at its end; a git that fails fails the test.
    static string Git(string repository, params string[] arguments)
    {
        var start = new ProcessStartInfo("git") { WorkingDirectory = repository, RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
        foreach (var argument in arguments)
            start.ArgumentList.Add(argument);
        using var process = Process.Start(start)!;
        var output = process.StandardOutput.ReadToEndAsync();
        var error = process.StandardError.ReadToEnd();
        process.WaitForExit();
        Assert.True(process.ExitCode == 0, $"git {string.Join(' ', arguments)}: {error}");
        return output.Result.TrimEnd('\n');
    }
}
