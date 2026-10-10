using TooBusy.Core.Settings;
using TooBusy.Core.Setup;

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
        Task.FromResult<IReadOnlyList<string>>(["bug", "feature", "chore", "docs", "draft", "manual", "question"]);

    public Task<string> CreateBoardAsync(SetupOwner owner, string title, CancellationToken cancellationToken) =>
        Task.FromResult($"https://github.com/{(owner.Organisation ? "orgs" : "users")}/{owner.Login}/projects/1");

    public Task LinkBoardAsync(string board, string repository, CancellationToken cancellationToken) => Task.CompletedTask;
}

// The boards of a demo: the real ones of the user when they can be read, and made-up ones after them until
// there are enough to try a list that scrolls. The owners a board can be made for are filled up the same way.
public sealed class ImitatedBoards(ISetupBoards? real) : ISetupBoards
{
    const int Enough = 10;

    static readonly string[] Titles =
        ["Rocket", "Moon base", "Night shift", "Lighthouse", "Paper trail", "Second wind", "Tide table", "Open road", "Low tide", "Far shore"];

    static readonly string[] Logins =
    [
        "example", "example-labs", "example-studio", "example-works", "example-press",
        "example-tools", "example-garden", "example-harbour", "example-atlas", "example-north",
    ];

    public async Task<SetupBoards> ReadAsync(string? repository, CancellationToken cancellationToken)
    {
        var read = real is null ? SetupBoards.None : await real.ReadAsync(repository, cancellationToken);
        var boards = Titles.Take(Math.Max(0, Enough - read.Boards.Count))
            .Select((title, index) => new SetupBoard($"https://github.com/users/example/projects/{index + 1}", $"{title} (made up)", Linked: false));
        var owners = Logins.Take(Math.Max(0, Enough - read.Owners.Count))
            .Select((login, index) => new SetupOwner(login, Organisation: index > 0));
        return new SetupBoards([.. read.Boards, .. boards], [.. read.Owners, .. owners]);
    }
}

// The settings file of a demo: it is read as it is and never written.
public sealed class UnwrittenSettings(ISettingsStore file) : ISettingsStore
{
    public string DisplayPath => file.DisplayPath;

    public SettingsValidation? Load() => file.Load();

    public SettingsPreview Preview(ProjectSettings settings) => file.Preview(settings);

    public void Save(ProjectSettings settings)
    {
    }
}
