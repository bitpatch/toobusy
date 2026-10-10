using TooBusy.Core.Settings;
using TooBusy.Core.Setup;

namespace TooBusy.Cli.Imitation;

// What `init --dry-run` puts in place of the machine and the tracker: everything is installed and logged in,
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

// The boards of a dry run: the real ones of the user when they can be read, and made-up ones after them until
// there are enough to try a list that scrolls. Without anyone to make a board for, a made-up owner stands in.
public sealed class ImitatedBoards(ISetupBoards? real) : ISetupBoards
{
    const int Enough = 8;

    static readonly string[] Titles = ["Rocket", "Moon base", "Night shift", "Lighthouse", "Paper trail", "Second wind", "Tide table", "Open road"];

    public async Task<SetupBoards> ReadAsync(string? repository, CancellationToken cancellationToken)
    {
        var read = real is null ? SetupBoards.None : await real.ReadAsync(repository, cancellationToken);
        var made = Titles.Take(Math.Max(0, Enough - read.Boards.Count))
            .Select((title, index) => new SetupBoard($"https://github.com/users/example/projects/{index + 1}", $"{title} (made up)", Linked: false));
        return new SetupBoards([.. read.Boards, .. made], read.Owners.Count > 0 ? read.Owners : [new SetupOwner("example", Organisation: false)]);
    }
}

// The settings file of a dry run: it is read as it is and never written.
public sealed class UnwrittenSettings(ISettingsStore file) : ISettingsStore
{
    public string DisplayPath => file.DisplayPath;

    public SettingsValidation? Load() => file.Load();

    public SettingsPreview Preview(ProjectSettings settings) => file.Preview(settings);

    public void Save(ProjectSettings settings)
    {
    }
}
