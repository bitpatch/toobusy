using System.Globalization;
using TooBusy.Core.Processes;
using TooBusy.Core.Run;

namespace TooBusy.Infrastructure.Git;

// The working copy of a project, as git tells of it: the files that differ from what is committed, those it does
// not know among them, and the commits of the branch that its upstream does not have.
public sealed class GitWorkspace(IProcessRunner processes, string root) : IWorkspace
{
    static readonly TimeSpan Patience = TimeSpan.FromSeconds(30);

    public async Task<WorkspaceState?> ReadAsync(CancellationToken cancellationToken)
    {
        var result = await processes.RunAsync("git", ["-C", root, "status", "--porcelain=v2", "--branch"], Patience, cancellationToken);
        if (result is not { Status: ProcessStatus.Exited, ExitCode: 0 })
            return null;

        // A line that starts with `#` tells of the branch; every other one is a file. A branch without an upstream
        // has nothing to push to, and so nothing that is not pushed.
        var lines = result.Output.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        var ahead = lines.Select(line => line.TrimEnd('\r').Split(' ')).FirstOrDefault(fields => fields is ["#", "branch.ab", _, _])?[2];
        return new WorkspaceState(
            lines.Count(line => !line.StartsWith('#')),
            ahead is not null && int.TryParse(ahead.TrimStart('+'), NumberStyles.None, CultureInfo.InvariantCulture, out var commits) ? commits : 0);
    }
}
