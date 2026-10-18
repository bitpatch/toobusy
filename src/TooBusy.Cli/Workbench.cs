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
using TooBusy.Infrastructure.Processes;
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
        var machine = new MachineCheckup(processes, MachineCheckup.Here, root, ClaudeFolders.Configuration(home, System.Environment.GetEnvironmentVariable));
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
    // GitHub repository. `only` is the one task the run does and `most` how many it takes at most; without them it
    // is a run of the whole queue. `skip` are the labels whose tasks the run leaves out. A demo of a run of one task
    // has no task that a run was killed over, unless it is the one: the record of that task would keep the run from
    // any other.
    public IQueueRun? OpenRun(ProjectSettings settings, string? milestone, ModelChoice model, string effort, int? only = null, int? most = null, IReadOnlyList<string>? skip = null)
    {
        var plan = new RunPlan(milestone, QueueRules.Of(settings) with { Skip = skip ?? [] }, model, effort) { Board = settings.Tracker.Board, Skill = settings.Skill, Only = only, Most = most, Commands = settings.Commands, Branch = settings.Branch, PermissionMode = settings.Assistant.SessionMode };
        if (Demo)
            return ImitatedRun.Open(plan, Clock, DemoTasks(plan.Rules, killed: only is null or ImitatedTasks.Killed), remembered);
        if (Repository is null)
            return null;

        var local = LocalFolder.Ensure(Root);
        return new Supervisor(
            new GitHubTasks(Processes, Repository, settings.Tracker.Board, settings.Tracker.Statuses),
            new ClaudeCode(Processes.Inside(Root), Clock, ClaudeFolders.Of(Root, local, Home, System.Environment.GetEnvironmentVariable), Root, plan.PermissionMode),
            new GitWorkspace(Processes, Root),
            new RunStateFile(local),
            new RunSignFile(local),
            new LocalMachine(Processes),
            Clock,
            new ShellCommands(Root),
            plan,
            RunPolicy.Default);
    }

    // The run that goes on in the working copy; null when none does. A demo looks at no sign, as it puts none up.
    public GoingRun? Going() => Demo ? null : new RunSignFile(LocalFolder.Of(Root)).Read();

    // The made-up tasks of a demo: those that were kept while one of them is interrupted or has a record, and
    // otherwise tasks that are made up anew, so that a demo can be run again and again. The first of them have a
    // task that a run was killed over, with its record, so that a demo starts by going on with a session.
    ImitatedTasks DemoTasks(QueueRules rules, bool killed = true)
    {
        if (kept is null && !killed)
        {
            kept = new ImitatedTasks(rules, killed: false);
        }
        else if (kept is null)
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
    // run left, or one that was interrupted before. `skip` are the labels whose tasks the run leaves out. Null when
    // the tasks cannot be read. A demo counts its made-up tasks, and takes a moment over it, as a tracker does.
    public async Task<QueueOutlook?> OutlookAsync(ProjectSettings settings, string? milestone, IReadOnlyList<string> skip, CancellationToken cancellationToken)
    {
        try
        {
            if (Demo)
                await Clock.DelayAsync(TimeSpan.FromSeconds(1), cancellationToken);

            if (await ReadQueueAsync(settings, milestone, skip, cancellationToken) is not var (lineup, recorded))
                return null;

            var outlook = lineup.Outlook(QueueRules.Of(settings));
            return recorded is null ? outlook : outlook with { Interrupted = recorded };
        }
        catch (Exception refused) when (refused is TrackerException or OperationCanceledException)
        {
            return null;
        }
    }

    // The queue as a run over these settings and this milestone reads it before a task, and nothing is changed by
    // it: the lineup, with the task an earlier run left with a session before the others, and that task, null when
    // there is none or the run would not go on with it. `skip` are the labels whose tasks the run leaves out. In a
    // demo the tasks are its made-up ones. Null when the project has no tasks to read: its `origin` remote is not a
    // GitHub repository. Throws TrackerException when the tracker refuses.
    public async Task<(TaskLineup Lineup, QueueTask? Recorded)?> ReadQueueAsync(ProjectSettings settings, string? milestone, IReadOnlyList<string> skip, CancellationToken cancellationToken)
    {
        var rules = QueueRules.Of(settings) with { Skip = skip };
        ITaskTracker? tasks = Demo ? DemoTasks(rules) : Repository is null ? null : new GitHubTasks(Processes, Repository, settings.Tracker.Board, settings.Tracker.Statuses);
        if (tasks is null)
            return null;

        var open = await tasks.ReadOpenAsync(milestone, cancellationToken);
        IRunState state = Demo ? remembered : new RunStateFile(LocalFolder.Of(Root));
        var recorded = state.Load() is { } record ? TaskLineup.Recorded(open, rules, record.Number).Task : null;
        var lineup = TaskLineup.Arrange(open, rules);
        return (recorded is null ? lineup : lineup.Taking(recorded), recorded);
    }

    // How the project stands for a step of `toobusy init --assist`: the settings, the choices of the user, the skill
    // the settings name and the sign of a run, and from the tracker what the step chooses among, as the setup reads
    // it. Nothing is changed by it, and nothing is asked of the tracker for a step that chooses among nothing there.
    // What cannot be read is left out, with the reason when the tracker cannot be asked at all.
    public async Task<AssistGround> AssistAsync(AssistReading reading, CancellationToken cancellationToken)
    {
        var loaded = Settings.Load();
        var ground = new AssistGround(loaded, Settings.DisplayPath, Repository)
        {
            Milestone = Personal.LoadMilestone(),
            Model = Personal.LoadModel(),
            Effort = Personal.LoadEffort(),
            Skill = loaded?.Settings?.Skill is { } skill ? Machine.FindSkill(skill) : null,
            Going = Going()?.Sign.Told,
        };
        if (reading == AssistReading.Nothing)
            return ground;
        if (!await Machine.HasAsync(Tool.GitHubCli, cancellationToken))
            return ground with { Unread = "the GitHub command-line tool `gh` is not installed." };
        if (!await Machine.TrackerLoggedInAsync(cancellationToken))
            return ground with { Unread = "`gh` is not logged in to github.com." };

        // The boards are those of the user, whatever the repository is; the labels and the milestones are its own.
        if (reading == AssistReading.Boards)
        {
            var known = (await Boards.ReadAsync(Repository, cancellationToken)).Boards;
            if (loaded?.Settings?.Tracker.Board is { } kept && known.All(board => !board.Address.Equals(kept, StringComparison.OrdinalIgnoreCase)))
                known = [new SetupBoard(kept, "", Linked: false), .. known];

            // The statuses of a board are a request each: a few are asked at a time.
            var boards = new List<AssistBoard>();
            foreach (var some in known.Chunk(8))
            {
                boards.AddRange(await Task.WhenAll(some.Select(async board =>
                    new AssistBoard(board.Address, board.Title, board.Linked, await Machine.ReadStatusesAsync(board.Address, cancellationToken)))));
            }

            return ground with { Boards = boards };
        }

        if (Repository is null)
            return ground with { Unread = "the `origin` remote is not a GitHub repository." };
        return reading == AssistReading.Labels
            ? ground with { Labels = await Machine.ReadLabelsAsync(Repository, cancellationToken) }
            : ground with { Milestones = await ReadMilestonesAsync(cancellationToken) };
    }

    // The labels a run may be told to skip, read as `init` reads them for its questions: those of the repository and,
    // after them, those of the settings that it lacks. Null when they cannot be read, with why: without `gh`, logged
    // in, or a GitHub `origin` remote. A demo has its made-up labels.
    public async Task<(IReadOnlyList<string>? Labels, string? Unread)> ReadLabelsAsync(ProjectSettings settings, CancellationToken cancellationToken)
    {
        if (Repository is null)
            return (null, "The `origin` remote is not a GitHub repository, so there are no labels to choose from.");
        if (!await Machine.HasAsync(Tool.GitHubCli, cancellationToken))
            return (null, "The GitHub command-line tool `gh` is not installed, so the labels cannot be read.");
        if (!await Machine.TrackerLoggedInAsync(cancellationToken))
            return (null, "`gh` is not logged in to github.com, so the labels cannot be read.");

        var named = settings.Queue.Labels;
        string[] ofSettings = [.. named.Blocking, .. named.Take, named.Owner, named.Interrupted];
        return ([.. (await Tracker.ReadLabelsAsync(Repository, cancellationToken)).Concat(ofSettings).Distinct(StringComparer.OrdinalIgnoreCase)], null);
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
