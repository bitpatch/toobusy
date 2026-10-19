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

    // What the last run left for the next one.
    IRunState State { get; }

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
    public IRunState State { get; } = LocalFolder.State(root);

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
        return new LoggedRun(
            new Supervisor(
                Tasks(repository, settings, logged),
                new ClaudeCode(logged.Inside(root), clock, ClaudeFolders.Of(root, local, () => LocalFolder.Make(local), home, variables), root, plan.PermissionMode),
                new GitWorkspace(logged, root),
                State,
                LocalFolder.Sign(root),
                new LocalMachine(logged),
                clock,
                new LoggedCommands(new ShellCommands(root), log),
                plan,
                RunPolicy.Default),
            log);
    }

    // The issues of the repository, with their statuses on the board of the settings.
    static GitHubTasks Tasks(string repository, ProjectSettings settings, IProcessRunner runner) =>
        new(runner, repository, settings.Tracker.Board, settings.Tracker.Statuses);
}
