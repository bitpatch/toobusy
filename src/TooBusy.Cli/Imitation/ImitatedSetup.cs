using TooBusy.Core.Assistant;
using TooBusy.Core.Queue;
using TooBusy.Core.Run;
using TooBusy.Core.Settings;
using TooBusy.Core.Setup;
using TooBusy.Infrastructure.Settings;

namespace TooBusy.Cli.Imitation;

// What `init --demo` puts in place of the machine and the tracker: everything is installed and logged in,
// every board can be read, the repository has made-up labels, and nothing is made or linked.
// The repository itself is the one it is given: the real `origin` of the project when it is known.
public sealed class ImitatedSetup(string? origin) : ISetupEnvironment, ISetupTracker
{
    public Task<SetupEnvironment> InspectAsync(CancellationToken cancellationToken) =>
        Task.FromResult(new SetupEnvironment([], TrackerReachable: true, origin));

    public Task<string?> RefuseBoardAsync(string board, CancellationToken cancellationToken) => Task.FromResult<string?>(null);

    public Task<IReadOnlyList<string>> ReadLabelsAsync(string repository, CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<string>>(["bug", "feature", "chore", "docs", "draft", "interrupted", "manual", "question"]);

    public Task<string> CreateBoardAsync(SetupOwner owner, string title, CancellationToken cancellationToken) =>
        Task.FromResult($"https://github.com/{(owner.Organisation ? "orgs" : "users")}/{owner.Login}/projects/1");

    public Task LinkBoardAsync(string board, string repository, CancellationToken cancellationToken) => Task.CompletedTask;

    public Task CreateLabelAsync(string repository, string name, CancellationToken cancellationToken) => Task.CompletedTask;
}

// The boards of a demo: the real ones of the user when they can be read, and made-up ones after them until
// there are enough to try a list that scrolls. The owners a board can be made for are filled up the same way.
// They take their time, so that the wait for them can be looked at, unless nobody watches.
public sealed class ImitatedBoards(ISetupBoards? real, IClock clock, bool watched = true) : ISetupBoards
{
    const int Enough = 10;

    static readonly TimeSpan Wait = TimeSpan.FromSeconds(5);

    static readonly string[] Titles =
        ["Rocket", "Moon base", "Night shift", "Lighthouse", "Paper trail", "Second wind", "Tide table", "Open road", "Low tide", "Far shore"];

    static readonly string[] Logins =
    [
        "example", "example-labs", "example-studio", "example-works", "example-press",
        "example-tools", "example-garden", "example-harbour", "example-atlas", "example-north",
    ];

    // The same boards for a setup that has no screen: there is no wait to look at.
    public ImitatedBoards Unwatched => new(real, clock, watched: false);

    public async Task<SetupBoards> ReadAsync(string? repository, CancellationToken cancellationToken)
    {
        var waited = watched ? clock.DelayAsync(Wait, cancellationToken) : Task.CompletedTask;
        var read = real is null ? SetupBoards.None : await real.ReadAsync(repository, cancellationToken);
        await waited;
        var boards = Titles.Take(Math.Max(0, Enough - read.Boards.Count))
            .Select((title, index) => new SetupBoard($"https://github.com/users/example/projects/{index + 1}", $"{title} (made up)", Linked: false));
        var owners = Logins.Take(Math.Max(0, Enough - read.Owners.Count))
            .Select((login, index) => new SetupOwner(login, Organisation: index > 0));
        return new SetupBoards([.. read.Boards, .. boards], [.. read.Owners, .. owners]);
    }
}

// The milestones of a demo: the real open ones of the repository when they can be read, and made-up ones after
// them until there are enough to try a list that scrolls.
public sealed class ImitatedMilestones(IMilestones? real) : IMilestones
{
    const int Enough = 10;

    static readonly (string Title, int Month, int Tasks)[] MadeUp =
    [
        ("v0.1.0", 1, 3), ("v0.2.0", 2, 12), ("v0.3.0", 4, 7), ("v0.4.0", 0, 21), ("v1.0.0", 9, 34),
        ("Backlog", 0, 58), ("Polish", 0, 1), ("Docs", 0, 0), ("Research", 0, 5), ("Someday", 0, 16),
    ];

    IReadOnlyList<Milestone>? shown;

    // The real ones are read once: a demo shows the same milestones for as long as it lasts.
    public async Task<IReadOnlyList<Milestone>?> ReadOpenAsync(string repository, CancellationToken cancellationToken)
    {
        if (shown is not null)
            return shown;

        var read = real is null ? null : await real.ReadOpenAsync(repository, cancellationToken);
        var made = MadeUp.Take(Math.Max(0, Enough - (read?.Count ?? 0)))
            .Select(milestone => new Milestone($"{milestone.Title} (made up)", milestone.Month == 0 ? null : new DateOnly(2030, milestone.Month, 1), milestone.Tasks));
        return shown = [.. read ?? [], .. made];
    }
}

// The choices of the user in a demo: they are remembered until the demo ends, and nowhere else.
public sealed class UnsavedChoice(MilestoneChoice? milestone, ModelChoice? model, string? level) : IPersonalSettings
{
    public MilestoneChoice? LoadMilestone() => milestone;

    public void SaveMilestone(MilestoneChoice choice) => milestone = choice;

    public ModelChoice? LoadModel() => model;

    public void SaveModel(ModelChoice choice) => model = choice;

    public string? LoadEffort() => level;

    public void SaveEffort(string effort) => level = effort;
}

// The settings of a demo: the file is read as it is and never written; what the demo saves is remembered until it
// ends, so that the demo goes on as if the file were written. A demo that is to show a project that is set up
// starts with made-up settings where the project has none.
public sealed class UnwrittenSettings(ISettingsStore file, bool madeUp = false) : ISettingsStore
{
    ProjectSettings? saved = madeUp && file.Load() is null
        ? new ProjectSettings(
            new TrackerSettings(SettingsKeys.GitHubTracker, "https://github.com/users/example/projects/1"),
            new QueueSettings(new LabelSettings(["manual", "draft"], ["feature", "bug", "chore", "docs"], "needs-owner", "interrupted")),
            new AssistantSettings(SettingsKeys.ClaudeCodeAssistant))
        : null;

    public string DisplayPath => file.DisplayPath;

    public SettingsValidation? Load() => saved is null ? file.Load() : new SettingsValidation(saved, []);

    public SettingsPreview Preview(ProjectSettings settings) =>
        saved is null ? file.Preview(settings) : new SettingsPreview(SettingsToml.Write(saved), SettingsToml.Write(settings));

    public void Save(ProjectSettings settings) => saved = settings;
}
