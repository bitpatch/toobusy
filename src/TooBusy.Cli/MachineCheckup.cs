using TooBusy.Assistants.ClaudeCode;
using TooBusy.Core.Assistant;
using TooBusy.Core.Doctor;
using TooBusy.Core.Processes;
using TooBusy.Trackers.GitHub;

namespace TooBusy.Cli;

// What the checks of `doctor` ask of this machine: its tools through their own commands, GitHub through `gh` and
// the login of Claude Code through `claude`. `root` is the root of the project and `configuration` Claude Code's
// own folder of the user: a skill is looked for in both.
public sealed class MachineCheckup(IProcessRunner processes, Platform platform, string root, string configuration) : ICheckupMachine
{
    static readonly TimeSpan Patience = TimeSpan.FromSeconds(10);

    readonly GitHubSetup tracker = new(processes);

    public static Platform Here => OperatingSystem.IsMacOS() ? Platform.MacOS : OperatingSystem.IsWindows() ? Platform.Windows : Platform.Linux;

    public Platform Platform => platform;

    // A tool that is there tells its version: the `git` that macOS has before its developer tools are installed
    // is on the path and only says how to install them. A Claude Code newer than the one toobusy was written against
    // is told of.
    public async Task<ToolLook> FindAsync(Tool tool, CancellationToken cancellationToken)
    {
        var command = tool switch
        {
            Tool.Git => "git",
            Tool.GitHubCli => "gh",
            Tool.ClaudeCode => "claude",
            _ => throw new ArgumentOutOfRangeException(nameof(tool), tool, "There is no such tool."),
        };
        var result = await processes.RunAsync(command, ["--version"], Patience, cancellationToken);
        var runs = result is { Status: ProcessStatus.Exited, ExitCode: 0 };
        return new ToolLook(runs, result.Status == ProcessStatus.NotStarted ? result.Error : null)
        {
            WrittenAgainst = runs && tool == Tool.ClaudeCode && ClaudeCodeProtocol.Newer(result.Output) ? ClaudeCodeProtocol.Version : null,
        };
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

    public async Task<TrackerLoginState> CheckTrackerLoginAsync(CancellationToken cancellationToken) =>
        await new GitHubCli(processes).CheckAsync(cancellationToken) switch
        {
            GitHubCliState.Ready => TrackerLoginState.LoggedIn,
            GitHubCliState.TimedOut => TrackerLoginState.NoAnswer,
            _ => TrackerLoginState.LoggedOut,
        };

    public Task<bool> AssistantLoggedInAsync(CancellationToken cancellationToken) => new ClaudeLogin(processes).CheckAsync(cancellationToken);

    public Task<bool> CanReadRepositoryAsync(string repository, CancellationToken cancellationToken) => tracker.CanReadRepositoryAsync(repository, cancellationToken);

    public Task<BoardAccess> CheckBoardAsync(string board, CancellationToken cancellationToken) => tracker.CheckBoardAsync(board, cancellationToken);

    public Task<IReadOnlyList<string>?> ReadStatusesAsync(string board, CancellationToken cancellationToken) => tracker.ReadStatusesAsync(board, cancellationToken);

    public Task<IReadOnlyList<string>?> ReadLabelsAsync(string repository, CancellationToken cancellationToken) => tracker.FindLabelsAsync(repository, cancellationToken);

    public SkillLook FindSkill(string name) => ClaudeSkills.Find(root, configuration, name);
}
