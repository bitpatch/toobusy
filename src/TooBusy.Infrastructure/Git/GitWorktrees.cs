using System.Globalization;
using TooBusy.Core.Processes;
using TooBusy.Core.Run;
using TooBusy.Infrastructure.Run;

namespace TooBusy.Infrastructure.Git;

// The git worktrees of a run of several sessions, in `.toobusy/local/worktrees/<task>` of the project at the root, each
// on a branch `toobusy/<task>` that follows the branch of the tasks on the remote: two worktrees cannot have one branch
// checked out, and with that upstream what git counts of a worktree is what its session has not pushed. A worktree of a
// repository that Claude Code trusts is trusted wherever it stands. `copy` puts the ports of a folder together.
public sealed class GitWorktrees(IProcessRunner processes, string root, Func<string, Landing, WorkingCopy> copy) : IWorkingCopies
{
    const string Prefix = "toobusy/";

    static readonly TimeSpan Patience = TimeSpan.FromSeconds(30);

    public async Task<IReadOnlyList<WorkingCopy>> FindAsync(CancellationToken cancellationToken)
    {
        var found = new List<WorkingCopy>();
        foreach (var folder in LocalFolder.WorktreeFolders(root))
        {
            var branch = Prefix + Path.GetFileName(folder);
            if (await FollowedAsync(branch, cancellationToken) is { } onto)
                found.Add(copy(folder, new Landing(branch, onto)));
        }

        return found;
    }

    public async Task<Upstream?> FollowedAsync(string branch, CancellationToken cancellationToken)
    {
        var remote = await GitAsync(["config", "--get", $"branch.{branch}.remote"], cancellationToken);
        var merge = await GitAsync(["config", "--get", $"branch.{branch}.merge"], cancellationToken);
        return remote is { Status: ProcessStatus.Exited, ExitCode: 0 } && merge is { Status: ProcessStatus.Exited, ExitCode: 0 } && remote.Output.Trim() is { Length: > 0 } name && name != "."
            ? new Upstream(name, merge.Output.Trim().Replace("refs/heads/", "", StringComparison.Ordinal))
            : null;
    }

    public async Task<WorkingCopy> MakeAsync(int task, Upstream onto, CancellationToken cancellationToken)
    {
        var name = task.ToString(CultureInfo.InvariantCulture);
        var folder = Path.Combine(LocalFolder.Worktrees(root), name);
        if (Directory.Exists(folder))
            throw new LocalStateException($"the worktree of #{name} is there already, with what an earlier run left: {folder}");

        // A worktree stands in the local folder of the project, which keeps it out of git only where it has its `.gitignore`.
        LocalFolder.Ensure(root);
        var branch = Prefix + name;
        var made = await GitAsync(["worktree", "add", "--quiet", "--track", "-B", branch, folder, onto.Name], cancellationToken);
        if (made is not { Status: ProcessStatus.Exited, ExitCode: 0 })
            throw new LocalStateException($"the worktree of #{name} cannot be made: {Said(made)}");

        return copy(folder, new Landing(branch, onto));
    }

    // Git keeps no worktree whose work is not committed, and this one has none: what is left of it is ignored, the
    // local folder of the worktree among it.
    public async Task RemoveAsync(WorkingCopy copy, CancellationToken cancellationToken)
    {
        var removed = await GitAsync(["worktree", "remove", copy.Folder], cancellationToken);
        if (removed is not { Status: ProcessStatus.Exited, ExitCode: 0 })
            throw new LocalStateException($"the worktree {copy.Folder} cannot be taken away: {Said(removed)}");

        if (copy.Landing is { } landing)
            await GitAsync(["branch", "-D", landing.Branch], cancellationToken);
    }

    Task<ProcessResult> GitAsync(string[] arguments, CancellationToken cancellationToken) =>
        processes.RunAsync("git", ["-C", root, .. arguments], Patience, cancellationToken);

    static string Said(ProcessResult result) =>
        (result.Error + "\n" + result.Output).Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).FirstOrDefault() ?? $"git ended as {result.Status}";
}
