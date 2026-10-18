using TooBusy.Core.Processes;

namespace TooBusy.Trackers.GitHub;

public enum GitHubCliState
{
    Ready,

    // `gh` is not on the path.
    Missing,

    // `gh` is on the path, and it could not be started.
    Unstarted,

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
            { Status: ProcessStatus.NotStarted } => GitHubCliState.Unstarted,
            { Status: ProcessStatus.Exited, ExitCode: 0 } => GitHubCliState.Ready,
            _ => GitHubCliState.LoggedOut,
        };
    }

    // What is said, after what failed, of a request that GitHub gave no answer to: `gh` is not on the path, it could
    // not be started, or GitHub did not answer in time.
    internal static string Unanswered(ProcessResult result) => result.Status switch
    {
        ProcessStatus.NotFound => "The GitHub command-line tool `gh` is not installed.",
        ProcessStatus.NotStarted => $"The GitHub command-line tool `gh` could not be started: {result.Error}.",
        _ => "GitHub did not answer.",
    };
}
