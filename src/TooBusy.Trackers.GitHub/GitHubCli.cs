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

    // `gh` is there, and GitHub did not answer it in time: whether it is logged in is not known.
    TimedOut,
}

// The GitHub command-line tool of the machine: whether it can be asked at all.
public sealed class GitHubCli(IProcessRunner processes)
{
    static readonly TimeSpan Patience = TimeSpan.FromSeconds(5);

    // `gh auth status` asks GitHub whether the token is good, so a slow GitHub is no answer about the login: when it
    // does not end in its time, or says that its own request timed out, nothing is known of the login.
    public async Task<GitHubCliState> CheckAsync(CancellationToken cancellationToken)
    {
        var result = await processes.RunAsync("gh", ["auth", "status", "--hostname", "github.com"], Patience, cancellationToken);
        return result switch
        {
            { Status: ProcessStatus.NotFound } => GitHubCliState.Missing,
            { Status: ProcessStatus.NotStarted } => GitHubCliState.Unstarted,
            { Status: ProcessStatus.TimedOut } => GitHubCliState.TimedOut,
            { Status: ProcessStatus.Exited, ExitCode: 0 } => GitHubCliState.Ready,
            _ when result.Error.Contains("Timeout trying to log in", StringComparison.OrdinalIgnoreCase) => GitHubCliState.TimedOut,
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

    // The field of a query of GitHub that the owner of a board is asked for by: an organisation, or a user.
    internal static string Owner(bool organisation) => organisation ? "organization" : "user";
}
