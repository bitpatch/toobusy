namespace TooBusy.Core.Doctor;

// What puts a failed check right on the platform the tool runs on. A command of a package manager is named only
// when that manager is on the path; otherwise the fix is the address of the tool's own instructions.
public static class Fixes
{
    public const string TrackerLogin = "gh auth login";
    public const string ProjectScope = "gh auth refresh -s project";
    public const string AssistantLogin = "claude, then /login";
    public const string Setup = "toobusy init";

    public static string Install(Tool tool, Platform platform, bool manager) => (tool, platform) switch
    {
        (Tool.Git, Platform.MacOS) => "xcode-select --install",
        (Tool.Git, Platform.Windows) => manager ? "winget install Git.Git" : "https://git-scm.com/download/win",
        (Tool.Git, _) => "https://git-scm.com/download/linux",
        (Tool.GitHubCli, Platform.MacOS) when manager => "brew install gh",
        (Tool.GitHubCli, Platform.Windows) when manager => "winget install GitHub.cli",
        (Tool.GitHubCli, _) => "https://github.com/cli/cli#installation",
        (Tool.ClaudeCode, Platform.Windows) => "irm https://claude.ai/install.ps1 | iex",
        _ => "curl -fsSL https://claude.ai/install.sh | bash",
    };

    // Whether the fix of a missing tool depends on a package manager on this platform.
    public static bool AsksManager(Tool tool, Platform platform) => (tool, platform) is (Tool.Git, Platform.Windows) or (Tool.GitHubCli, Platform.MacOS or Platform.Windows);
}
