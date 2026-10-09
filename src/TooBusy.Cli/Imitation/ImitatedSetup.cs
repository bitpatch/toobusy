using TooBusy.Core.Queue;
using TooBusy.Core.Settings;
using TooBusy.Core.Setup;

namespace TooBusy.Cli.Imitation;

// What `init --dry-run` puts in place of the machine and the tracker: everything is installed and logged in,
// every repository and board can be read, and the repository has made-up labels and milestones.
public sealed class ImitatedSetup : ISetupEnvironment, ISetupTracker
{
    public Task<SetupEnvironment> InspectAsync(CancellationToken cancellationToken) =>
        Task.FromResult(new SetupEnvironment([], TrackerReachable: true, OriginRepository: "example/project"));

    public Task<string?> RefuseRepositoryAsync(string repository, CancellationToken cancellationToken) => Task.FromResult<string?>(null);

    public Task<string?> RefuseBoardAsync(string board, CancellationToken cancellationToken) => Task.FromResult<string?>(null);

    public Task<IReadOnlyList<string>> ReadLabelsAsync(string repository, CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<string>>(["bug", "feature", "chore", "docs", "draft", "manual", "question"]);

    public Task<IReadOnlyList<Milestone>> ReadOpenMilestonesAsync(string repository, CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<Milestone>>(
        [
            new Milestone("v.0.2.0", null),
            new Milestone("v.0.1.0", new DateOnly(2030, 3, 1)),
            new Milestone("Polish", new DateOnly(2030, 1, 15)),
        ]);
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
