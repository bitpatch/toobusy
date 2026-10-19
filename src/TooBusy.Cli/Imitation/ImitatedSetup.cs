using TooBusy.Core.Assistant;
using TooBusy.Core.Queue;
using TooBusy.Core.Run;
using TooBusy.Core.Settings;
using TooBusy.Core.Setup;
using TooBusy.Infrastructure.Settings;

namespace TooBusy.Cli.Imitation;

// What `init --demo` puts in place of the machine and the tracker: everything is installed and logged in, the
// repository is the one of the made-up project, with its labels, every board can be read and has statuses, the
// project has whatever skill it names, and nothing is made or linked.
public sealed class ImitatedSetup : ISetupEnvironment, ISetupTracker
{
    public Task<SetupEnvironment> InspectAsync(CancellationToken cancellationToken) =>
        Task.FromResult(new SetupEnvironment([], TrackerReachable: true, DemoProject.Repository));

    public SkillLook FindSkill(string name) => Skill(name);

    // The skill of a demo is there whatever its name is, where a project keeps its skills for Claude Code.
    public static SkillLook Skill(string name) => new(true, $".claude/skills/{name}/SKILL.md");

    public Task<string?> RefuseBoardAsync(string board, CancellationToken cancellationToken) => Task.FromResult<string?>(null);

    // The statuses of a board of a demo: those GitHub gives a new one.
    public static IReadOnlyList<string> Statuses { get; } = BoardStatuses.Default.Names;

    // What one of the made-up boards calls its statuses, so that the questions about them can be tried.
    public static IReadOnlyList<string> OtherStatuses { get; } = ["Backlog", "Ready", "Doing", "Review", "Shipped"];

    public Task<IReadOnlyList<string>?> ReadStatusesAsync(string board, CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<string>?>(StatusesOf(board));

    // What a board of a demo calls its statuses: one of the made-up boards calls them otherwise, and every other
    // board as GitHub does.
    public static IReadOnlyList<string> StatusesOf(string board) => ImitatedBoards.NamesOtherwise(board) ? OtherStatuses : Statuses;

    public Task<IReadOnlyList<string>> ReadLabelsAsync(string repository, CancellationToken cancellationToken) =>
        Task.FromResult(DemoProject.Labels);

    public Task<string> CreateBoardAsync(SetupOwner owner, string title, CancellationToken cancellationToken) =>
        Task.FromResult(ImitatedBoards.Next(owner));

    public Task LinkBoardAsync(string board, string repository, CancellationToken cancellationToken) => Task.CompletedTask;

    public Task CreateLabelAsync(string repository, string name, CancellationToken cancellationToken) => Task.CompletedTask;
}

// The boards of a demo: a few made-up ones of the user and of the organisation of the repository, the board of the
// settings among them, linked to the repository; the user and the organisation are those a board can be made for.
// They take their time, so that the wait for them can be looked at, unless nobody watches: a setup without a screen
// has no wait to look at.
public sealed class ImitatedBoards(IClock clock, bool watched) : ISetupBoards
{
    static readonly TimeSpan Wait = TimeSpan.FromSeconds(5);

    static readonly SetupBoard[] Boards =
    [
        new(DemoProject.Board, DemoProject.BoardTitle, Linked: true),
        new($"{Address(DemoProject.Organisation, organisation: true)}2", "Marketing site", Linked: false),
        new($"{Address(DemoProject.Organisation, organisation: true)}3", "Roadmap", Linked: false),
        new($"{Address(DemoProject.User, organisation: false)}1", "Side projects", Linked: false),
    ];

    static readonly SetupOwner[] Owners = [new(DemoProject.User, Organisation: false), new(DemoProject.Organisation, Organisation: true)];

    // Whether a board is the made-up one that calls its statuses otherwise than GitHub does.
    public static bool NamesOtherwise(string board) => board.Equals(Boards[1].Address, StringComparison.OrdinalIgnoreCase);

    // The address a new board of the owner would have: the next number after their made-up boards.
    public static string Next(SetupOwner owner)
    {
        var address = Address(owner.Login, owner.Organisation);
        return $"{address}{Boards.Count(board => board.Address.StartsWith(address, StringComparison.OrdinalIgnoreCase)) + 1}";
    }

    public async Task<SetupBoards> ReadAsync(string? repository, CancellationToken cancellationToken)
    {
        if (watched)
            await clock.DelayAsync(Wait, cancellationToken);
        return new SetupBoards(Boards, Owners);
    }

    // The address of a board of the owner, before its number.
    static string Address(string login, bool organisation) => $"https://github.com/{(organisation ? "orgs" : "users")}/{login}/projects/";
}

// The milestones of a demo: a few made-up open ones of the repository.
public sealed class ImitatedMilestones : IMilestones
{
    public static IReadOnlyList<Milestone> Open { get; } =
    [
        new("v1.0.0", new DateOnly(2030, 3, 1), 15),
        new("v1.1.0", new DateOnly(2030, 6, 1), 6),
        new("Later", null, 9),
    ];

    public Task<IReadOnlyList<Milestone>?> ReadOpenAsync(string repository, CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<Milestone>?>(Open);
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

// The settings of a demo: there is no file, and what the demo saves is remembered until it ends, so that the demo goes
// on as if a file were written. A demo that shows a project that is set up starts with the made-up settings, and any
// other with none.
public sealed class UnwrittenSettings(ProjectSettings? settings) : ISettingsStore
{
    ProjectSettings? saved = settings;

    public string DisplayPath => SettingsFile.DisplayPath;

    public SettingsValidation? Load() => saved is null ? null : new SettingsValidation(saved, []);

    public SettingsPreview Preview(ProjectSettings settings)
    {
        var before = saved is null ? "" : SettingsToml.Write(saved);
        return new SettingsPreview(before, SettingsToml.Write(settings, before));
    }

    public void Save(ProjectSettings settings) => saved = settings;
}
