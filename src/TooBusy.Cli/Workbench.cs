using TooBusy.Assistants.ClaudeCode;
using TooBusy.Cli.Imitation;
using TooBusy.Core.Assistant;
using TooBusy.Core.Doctor;
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
// machine, Home is the home folder of the user, where Claude Code keeps its conversations, and Machine is what
// the checks of `doctor` ask.
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
    string Home,
    ICheckupMachine Machine)
{
    // The made-up tasks of a demo and the record of its runs: the menu and the next run see a task that an abort
    // left interrupted. They are kept in memory for as long as the demo goes on, and nowhere else.
    readonly UnwrittenState remembered = new();
    ImitatedTasks? kept;

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
            var unwritten = new UnwrittenSettings(new SettingsFile(root), madeUp: ready);
            var milestones = new ImitatedMilestones(processes is null ? null : new GitHubMilestones(processes));
            var first = ready && MilestoneOrder.Sorted(await milestones.ReadOpenAsync(repository, cancellationToken) ?? []) is [var lowest, ..] ? lowest : null;
            return new Workbench(
                root,
                demo,
                repository,
                imitated,
                imitated,
                new ImitatedBoards(processes is null ? null : new GitHubBoards(processes), clock),
                unwritten,
                milestones,
                new UnsavedChoice(
                    first is null ? null : new MilestoneChoice(first.Title),
                    ready ? ModelChoice.AssistantsOwn : null,
                    ready ? ClaudeCodeOptions.ProposedEffort : null),
                clock,
                processes ?? new NoProcesses(),
                home,
                new ImitatedCheckup(clock, unwritten));
        }

        processes ??= new NoProcesses();
        var origin = await new GitOrigin(processes).ReadAsync(root, cancellationToken);
        var machine = new MachineCheckup(processes, MachineCheckup.Here);
        return new Workbench(
            root,
            demo,
            origin,
            new MachineEnvironment(machine, origin),
            new GitHubSetup(processes),
            new GitHubBoards(processes),
            new SettingsFile(root),
            new GitHubMilestones(processes),
            new PersonalSettingsFile(personalFolder, root),
            clock,
            processes,
            home,
            machine);
    }

    // The checks of everything a run needs, over the settings as they are now.
    public Checkup Checkup => new(Machine, Settings, Repository);

    // A run of the queue over the settings and the choices of the user as they are now; the milestone is null for
    // tasks whatever their milestone. In a demo the run is an imitated one. Otherwise the tasks are the issues of
    // the repository, Claude Code does them in the working copy, and what the run leaves on this machine is kept in
    // the local folder of the project. Null when the project has no tasks to take: its `origin` remote is not a
    // GitHub repository.
    public IQueueRun? OpenRun(ProjectSettings settings, string? milestone, ModelChoice model, string effort)
    {
        var plan = new RunPlan(milestone, QueueRules.Of(settings), model, effort);
        if (Demo)
            return ImitatedRun.Open(plan, Clock, DemoTasks(plan.Rules), remembered);
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

    // The made-up tasks of a demo: those that were kept while one of them is interrupted or has a record, and
    // otherwise tasks that are made up anew, so that a demo can be run again and again. The first of them have a
    // task that a run was killed over, with its record, so that a demo starts by going on with a session.
    ImitatedTasks DemoTasks(QueueRules rules)
    {
        if (kept is null)
        {
            kept = new ImitatedTasks(rules, killed: true);
            remembered.Save(new TaskRecord(ImitatedTasks.Killed, ImitatedAssistant.Trace(ImitatedTasks.Killed, Clock.Now)));
        }
        else if (remembered.Load() is null && !kept.HasInterrupted(rules.Interrupted))
        {
            kept = new ImitatedTasks(rules, killed: false);
        }

        return kept;
    }

    // What a run over these settings and this milestone would take as things are: how many tasks, those that are
    // ready and those that open after them, and the one it goes on with first: the task of the record an earlier
    // run left, or one that was interrupted before. Null when the tasks cannot be read. A demo counts its made-up tasks, and takes a moment over it, as a tracker does.
    public async Task<QueueOutlook?> OutlookAsync(ProjectSettings settings, string? milestone, CancellationToken cancellationToken)
    {
        var rules = QueueRules.Of(settings);
        ITaskTracker? tasks = Demo ? DemoTasks(rules) : Repository is null ? null : new GitHubTasks(Processes, Repository, settings.Tracker.Board);
        if (tasks is null)
            return null;

        try
        {
            if (Demo)
                await Clock.DelayAsync(TimeSpan.FromSeconds(1), cancellationToken);

            var open = await tasks.ReadOpenAsync(milestone, cancellationToken);
            IRunState state = Demo ? remembered : new RunStateFile(LocalFolder.Of(Root));
            var recorded = state.Load() is { } record ? TaskLineup.Recorded(open, rules, record.Number).Task : null;
            return TaskLineup.Arrange(open, rules).Outlook(rules, recorded);
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
