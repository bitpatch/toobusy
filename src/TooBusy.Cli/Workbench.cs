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
using TooBusy.Infrastructure.Run;
using TooBusy.Infrastructure.Settings;
using TooBusy.Trackers.GitHub;

namespace TooBusy.Cli;

// Everything the commands work with in one project: its folder, its settings, the tracker, the choice of the user and
// its runs. It is put together in one place, real or, for a demo, imitated. Folder is the folder of the project as the
// bar names it. Repository is where the milestones and the tasks are read from; null when the `origin` remote is not a
// GitHub repository. SetupMachine is what the setup asks of the machine, Machine is what the checks of `doctor` ask,
// and Runs is what a run is made of: the tasks it reads, the record it leaves and the sign it puts up.
public sealed class Workbench(
    string folder,
    bool demo,
    string? repository,
    ISetupEnvironment setupMachine,
    ISetupTracker tracker,
    ISetupBoards boards,
    ISettingsStore settings,
    IMilestones milestones,
    IPersonalSettings personal,
    IClock clock,
    ICheckupMachine machine,
    IProjectRuns runs)
{
    public string Folder => folder;

    public bool Demo => demo;

    public string? Repository => repository;

    public ISetupEnvironment SetupMachine => setupMachine;

    public ISetupTracker Tracker => tracker;

    public ISetupBoards Boards => boards;

    public ISettingsStore Settings => settings;

    public IMilestones Milestones => milestones;

    public IPersonalSettings Personal => personal;

    public IClock Clock => clock;

    public ICheckupMachine Machine => machine;

    public IProjectRuns Runs => runs;

    // The project at `root`, shown as `folder`. `personalFolder` is where the choices of the user are kept.
    // `processes` runs the commands of the machine, and `home` is the home folder of the user, where Claude Code keeps
    // its conversations unless `CLAUDE_CONFIG_DIR` among the `variables` of the environment puts them elsewhere.
    public static async Task<Workbench> OpenAsync(string root, string folder, IProcessRunner? processes, string personalFolder, IClock clock, string home, Func<string, string?> variables, CancellationToken cancellationToken)
    {
        processes ??= new NoProcesses();
        var origin = await new GitOrigin(processes).ReadAsync(root, cancellationToken);
        var machine = new MachineCheckup(processes, MachineCheckup.Here, root, ClaudeFolders.Configuration(home, variables));
        return new Workbench(
            folder,
            demo: false,
            origin,
            new MachineEnvironment(machine, origin),
            new GitHubSetup(processes),
            new GitHubBoards(processes),
            new SettingsFile(root),
            new GitHubMilestones(processes),
            new PersonalSettingsFile(personalFolder, root),
            clock,
            machine,
            new ProjectRuns(root, origin, processes, clock, home, variables));
    }

    // The made-up project of a demo, the same wherever it is opened: nothing of the folder or of the machine is read,
    // and what it would make, link, write and remember is imitated. A demo that is `ready` shows a project that is
    // set up and has everything chosen: its settings are made up, the first milestone is the chosen one, the model
    // is the assistant's own and the effort is the proposed one; any other demo starts with no settings and nothing
    // chosen. `watched` says whether a page shows what is read while it is read: a demo takes its time over the
    // boards and the tasks only then, so that the wait for them can be looked at.
    public static Workbench OpenDemo(bool ready, bool watched, IClock clock)
    {
        var imitated = new ImitatedSetup();
        var unwritten = new UnwrittenSettings(ready ? DemoProject.Settings : null);
        return new Workbench(
            DemoProject.Folder,
            demo: true,
            DemoProject.Repository,
            imitated,
            imitated,
            new ImitatedBoards(clock, watched),
            unwritten,
            new ImitatedMilestones(),
            new UnsavedChoice(
                ready ? new MilestoneChoice(MilestoneOrder.Sorted(ImitatedMilestones.Open)[0].Title) : null,
                ready ? ModelChoice.AssistantsOwn : null,
                ready ? ClaudeCodeOptions.ProposedEffort : null),
            clock,
            new ImitatedCheckup(clock, unwritten),
            new ImitatedRuns(clock, watched));
    }

    // The checks of everything a run needs, over the settings as they are now.
    public Checkup Checkup => new(Machine, Settings, Repository);

    // A run of the queue over the settings and the choices of the user as they are now; the milestone is null for
    // tasks whatever their milestone. Null when the project has no tasks to take: its `origin` remote is not a GitHub
    // repository. `only` is the one task the run does and `most` how many it takes at most; without them it is a run
    // of the whole queue. `skip` are the labels whose tasks the run leaves out, and `sessions` how many work at once,
    // for this run; without it the number the user chose.
    public IQueueRun? OpenRun(ProjectSettings settings, string? milestone, ModelChoice model, string effort, int? only = null, int? most = null, IReadOnlyList<string>? skip = null, int? sessions = null) =>
        Runs.Open(settings, new RunPlan(milestone, QueueRules.Of(settings) with { Skip = skip ?? [] }, model, effort) { Board = settings.Tracker.Board, Skill = settings.Session.Skill, Only = only, Most = most, Commands = settings.Commands, Branch = settings.Workspace.Branch, PermissionMode = settings.Assistant.SessionMode, Sessions = sessions ?? Personal.LoadSessions() ?? 1 });

    // The run that goes on in the working copy; null when none does.
    public GoingRun? Going() => Runs.Going();

    // What a run over these settings and this milestone would take as things are: how many tasks, those that are
    // ready and those that open after them, and the one it goes on with first: the task of the record an earlier
    // run left, or one that was interrupted before. `skip` are the labels whose tasks the run leaves out. Null when
    // the tasks cannot be read.
    public async Task<QueueOutlook?> OutlookAsync(ProjectSettings settings, string? milestone, IReadOnlyList<string> skip, CancellationToken cancellationToken)
    {
        try
        {
            if (await ReadQueueAsync(settings, milestone, skip, cancellationToken) is not var (lineup, recorded))
                return null;

            var outlook = lineup.Outlook(QueueRules.Of(settings));
            return recorded is [var first, ..] ? outlook with { Interrupted = first } : outlook;
        }
        catch (Exception refused) when (refused is TrackerException or OperationCanceledException)
        {
            return null;
        }
    }

    // The queue as a run over these settings and this milestone reads it before a task, and nothing is changed by
    // it: the lineup, with the tasks earlier runs left with a session before the others, and those tasks, without one
    // the run would not go on with. `skip` are the labels whose tasks the run leaves out. Null
    // when the project has no tasks to read: its `origin` remote is not a GitHub repository. Throws TrackerException
    // when the tracker refuses.
    public async Task<(TaskLineup Lineup, IReadOnlyList<QueueTask> Recorded)?> ReadQueueAsync(ProjectSettings settings, string? milestone, IReadOnlyList<string> skip, CancellationToken cancellationToken)
    {
        if (await Runs.ReadOpenAsync(settings, milestone, cancellationToken) is not { } open)
            return null;

        var rules = QueueRules.Of(settings) with { Skip = skip };
        List<QueueTask> recorded = [.. Runs.Records().Select(record => TaskLineup.Recorded(open, rules, record.Number).Task).OfType<QueueTask>()];
        return (Enumerable.Reverse(recorded).Aggregate(TaskLineup.Arrange(open, rules), (lineup, task) => lineup.Taking(task)), recorded);
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
            Skill = loaded?.Settings?.Session.Skill is { } skill ? Machine.FindSkill(skill) : null,
            Going = Going()?.Sign.Told,
        };
        if (reading == AssistReading.Nothing)
            return ground;
        if (await Machine.FindAsync(Tool.GitHubCli, cancellationToken) is { Runs: false } gh)
            return ground with { Unread = gh.Unstarted is { } why ? $"the GitHub command-line tool `gh` could not be started: {why}." : "the GitHub command-line tool `gh` is not installed." };

        // A GitHub that did not answer has nothing read, and no reason that a step puts right: the step says so.
        switch (await Machine.CheckTrackerLoginAsync(cancellationToken))
        {
            case TrackerLoginState.LoggedOut:
                return ground with { Unread = "`gh` is not logged in to github.com." };
            case TrackerLoginState.NoAnswer:
                return ground;
        }

        // The boards are those of the user, whatever the repository is; the labels and the milestones are its own.
        if (reading == AssistReading.Boards)
        {
            var read = await Boards.ReadAsync(Repository, cancellationToken);
            if (read.Unanswered)
                return ground;

            var known = read.Boards;
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
    // in, a GitHub that answers, or a GitHub `origin` remote. A demo has its made-up labels.
    public async Task<(IReadOnlyList<string>? Labels, string? Unread)> ReadLabelsAsync(ProjectSettings settings, CancellationToken cancellationToken)
    {
        if (Repository is null)
            return (null, "The `origin` remote is not a GitHub repository, so there are no labels to choose from.");
        if (await Machine.FindAsync(Tool.GitHubCli, cancellationToken) is { Runs: false } gh)
            return (null, $"The GitHub command-line tool `gh` {(gh.Unstarted is { } why ? $"could not be started: {why}" : "is not installed")}, so the labels cannot be read.");
        switch (await Machine.CheckTrackerLoginAsync(cancellationToken))
        {
            case TrackerLoginState.LoggedOut:
                return (null, "`gh` is not logged in to github.com, so the labels cannot be read.");
            case TrackerLoginState.NoAnswer:
                return (null, "GitHub did not answer, so the labels cannot be read.");
        }

        var named = settings.Queue.Labels;
        string[] ofSettings = [.. named.Blocking, .. named.Take, named.Manual, named.Interrupted];
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
