using TooBusy.Assistants.ClaudeCode;
using TooBusy.Cli.Imitation;
using TooBusy.Core.Assistant;
using TooBusy.Core.Processes;
using TooBusy.Core.Queue;
using TooBusy.Core.Run;
using TooBusy.Core.Settings;
using TooBusy.Core.Setup;
using TooBusy.Infrastructure.Git;
using TooBusy.Infrastructure.Machine;
using TooBusy.Infrastructure.Run;
using TooBusy.Infrastructure.Settings;
using TooBusy.Trackers.GitHub;

namespace TooBusy.Cli;

// Everything the commands work with in one project: its root, its settings, the tracker and the choice of the user.
// It is put together in one place, real or, for a demo, imitated. Repository is where the milestones and the tasks
// are read from; null when the `origin` remote is not a GitHub repository. Processes runs the commands of the
// machine, and Home is the home folder of the user, where Claude Code keeps its conversations.
public sealed record Workbench(
    string Root,
    bool Demo,
    string? Repository,
    ISetupEnvironment Environment,
    ISetupTracker Tracker,
    ISetupBoards Boards,
    ISettingsStore Settings,
    IMilestones Milestones,
    IPersonalSettings Personal,
    IClock Clock,
    IProcessRunner Processes,
    string Home)
{
    // `personalFolder` is where the choices of the user are kept. A demo keeps nothing. What it reads is real where
    // reading changes nothing: the `origin` remote, the boards and the milestones; what it would make, link, write
    // and remember is imitated. A demo that is `ready` shows a project that is set up and has everything chosen,
    // whatever the project is: where there are no settings they are made up, the first milestone is the chosen one,
    // the model is the assistant's own and the effort is the proposed one.
    public static async Task<Workbench> OpenAsync(string root, bool demo, IProcessRunner? processes, string personalFolder, bool ready, IClock clock, string home, CancellationToken cancellationToken)
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
                new ImitatedBoards(processes is null ? null : new GitHubBoards(processes), clock),
                new UnwrittenSettings(new SettingsFile(root), madeUp: ready),
                milestones,
                new UnsavedChoice(
                    first is null ? null : new MilestoneChoice(first.Title),
                    ready ? ModelChoice.AssistantsOwn : null,
                    ready ? ClaudeCodeOptions.ProposedEffort : null),
                clock,
                processes ?? new NoProcesses(),
                home);
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
            new PersonalSettingsFile(personalFolder, root),
            clock,
            processes,
            home);
    }

    // A run of the queue over the settings and the choices of the user as they are now; the milestone is null for
    // tasks whatever their milestone, and the share of the weekly limit is the proposed one until the user chooses. In a demo the run is an imitated one. Otherwise the tasks are the issues of
    // the repository, Claude Code does them in the working copy, and what the run leaves on this machine is kept in
    // the local folder of the project. Null when the project has no tasks to take: its `origin` remote is not a
    // GitHub repository.
    public IQueueRun? OpenRun(ProjectSettings settings, string? milestone, ModelChoice model, string effort)
    {
        var plan = new RunPlan(milestone, QueueRules.Of(settings), model, effort, Personal.LoadShare() ?? UsageShare.Proposed);
        if (Demo)
            return ImitatedRun.Open(plan, Clock);
        if (Repository is null)
            return null;

        var local = LocalFolder.Ensure(Root);
        return new Supervisor(
            new GitHubTasks(Processes, Repository, settings.Tracker.Board),
            new ClaudeCode(Processes.Inside(Root), Clock, ClaudeFolders.Of(Root, local, Home, System.Environment.GetEnvironmentVariable), Root),
            new GitWorkspace(Processes, Root),
            new RunStateFile(local),
            new LocalMachine(Processes),
            Clock,
            plan,
            RunPolicy.Default);
    }

    // How many tasks a run over these settings and this milestone would take as things are: those that are ready
    // and those that open after them. Null when the tasks cannot be read. A demo counts its made-up tasks, and takes
    // a moment over it, as a tracker does.
    public async Task<int?> CountTasksAsync(ProjectSettings settings, string? milestone, CancellationToken cancellationToken)
    {
        var rules = QueueRules.Of(settings);
        ITaskTracker? tasks = Demo ? new ImitatedTasks(rules) : Repository is null ? null : new GitHubTasks(Processes, Repository, settings.Tracker.Board);
        if (tasks is null)
            return null;

        try
        {
            if (Demo)
                await Clock.DelayAsync(TimeSpan.FromSeconds(1), cancellationToken);

            var lineup = TaskLineup.Arrange(await tasks.ReadOpenAsync(milestone, cancellationToken), rules);
            return lineup.Ready.Count + lineup.Later.Count;
        }
        catch (Exception refused) when (refused is TrackerException or OperationCanceledException)
        {
            return null;
        }
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

        public IProcessRunner Inside(string folder) => this;
    }
}
