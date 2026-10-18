using System.Globalization;
using TooBusy.Core.Processes;
using TooBusy.Core.Run;

namespace TooBusy.Infrastructure.Git;

// The working copy of a project, as git tells of it: the files that differ from what is committed, those it does
// not know among them, the branch that is checked out, and the commits of the branch that its upstream does not have.
public sealed class GitWorkspace(IProcessRunner processes, string root) : IWorkspace
{
    static readonly TimeSpan Patience = TimeSpan.FromSeconds(30);

    // How long the remote is given to answer a fetch.
    static readonly TimeSpan FetchPatience = TimeSpan.FromMinutes(2);

    public async Task<WorkspaceState?> ReadAsync(CancellationToken cancellationToken) =>
        await StatusAsync(cancellationToken) is { } status ? new WorkspaceState(status.Files, status.Ahead ?? 0) { Branch = status.Branch } : null;

    // `git fetch` with no remote named fetches the remote of the branch that is checked out, and moving the branch
    // forward is a merge that can only be a fast-forward: it makes no commit, and refuses everything else.
    public async Task<BranchSync> SyncAsync(CancellationToken cancellationToken)
    {
        if (await StatusAsync(cancellationToken) is not { } before)
            return new BranchSync(SyncStatus.Failed, Said: "git cannot tell of the working copy");
        if (before.Upstream is not { } upstream)
            return new BranchSync(SyncStatus.NoUpstream);

        var fetched = await processes.RunAsync("git", ["-C", root, "fetch", "--quiet"], FetchPatience, cancellationToken);
        if (fetched is not { Status: ProcessStatus.Exited, ExitCode: 0 })
            return new BranchSync(SyncStatus.Unreachable, upstream, Said: Said(fetched, FetchPatience));

        // A branch whose upstream the remote does not have any more has no count of commits against it.
        if (await StatusAsync(cancellationToken) is not { } after)
            return new BranchSync(SyncStatus.Failed, upstream, Said: "git cannot tell of the working copy");
        if (after is not { Ahead: { } ahead, Behind: { } behind })
            return new BranchSync(SyncStatus.Failed, upstream, Said: $"{upstream} is not on the remote any more");
        if (ahead > 0)
            return new BranchSync(behind > 0 ? SyncStatus.Parted : SyncStatus.Ahead, upstream, ahead, behind);
        if (behind == 0)
            return new BranchSync(SyncStatus.Level, upstream);

        var moved = await processes.RunAsync("git", ["-C", root, "merge", "--ff-only", "--quiet", "@{upstream}"], Patience, cancellationToken);
        return moved is { Status: ProcessStatus.Exited, ExitCode: 0 }
            ? new BranchSync(SyncStatus.Moved, upstream, 0, behind)
            : new BranchSync(SyncStatus.Failed, upstream, 0, behind, Said(moved, Patience));
    }

    // A line that starts with `#` tells of the branch; every other one is a file. A branch without an upstream has
    // nothing to push to, and so nothing that is not pushed.
    async Task<Status?> StatusAsync(CancellationToken cancellationToken)
    {
        var result = await processes.RunAsync("git", ["-C", root, "status", "--porcelain=v2", "--branch"], Patience, cancellationToken);
        if (result is not { Status: ProcessStatus.Exited, ExitCode: 0 })
            return null;

        var lines = result.Output.Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(line => line.TrimEnd('\r')).ToList();
        var heads = lines.Where(line => line.StartsWith('#')).Select(line => line.Split(' ')).ToList();
        var head = heads.FirstOrDefault(fields => fields is ["#", "branch.head", _])?[2];
        var counts = heads.FirstOrDefault(fields => fields is ["#", "branch.ab", _, _]);
        return new Status(
            lines.Count(line => !line.StartsWith('#')),
            head is null or "(detached)" ? null : head,
            heads.FirstOrDefault(fields => fields is ["#", "branch.upstream", _])?[2],
            counts is null ? null : Count(counts[2]),
            counts is null ? null : Count(counts[3]));
    }

    static int Count(string field) =>
        int.TryParse(field.TrimStart('+', '-'), NumberStyles.None, CultureInfo.InvariantCulture, out var commits) ? commits : 0;

    // What git said of a failure, in its first line.
    static string Said(ProcessResult result, TimeSpan patience) => result.Status switch
    {
        ProcessStatus.NotFound => "git cannot be run",
        ProcessStatus.TimedOut => $"git did not end in {Spoken.Time(patience)}",
        _ => (result.Error + "\n" + result.Output).Split('\n').Select(line => line.Trim()).FirstOrDefault(line => line.Length > 0)
            ?? string.Create(CultureInfo.InvariantCulture, $"git exited with {result.ExitCode}"),
    };

    // Ahead and Behind are null where the branch has no upstream to count against.
    sealed record Status(int Files, string? Branch, string? Upstream, int? Ahead, int? Behind);
}
