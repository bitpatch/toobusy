using TooBusy.Core.Processes;
using TooBusy.Core.Setup;

namespace TooBusy.Infrastructure.Git;

// The repository a working copy is cloned from, as git names its `origin` remote.
public sealed class GitOrigin(IProcessRunner processes)
{
    static readonly TimeSpan Patience = TimeSpan.FromSeconds(3);

    // `owner/name`; null when there is no `origin`, when it is not a GitHub repository, or when git cannot be asked.
    public async Task<string?> ReadAsync(string root, CancellationToken cancellationToken)
    {
        var result = await processes.RunAsync("git", ["-C", root, "remote", "get-url", "origin"], Patience, cancellationToken);
        return result is { Status: ProcessStatus.Exited, ExitCode: 0 } ? RepositoryName.OfRemote(result.Output) : null;
    }
}
