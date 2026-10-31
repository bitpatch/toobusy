using TooBusy.Assistants.ClaudeCode;
using TooBusy.Core.Processes;
using TooBusy.Core.Queue;
using TooBusy.Core.Run;
using TooBusy.Core.Settings;
using TooBusy.Infrastructure.Git;
using TooBusy.Infrastructure.Machine;
using TooBusy.Infrastructure.Processes;
using TooBusy.Infrastructure.Run;
using TooBusy.Trackers.GitHub;

namespace TooBusy.Cli;

// What the runs of a project are made of: the open tasks they read, the record a run leaves for the next one, the
// sign of a run that goes on, and a run itself. `Workbench` chooses them once, when it is opened: those of the
// machine, or the imitated ones of a demo, so that `run`, `queue` and the menu read the same queue and the same record.
public interface IProjectRuns
{
    // The open tasks of the milestone, as a run over these settings reads them before a task; the milestone is null
    // for tasks whatever their milestone. Null when the project has no tasks to read: its `origin` remote is not a
    // GitHub repository. Throws TrackerException when the tracker refuses.
    Task<IReadOnlyList<QueueTask>?> ReadOpenAsync(ProjectSettings settings, string? milestone, CancellationToken cancellationToken);

    // What the last run left for the next one: the record of the working copy, and those of the worktrees of a run of
    // several sessions.
    IReadOnlyList<TaskRecord> Records();

    // The run that goes on in the working copy, by its sign; null when none does.
    GoingRun? Going();

    // A run of the plan over these settings; null when the project has no tasks to take.
    IQueueRun? Open(ProjectSettings settings, RunPlan plan);
}

// The runs of a project on this machine. The tasks are the issues of the repository, which is null when the `origin`
// remote is not a GitHub repository; Claude Code does them in the working copy at the root; and what a run leaves on
// the machine, its record, its sign and its log, is kept in the local folder of the project, where `toobusy status`
// reads it too. Processes runs the commands of the machine, and Home is the home folder of the user, where Claude Code
// keeps its conversations unless `CLAUDE_CONFIG_DIR` among the Variables of the environment puts them elsewhere.
public sealed class ProjectRuns(string root, string? repository, IProcessRunner processes, IClock clock, string home, Func<string, string?> variables) : IProjectRuns
{
    public IReadOnlyList<TaskRecord> Records() => LocalFolder.Records(root);

    public GoingRun? Going() => LocalFolder.Sign(root).Read();

    public async Task<IReadOnlyList<QueueTask>?> ReadOpenAsync(ProjectSettings settings, string? milestone, CancellationToken cancellationToken) =>
        repository is null ? null : await Tasks(repository, settings, processes).ReadOpenAsync(milestone, cancellationToken);

    public IQueueRun? Open(ProjectSettings settings, RunPlan plan)
    {
        if (repository is null)
            return null;

        // The log of the run is written by everything around the run: the commands of the machine and of the
        // project, and the view of the run itself.
        var local = LocalFolder.Ensure(root);
        var log = new RunLogFile(local, clock);
        var logged = new LoggedProcesses(processes, log);

        // A session in a worktree is one of the same account: there is one list of the sessions, and the status line
        // of each writes what it tells of the limits into the local folder of the working copy.
        var sessions = new SessionList(logged, clock);
        var fetching = new Turns();
        var worktrees = new GitWorktrees(logged, root, (folder, landing) => Copy(folder, landing, local, plan, logged, log, sessions, fetching));
        return new LoggedRun(
            new Supervisor(
                Tasks(repository, settings, logged),
                new ClaudeCode(logged.Inside(root), clock, ClaudeFolders.Of(root, local, () => LocalFolder.Make(local), home, variables), root, plan.PermissionMode, sessions),
                new GitWorkspace(logged, root, fetching),
                LocalFolder.State(root),
                LocalFolder.Sign(root),
                new LocalMachine(logged),
                clock,
                new LoggedCommands(new ShellCommands(root), log),
                plan,
                RunPolicy.Default,
                worktrees),
            log);
    }

    // The ports of a worktree: they are those of the working copy, bound to its folder. It keeps a local folder of its
    // own, which is where its changes are.
    WorkingCopy Copy(string folder, Landing landing, string usage, RunPlan plan, LoggedProcesses logged, RunLogFile log, SessionList sessions, Turns fetching)
    {
        var local = LocalFolder.Ensure(folder);
        var folders = ClaudeFolders.Of(folder, local, () => LocalFolder.Make(local), home, variables) with { UsageFolder = usage };
        return new WorkingCopy(
            folder,
            new GitWorkspace(logged, folder, fetching),
            LocalFolder.State(folder),
            new LoggedCommands(new ShellCommands(folder), log),
            new ClaudeCode(logged.Inside(folder), clock, folders, folder, plan.PermissionMode, sessions))
        {
            Landing = landing,
        };
    }

    // The issues of the repository, with their statuses on the board of the settings.
    static GitHubTasks Tasks(string repository, ProjectSettings settings, IProcessRunner runner) =>
        new(runner, repository, settings.Tracker.Board, settings.Tracker.Statuses);
}
