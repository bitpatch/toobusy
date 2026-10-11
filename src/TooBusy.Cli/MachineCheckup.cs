using TooBusy.Assistants.ClaudeCode;
using TooBusy.Core.Doctor;
using TooBusy.Core.Processes;
using TooBusy.Trackers.GitHub;

namespace TooBusy.Cli;

// What the checks of `doctor` ask of this machine: its tools through their own commands, GitHub through `gh` and
// the login of Claude Code through `claude`.
public sealed class MachineCheckup(IProcessRunner processes, Platform platform) : ICheckupMachine
{
    static readonly TimeSpan Patience = TimeSpan.FromSeconds(10);

    readonly GitHubSetup tracker = new(processes);

    public static Platform Here => OperatingSystem.IsMacOS() ? Platform.MacOS : OperatingSystem.IsWindows() ? Platform.Windows : Platform.Linux;

    public Platform Platform => platform;

    // A tool that is there tells its version: the `git` that macOS has before its developer tools are installed
    // is on the path and only says how to install them.
    public async Task<bool> HasAsync(Tool tool, CancellationToken cancellationToken)
    {
        var command = tool switch
        {
            Tool.Git => "git",
            Tool.GitHubCli => "gh",
            _ => "claude",
        };
        return await processes.RunAsync(command, ["--version"], Patience, cancellationToken) is { Status: ProcessStatus.Exited, ExitCode: 0 };
    }

    public async Task<bool> HasManagerAsync(CancellationToken cancellationToken)
    {
        var manager = platform switch
        {
            Platform.MacOS => "brew",
            Platform.Windows => "winget",
            _ => null,
        };
        return manager is not null && (await processes.RunAsync(manager, ["--version"], Patience, cancellationToken)).Status != ProcessStatus.NotFound;
    }

    public async Task<bool> TrackerLoggedInAsync(CancellationToken cancellationToken) =>
        await new GitHubCli(processes).CheckAsync(cancellationToken) == GitHubCliState.Ready;

    public Task<bool> AssistantLoggedInAsync(CancellationToken cancellationToken) => new ClaudeLogin(processes).CheckAsync(cancellationToken);

    public Task<bool> CanReadRepositoryAsync(string repository, CancellationToken cancellationToken) => tracker.CanReadRepositoryAsync(repository, cancellationToken);

    public Task<BoardAccess> CheckBoardAsync(string board, CancellationToken cancellationToken) => tracker.CheckBoardAsync(board, cancellationToken);

    public Task<IReadOnlyList<string>?> ReadLabelsAsync(string repository, CancellationToken cancellationToken) => tracker.FindLabelsAsync(repository, cancellationToken);
}
