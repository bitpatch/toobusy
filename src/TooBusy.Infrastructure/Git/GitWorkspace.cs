using System.Globalization;
using System.Text.RegularExpressions;
using TooBusy.Core.Processes;
using TooBusy.Core.Run;

namespace TooBusy.Infrastructure.Git;

// The working copy of a project, as git tells of it: the files that differ from what is committed, those it does
// not know among them, the branch that is checked out, and the commits of the branch that its upstream does not have.
// The worktrees of a run share one repository, where two fetches at once get in each other's way, so they share
// `fetching` too: a fetch waits for its turn.
public sealed partial class GitWorkspace(IProcessRunner processes, string root, Turns? fetching = null) : IWorkspace
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

        var fetched = await (fetching ?? new Turns()).TakeAsync(() => processes.RunAsync("git", ["-C", root, "fetch", "--quiet"], FetchPatience, cancellationToken), cancellationToken);
        if (fetched is not { Status: ProcessStatus.Exited, ExitCode: 0 })
            return new BranchSync(SyncStatus.Unreachable, upstream, Said: Said(fetched, FetchPatience)) { MayPass = Passing(fetched) };

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

    // What git said of a failure, in a line: the one that says it could not ask for a login, the last of them, as git
    // says its own after what its credential helper said; or else its first line.
    static string Said(ProcessResult result, TimeSpan patience)
    {
        if (result.Status == ProcessStatus.NotFound)
            return "git cannot be run";
        if (result.Status == ProcessStatus.NotStarted)
            return $"git could not be started: {result.Error}";
        if (result.Status == ProcessStatus.TimedOut)
            return $"git did not end in {Spoken.Time(patience)}";

        var lines = (result.Error + "\n" + result.Output).Split('\n').Select(line => line.Trim()).Where(line => line.Length > 0).ToList();
        return lines.LastOrDefault(Unasked) ?? lines.FirstOrDefault() ?? string.Create(CultureInfo.InvariantCulture, $"git exited with {result.ExitCode}");
    }

    // Whether a fetch failed in a way that may pass: it did not end in its time, git did not reach the remote, or the
    // remote answered with a status of the 500s or a 429. A remote that refuses, as over a login or a repository that
    // is not there, stays, whatever git says after that, and so does a login that git could not ask for. What git
    // quotes is a name or an address it was given, and is not read: a repository called `timeout` is no timeout.
    static bool Passing(ProcessResult result)
    {
        if (result.Status != ProcessStatus.Exited)
            return result.Status == ProcessStatus.TimedOut;

        var said = Quotation().Replace(result.Error + "\n" + result.Output, "");
        if (Unasked(said))
            return false;
        if (HttpStatus().Match(said) is { Success: true } status)
            return int.Parse(status.Groups[1].ValueSpan, CultureInfo.InvariantCulture) is >= 500 or 429;

        return Unreached.Any(words => said.Contains(words, StringComparison.OrdinalIgnoreCase));
    }

    // What git and ssh say when they do not reach the remote: the name of the host is not found, the connection is
    // refused, cut or closed, or it takes too long.
    static readonly string[] Unreached =
    [
        "could not resolve host", "failed to connect", "couldn't connect", "connection refused", "connection reset",
        "connection closed", "network is unreachable", "timed out", "timeout", "hung up unexpectedly", "early EOF",
    ];

    // Whether git, or the Git Credential Manager it asked, says it would have asked for a login but its prompts are
    // off: `could not read Username for '…': terminal prompts disabled`, `Cannot prompt because …`.
    static bool Unasked(string said) =>
        said.Contains("terminal prompts disabled", StringComparison.OrdinalIgnoreCase) || said.Contains("cannot prompt because", StringComparison.OrdinalIgnoreCase);

    // The status the remote answered with, as git says it: `The requested URL returned error: 502`, or `HTTP 502`.
    [GeneratedRegex(@"(?:returned error:|\bHTTP) ([1-5][0-9]{2})\b")]
    private static partial Regex HttpStatus();

    // A text between quotes, as `'https://github.com/acme/rocket/'`; the apostrophe of a word, as of `couldn't`, opens
    // none.
    [GeneratedRegex(@"(?<!\w)'[^'\n]*'|""[^""\n]*""")]
    private static partial Regex Quotation();

    // Ahead and Behind are null where the branch has no upstream to count against.
    sealed record Status(int Files, string? Branch, string? Upstream, int? Ahead, int? Behind);
}
