using TooBusy.Cli.Imitation;
using TooBusy.Core.Assistant;
using TooBusy.Core.Processes;
using TooBusy.Core.Queue;
using TooBusy.Core.Settings;
using TooBusy.Core.Setup;
using TooBusy.Infrastructure.Git;
using TooBusy.Infrastructure.Settings;
using TooBusy.Trackers.GitHub;

namespace TooBusy.Cli;

// Everything the commands work with in one project: its root, its settings, the tracker and the choice of the user.
// It is put together in one place, real or, for a demo, imitated. Repository is where the milestones are read from;
// null when the `origin` remote is not a GitHub repository.
public sealed record Workbench(
    string Root,
    bool Demo,
    string? Repository,
    ISetupEnvironment Environment,
    ISetupTracker Tracker,
    ISetupBoards Boards,
    ISettingsStore Settings,
    IMilestones Milestones,
    IPersonalSettings Personal)
{
    // `personalFolder` is where the choices of the user are kept. A demo keeps nothing. What it reads is real where
    // reading changes nothing: the `origin` remote, the boards and the milestones; what it would make, link, write
    // and remember is imitated. A demo that is `ready` shows a project that is set up and has everything chosen,
    // whatever the project is: where there are no settings they are made up, the first milestone is the chosen one,
    // the model is the assistant's own and the effort is the proposed one.
    public static async Task<Workbench> OpenAsync(string root, bool demo, IProcessRunner? processes, string personalFolder, bool ready, CancellationToken cancellationToken)
    {
        if (demo)
        {
            var read = processes is null ? "example/project" : await new GitOrigin(processes).ReadAsync(root, cancellationToken);
            var repository = read ?? "example/project";
            var imitated = new ImitatedSetup(read);
            var milestones = new ImitatedMilestones(processes is null ? null : new GitHubMilestones(processes));
            var first = ready && MilestoneOrder.Sorted(await milestones.ReadOpenAsync(repository, cancellationToken) ?? []) is [var lowest, ..] ? lowest : null;
            return new Workbench(
                root,
                demo,
                repository,
                imitated,
                imitated,
                new ImitatedBoards(processes is null ? null : new GitHubBoards(processes)),
                new UnwrittenSettings(new SettingsFile(root), madeUp: ready),
                milestones,
                new UnsavedChoice(
                    first is null ? null : new MilestoneChoice(first.Title),
                    ready ? ModelChoice.AssistantsOwn : null,
                    ready ? ClaudeCodeOptions.ProposedEffort : null));
        }

        processes ??= new NoProcesses();
        var origin = await new GitOrigin(processes).ReadAsync(root, cancellationToken);
        return new Workbench(
            root,
            demo,
            origin,
            new MachineEnvironment(processes, origin),
            new GitHubSetup(processes),
            new GitHubBoards(processes),
            new SettingsFile(root),
            new GitHubMilestones(processes),
            new PersonalSettingsFile(personalFolder, root));
    }

    // The open milestones of the repository; null when they cannot be read.
    public async Task<IReadOnlyList<Milestone>?> ReadMilestonesAsync(CancellationToken cancellationToken) =>
        Repository is null ? null : await Milestones.ReadOpenAsync(Repository, cancellationToken);

    // Where the choice of the user stands now.
    public async Task<MilestoneStanding> StandAsync(CancellationToken cancellationToken)
    {
        var choice = Personal.LoadMilestone();
        return MilestoneStanding.Of(choice, choice?.Title is null ? null : await ReadMilestonesAsync(cancellationToken));
    }

    // A machine with no commands to run: every one of them is missing.
    sealed class NoProcesses : IProcessRunner
    {
        public Task<ProcessResult> RunAsync(string command, IReadOnlyList<string> arguments, TimeSpan timeout, CancellationToken cancellationToken) =>
            Task.FromResult(new ProcessResult(ProcessStatus.NotFound, 0, "", ""));
    }
}
