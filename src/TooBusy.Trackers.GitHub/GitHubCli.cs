using TooBusy.Core.Processes;

namespace TooBusy.Trackers.GitHub;

public enum GitHubCliState
{
    Ready,

    // `gh` is not on the path.
    Missing,

    // `gh` is there, and it is not logged in to github.com.
    LoggedOut,
}

// The GitHub command-line tool of the machine: whether it can be asked at all.
public sealed class GitHubCli(IProcessRunner processes)
{
    static readonly TimeSpan Patience = TimeSpan.FromSeconds(5);

    public async Task<GitHubCliState> CheckAsync(CancellationToken cancellationToken)
    {
        var result = await processes.RunAsync("gh", ["auth", "status", "--hostname", "github.com"], Patience, cancellationToken);
        return result switch
        {
            { Status: ProcessStatus.NotFound } => GitHubCliState.Missing,
            { Status: ProcessStatus.Exited, ExitCode: 0 } => GitHubCliState.Ready,
            _ => GitHubCliState.LoggedOut,
        };
    }
}
