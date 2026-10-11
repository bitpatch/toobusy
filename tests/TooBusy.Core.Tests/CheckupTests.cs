using TooBusy.Core.Doctor;
using TooBusy.Core.Settings;

namespace TooBusy.Core.Tests;

public class CheckupTests
{
    const string Rocket = "https://github.com/orgs/acme/projects/7";

    readonly FakeCheckupMachine machine = new();
    readonly Store store = new() { Current = Project(Rocket) };
    readonly Watched view = new();

    [Fact]
    public async Task OnAMachineThatHasItAllEveryCheckPasses()
    {
        var checks = await RunAsync();

        Assert.Equal(
            [
                new CheckResult("git", CheckState.Passed),
                new CheckResult("GitHub CLI", CheckState.Passed),
                new CheckResult("GitHub login", CheckState.Passed),
                new CheckResult("Claude Code", CheckState.Passed),
                new CheckResult("Claude Code login", CheckState.Passed),
                new CheckResult("settings", CheckState.Passed, ".toobusy/settings.toml"),
                new CheckResult("repository", CheckState.Passed, "acme/rocket"),
                new CheckResult("board", CheckState.Passed, Rocket),
                new CheckResult("labels", CheckState.Passed),
            ],
            checks.Select(check => check with { Details = [] }));
        Assert.All(checks, check => Assert.Empty(check.Details));

        // Every check starts before it ends, and they come one after another.
        Assert.Equal(checks.SelectMany(check => (string[])[$"start {check.Name}", $"done {check.Name}"]), view.Told);
    }

    [Theory]
    [InlineData(Tool.Git, "git", "`git` is not installed.", "xcode-select --install")]
    [InlineData(Tool.GitHubCli, "GitHub CLI", "The GitHub command-line tool `gh` is not installed.", "brew install gh")]
    [InlineData(Tool.ClaudeCode, "Claude Code", "Claude Code is not installed: `claude` is not on the path.", "curl -fsSL https://claude.ai/install.sh | bash")]
    public async Task AToolThatIsMissingFailsItsCheckWithTheFixOfThePlatform(Tool tool, string name, string text, string fix)
    {
        machine.Missing.Add(tool);

        var checks = await RunAsync();

        Assert.Equal(new CheckResult(name, CheckState.Failed, text, fix), Of(checks, name));
    }

    [Fact]
    public async Task WithoutAToolItsLoginIsSkippedAndSoIsWhatNeedsTheLogin()
    {
        machine.Missing.Add(Tool.GitHubCli);
        machine.Missing.Add(Tool.ClaudeCode);

        var checks = await RunAsync();

        Assert.Equal(new CheckResult("GitHub login", CheckState.Skipped, "waits for the GitHub CLI"), Of(checks, "GitHub login"));
        Assert.Equal(new CheckResult("Claude Code login", CheckState.Skipped, "waits for Claude Code"), Of(checks, "Claude Code login"));
        Assert.Equal(new CheckResult("repository", CheckState.Skipped, "waits for the GitHub login"), Of(checks, "repository"));
        Assert.Equal(new CheckResult("board", CheckState.Skipped, "waits for the GitHub login"), Of(checks, "board"));
        Assert.Equal(new CheckResult("labels", CheckState.Skipped, "waits for the repository"), Of(checks, "labels"));
        Assert.Equal(CheckState.Passed, Of(checks, "settings").State);

        // A check that is skipped never starts.
        Assert.DoesNotContain("start GitHub login", view.Told);
        Assert.Contains("done GitHub login", view.Told);
    }

    [Fact]
    public async Task ALoginThatIsMissingFailsWithTheCommandThatLogsIn()
    {
        machine.TrackerLoggedIn = false;
        machine.AssistantLoggedIn = false;

        var checks = await RunAsync();

        Assert.Equal(new CheckResult("GitHub login", CheckState.Failed, "`gh` is not logged in to github.com.", "gh auth login"), Of(checks, "GitHub login"));
        Assert.Equal(new CheckResult("Claude Code login", CheckState.Failed, "Claude Code is not logged in.", "claude, then /login"), Of(checks, "Claude Code login"));
        Assert.Equal(CheckState.Skipped, Of(checks, "repository").State);
    }

    [Fact]
    public async Task WithoutSettingsTheToolsAreCheckedAndTheSettingsFailAsNotSetUp()
    {
        store.Current = null;

        var checks = await RunAsync();

        Assert.All(checks.Take(5), check => Assert.Equal(CheckState.Passed, check.State));
        Assert.Equal(new CheckResult("settings", CheckState.Failed, "This project is not set up yet.", "toobusy init"), Of(checks, "settings"));
        Assert.Equal(CheckState.Passed, Of(checks, "repository").State);
        Assert.Equal(new CheckResult("board", CheckState.Skipped, "waits for the settings"), Of(checks, "board"));
        Assert.Equal(new CheckResult("labels", CheckState.Skipped, "waits for the settings"), Of(checks, "labels"));
    }

    [Fact]
    public async Task SettingsThatDoNotValidateFailWithEveryErrorOfTheFile()
    {
        store.Errors = [new SettingsError("version", 1, "must be 1"), new SettingsError("assistant.type", null, "is missing")];

        var settings = Of(await RunAsync(), "settings");

        Assert.Equal(CheckState.Failed, settings.State);
        Assert.Equal(".toobusy/settings.toml does not validate:", settings.Text);
        Assert.Equal([".toobusy/settings.toml:1: version: must be 1", ".toobusy/settings.toml: assistant.type: is missing"], settings.Details);
        Assert.Null(settings.Fix);
    }

    [Fact]
    public async Task ARepositoryFailsWhenTheOriginIsNotOnGitHubOrCannotBeRead()
    {
        Assert.Equal(
            new CheckResult("repository", CheckState.Failed, "The `origin` remote is not a GitHub repository."),
            Of(await RunAsync(origin: null), "repository"));

        machine.RepositoryReadable = false;
        var checks = await RunAsync();

        Assert.Equal(
            new CheckResult("repository", CheckState.Failed, "acme/rocket cannot be read on GitHub: it does not exist, or you have no access to it."),
            Of(checks, "repository"));
        Assert.Equal(new CheckResult("labels", CheckState.Skipped, "waits for the repository"), Of(checks, "labels"));
    }

    [Fact]
    public async Task WithoutGitTheRepositoryWaitsForIt()
    {
        machine.Missing.Add(Tool.Git);

        Assert.Equal(new CheckResult("repository", CheckState.Skipped, "waits for git"), Of(await RunAsync(origin: null), "repository"));
    }

    [Fact]
    public async Task AProjectWithoutABoardHasNoneToCheck()
    {
        store.Current = Project(board: null);

        Assert.Equal(new CheckResult("board", CheckState.Skipped, "the project has no board"), Of(await RunAsync(), "board"));
        Assert.Empty(machine.Boards);
    }

    [Theory]
    [InlineData(BoardAccess.LacksScope, "Your GitHub login cannot work with projects.", "gh auth refresh -s project")]
    [InlineData(BoardAccess.Unreadable, Rocket + " cannot be read: it does not exist, or you have no access to it.", null)]
    [InlineData(BoardAccess.NoAnswer, "GitHub did not answer, so the board could not be checked.", null)]
    public async Task ABoardThatCannotBeReadFailsForItsReason(BoardAccess access, string text, string? fix)
    {
        machine.Board = access;

        Assert.Equal(new CheckResult("board", CheckState.Failed, text, fix), Of(await RunAsync(), "board"));
        Assert.Equal([Rocket], machine.Boards);
    }

    [Fact]
    public async Task EveryLabelOfTheSettingsMustBeInTheRepositoryWhateverItsCase()
    {
        machine.Labels = ["Manual", "feature", "needs-owner", "INTERRUPTED", "bug"];
        Assert.Equal(new CheckResult("labels", CheckState.Passed), Of(await RunAsync(), "labels"));

        machine.Labels = ["manual", "needs-owner"];
        Assert.Equal(
            new CheckResult(
                "labels",
                CheckState.Failed,
                "acme/rocket has no labels “feature”, “interrupted”.",
                "gh label create \"feature\" --repo acme/rocket && gh label create \"interrupted\" --repo acme/rocket"),
            Of(await RunAsync(), "labels"));

        machine.Labels = ["manual", "needs-owner", "feature"];
        Assert.Equal(
            new CheckResult("labels", CheckState.Failed, "acme/rocket has no label “interrupted”.", "gh label create \"interrupted\" --repo acme/rocket"),
            Of(await RunAsync(), "labels"));

        machine.Labels = null;
        Assert.Equal(new CheckResult("labels", CheckState.Failed, "The labels of acme/rocket cannot be read."), Of(await RunAsync(), "labels"));
    }

    [Fact]
    public async Task TheChecksOfTheMachineAreTheFiveThatNeedNoSettings()
    {
        machine.TrackerLoggedIn = false;

        var checks = await Checkup.MachineAsync(machine, view, TestContext.Current.CancellationToken);

        Assert.Equal(["git", "GitHub CLI", "GitHub login", "Claude Code", "Claude Code login"], checks.Select(check => check.Name));
        Assert.Equal(CheckState.Failed, Of(checks, "GitHub login").State);
    }

    [Theory]
    [InlineData(Platform.MacOS, true, "brew install gh")]
    [InlineData(Platform.MacOS, false, "https://github.com/cli/cli#installation")]
    [InlineData(Platform.Windows, true, "winget install GitHub.cli")]
    [InlineData(Platform.Windows, false, "https://github.com/cli/cli#installation")]
    [InlineData(Platform.Linux, false, "https://github.com/cli/cli#installation")]
    public async Task APackageManagerIsNamedOnlyWhenItIsOnThePath(Platform platform, bool manager, string fix)
    {
        (machine.Platform, machine.HasManager) = (platform, manager);
        machine.Missing.Add(Tool.GitHubCli);

        Assert.Equal(fix, Of(await RunAsync(), "GitHub CLI").Fix);
    }

    [Theory]
    [InlineData(Tool.Git, Platform.MacOS, false, "xcode-select --install")]
    [InlineData(Tool.Git, Platform.MacOS, true, "xcode-select --install")]
    [InlineData(Tool.Git, Platform.Linux, false, "https://git-scm.com/download/linux")]
    [InlineData(Tool.Git, Platform.Windows, true, "winget install Git.Git")]
    [InlineData(Tool.Git, Platform.Windows, false, "https://git-scm.com/download/win")]
    [InlineData(Tool.GitHubCli, Platform.MacOS, true, "brew install gh")]
    [InlineData(Tool.GitHubCli, Platform.MacOS, false, "https://github.com/cli/cli#installation")]
    [InlineData(Tool.GitHubCli, Platform.Linux, false, "https://github.com/cli/cli#installation")]
    [InlineData(Tool.GitHubCli, Platform.Windows, true, "winget install GitHub.cli")]
    [InlineData(Tool.GitHubCli, Platform.Windows, false, "https://github.com/cli/cli#installation")]
    [InlineData(Tool.ClaudeCode, Platform.MacOS, true, "curl -fsSL https://claude.ai/install.sh | bash")]
    [InlineData(Tool.ClaudeCode, Platform.Linux, false, "curl -fsSL https://claude.ai/install.sh | bash")]
    [InlineData(Tool.ClaudeCode, Platform.Windows, true, "irm https://claude.ai/install.ps1 | iex")]
    [InlineData(Tool.ClaudeCode, Platform.Windows, false, "irm https://claude.ai/install.ps1 | iex")]
    public void EveryToolHasItsFixOnEveryPlatform(Tool tool, Platform platform, bool manager, string fix) =>
        Assert.Equal(fix, Fixes.Install(tool, platform, manager));

    [Fact]
    public async Task AManagerIsLookedForOnlyWhereTheFixDependsOnIt()
    {
        machine.Missing.Add(Tool.Git);
        machine.Missing.Add(Tool.ClaudeCode);

        await RunAsync();

        Assert.Equal(0, machine.ManagerAsked);
    }

    Task<IReadOnlyList<CheckResult>> RunAsync(string? origin = "acme/rocket") =>
        new Checkup(machine, store, origin).RunAsync(view, TestContext.Current.CancellationToken);

    static CheckResult Of(IReadOnlyList<CheckResult> checks, string name)
    {
        var check = Assert.Single(checks, check => check.Name == name);
        return check;
    }

    static ProjectSettings Project(string? board) => new(
        new TrackerSettings(SettingsKeys.GitHubTracker, board),
        new QueueSettings(new LabelSettings(["manual"], ["feature"], "needs-owner", "interrupted")),
        new AssistantSettings(SettingsKeys.ClaudeCodeAssistant));

    // A Mac with Homebrew where everything is installed, logged in and readable, until a test says otherwise.
    sealed class FakeCheckupMachine : ICheckupMachine
    {
        public Platform Platform { get; set; } = Platform.MacOS;

        public HashSet<Tool> Missing { get; } = [];

        public bool HasManager { get; set; } = true;

        public int ManagerAsked { get; private set; }

        public bool TrackerLoggedIn { get; set; } = true;

        public bool AssistantLoggedIn { get; set; } = true;

        public bool RepositoryReadable { get; set; } = true;

        public BoardAccess Board { get; set; } = BoardAccess.Readable;

        public List<string> Boards { get; } = [];

        public IReadOnlyList<string>? Labels { get; set; } = ["manual", "feature", "needs-owner", "interrupted"];

        public Task<bool> HasAsync(Tool tool, CancellationToken cancellationToken) => Task.FromResult(!Missing.Contains(tool));

        public Task<bool> HasManagerAsync(CancellationToken cancellationToken)
        {
            ManagerAsked++;
            return Task.FromResult(HasManager);
        }

        public Task<bool> TrackerLoggedInAsync(CancellationToken cancellationToken) => Task.FromResult(TrackerLoggedIn);

        public Task<bool> AssistantLoggedInAsync(CancellationToken cancellationToken) => Task.FromResult(AssistantLoggedIn);

        public Task<bool> CanReadRepositoryAsync(string repository, CancellationToken cancellationToken) => Task.FromResult(RepositoryReadable);

        public Task<BoardAccess> CheckBoardAsync(string board, CancellationToken cancellationToken)
        {
            Boards.Add(board);
            return Task.FromResult(Board);
        }

        public Task<IReadOnlyList<string>?> ReadLabelsAsync(string repository, CancellationToken cancellationToken) => Task.FromResult(Labels);
    }

    sealed class Store : ISettingsStore
    {
        public ProjectSettings? Current { get; set; }

        public IReadOnlyList<SettingsError>? Errors { get; set; }

        public string DisplayPath => ".toobusy/settings.toml";

        public SettingsValidation? Load() =>
            Errors is not null ? new SettingsValidation(null, Errors) : Current is null ? null : new SettingsValidation(Current, []);

        public SettingsPreview Preview(ProjectSettings settings) => new("", "");

        public void Save(ProjectSettings settings) => Current = settings;
    }

    sealed class Watched : ICheckupView
    {
        public List<string> Told { get; } = [];

        public void Start(string name) => Told.Add($"start {name}");

        public void Done(CheckResult result) => Told.Add($"done {result.Name}");
    }
}
