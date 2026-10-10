using TooBusy.Core.Processes;
using TooBusy.Core.Setup;
using TooBusy.Trackers.GitHub;

namespace TooBusy.Cli;

// What the setup needs to know of this machine: whether the GitHub command-line tool can be asked, with the fix when
// it cannot, and the repository the project is cloned from.
public sealed class MachineEnvironment(IProcessRunner processes, string? origin) : ISetupEnvironment
{
    const string Instructions = "https://github.com/cli/cli#installation";

    public async Task<SetupEnvironment> InspectAsync(CancellationToken cancellationToken)
    {
        switch (await new GitHubCli(processes).CheckAsync(cancellationToken))
        {
            case GitHubCliState.Missing:
                var fix = OperatingSystem.IsMacOS() && await HasAsync("brew", cancellationToken) ? "brew install gh"
                    : OperatingSystem.IsWindows() && await HasAsync("winget", cancellationToken) ? "winget install GitHub.cli"
                    : Instructions;
                return new SetupEnvironment([new SetupProblem("The GitHub command-line tool `gh` is not installed.", fix)], TrackerReachable: false, origin);
            case GitHubCliState.LoggedOut:
                return new SetupEnvironment([new SetupProblem("`gh` is not logged in to github.com.", "gh auth login")], TrackerReachable: false, origin);
            default:
                return new SetupEnvironment([], TrackerReachable: true, origin);
        }
    }

    // A package manager is proposed only when it is on the path.
    async Task<bool> HasAsync(string command, CancellationToken cancellationToken) =>
        (await processes.RunAsync(command, ["--version"], TimeSpan.FromSeconds(3), cancellationToken)).Status != ProcessStatus.NotFound;
}
