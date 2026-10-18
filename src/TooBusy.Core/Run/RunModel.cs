using System.Globalization;
using TooBusy.Core.Assistant;
using TooBusy.Core.Queue;
using TooBusy.Core.Settings;

namespace TooBusy.Core.Run;

// What a run works on: the milestone, null for tasks whatever their milestone, the rules of the queue, with the
// labels the run was told to skip, and the model and the effort the tasks are done with. Board is the address of the board of the project, null where it has none:
// a run needs it only to tell a session where a new task stands. Skill is the skill of the project a session starts
// with, null where the project names none. Only is the number of the one task the run does, and Most how many tasks
// it takes at most; null is a run of the whole queue. A run is given one of the two at most. Commands are the
// commands of the project the run runs around a task. Branch is the branch the tasks are done on, null where the
// project names none: the working copy is checked to be on it and brought up to date with its upstream.
// PermissionMode is the permission mode of the assistant its sessions work in, the one of the settings or the one
// a session is started in when they name none.
public sealed record RunPlan(string? Milestone, QueueRules Rules, ModelChoice Model, string Effort)
{
    public string? Board { get; init; }

    public string? Skill { get; init; }

    public int? Only { get; init; }

    public int? Most { get; init; }

    public TaskCommands Commands { get; init; } = TaskCommands.None;

    public string? Branch { get; init; }

    public string PermissionMode { get; init; } = ClaudeCodeOptions.DefaultPermissionMode;
}

// What a session is told of the tracker to make a new task there: the manual label, the labels a task needs
// one of to be taken, the milestone the user works on, null for none, and the address of the board, null in a
// project without one. Statuses are what that board calls the statuses a run knows.
public sealed record NewTasks(string Manual, IReadOnlyList<string> Take, string? Milestone, string? Board)
{
    public BoardStatuses Statuses { get; init; } = BoardStatuses.Default;

    public static NewTasks Of(RunPlan plan) => new(plan.Rules.Manual, plan.Rules.Take, plan.Milestone, plan.Rules.Board ? plan.Board : null) { Statuses = plan.Rules.Statuses };
}

// The times and the counts of a run.
public sealed record RunPolicy
{
    // How often a session is looked at, and how often a run looks whether it was told something.
    public TimeSpan Poll { get; init; } = TimeSpan.FromSeconds(3);

    public TimeSpan Tick { get; init; } = TimeSpan.FromMilliseconds(200);

    // How long a session that waits for the owner is left before it is told to go on alone; how many times a task
    // is told so at most, and after how many in a row that brought no step of its own it is left to the owner.
    public TimeSpan OwnerWait { get; init; } = TimeSpan.FromSeconds(90);

    public int Messages { get; init; } = 10;

    public int IdleMessages { get; init; } = 2;

    // How long a session has to wrap up after `/abort` before its turn is cut.
    public TimeSpan AbortGrace { get; init; } = TimeSpan.FromMinutes(10);

    // At this much of a limit, in percent, it counts as run into. No limit holds a task back before a session runs
    // into it.
    public double Spent { get; init; } = 99;

    // How long after the reset of a limit a run goes on, how long it waits for a reset it does not know the time
    // of, and how many times a task waits for a limit at most.
    public TimeSpan ResetMargin { get; init; } = TimeSpan.FromMinutes(1);

    public TimeSpan UnknownLimitWait { get; init; } = TimeSpan.FromMinutes(30);

    public int LimitWaits { get; init; } = 10;

    // How long a command of the project may run before it is given up, and how long the one after a task may when
    // the run was killed and its owner waits for it to end.
    public TimeSpan CommandTime { get; init; } = TimeSpan.FromMinutes(10);

    public TimeSpan KilledCommandTime { get; init; } = TimeSpan.FromSeconds(30);

    // The pauses before a call of the tracker that did not answer is made again, and before a fetch of the branch
    // that the remote did not answer is: one try more after each, and the failure of the last try stands.
    public IReadOnlyList<TimeSpan> RetryPauses { get; init; } = [TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(30)];

    public static RunPolicy Default { get; } = new();
}

public enum RunCommand
{
    // Finish the current task, then stop.
    Stop,

    // Take `Stop` back.
    Continue,

    // Stop as soon as possible: nothing committed, a report in the task.
    Abort,

    // Tell the session that waits for the owner to go on alone, now.
    Nudge,

    // Tell it nothing: it waits for the owner.
    Hold,
}

[Flags]
public enum RunCommands
{
    None = 0,
    Stop = 1,
    Continue = 2,
    Abort = 4,
    Nudge = 8,
    Hold = 16,
}

public enum RunMark
{
    // The first line of a run.
    Head,

    // Something said under the line before it.
    Note,
    Started,
    Done,
    Partial,
    Owner,
    Interrupted,
    Failed,

    // Something the owner may want to look at.
    Attention,
    GoesOn,
    Paused,
}

// A line of the log of a run.
public sealed record RunLine(RunMark Mark, string Text)
{
    public string Symbol => Mark switch
    {
        RunMark.Head => "✻",
        RunMark.Note => " ",
        RunMark.Started => "→",
        RunMark.Done => "✔",
        RunMark.Partial => "◐",
        RunMark.Owner => "◇",
        RunMark.Interrupted => "■",
        RunMark.Failed => "✖",
        RunMark.Attention => "▲",
        RunMark.GoesOn => "▶",
        _ => "‖",
    };

    public override string ToString() => $"{Symbol} {Text}";
}

public enum RunPhase
{
    // Between tasks: the working tree is checked, the queue is read, a session is started.
    Preparing,
    Working,

    // The session waits for the owner; Until is when it is told to go on alone, null when it is not told.
    WaitingForOwner,

    // The run waits for a usage limit to reset, until Until.
    WaitingForLimit,
    Ended,
}

// A task that is over for this run: the mark of how it went, as a line of the log has it, and how long it took.
public sealed record TaskEnd(QueueTask Task, RunMark Mark, TimeSpan Took);

// What a run is doing right now. Text says it in words when no task is being done. Since is when the task was
// taken, Step what its session is doing, Context the size of its conversation, Queued how many tasks follow it.
// Reply is what a session that waits for the owner said last, line by line; it is empty while nothing waits.
// Plan is the plan the session keeps of the task, step by step; it is empty when it keeps none.
public sealed record RunStatus(RunPhase Phase, string Text)
{
    public QueueTask? Task { get; init; }

    public DateTimeOffset? Since { get; init; }

    public string? Step { get; init; }

    public long Context { get; init; }

    public IReadOnlyList<PlanStep> Plan { get; init; } = [];

    public int Queued { get; init; }

    public DateTimeOffset? Until { get; init; }

    // How the owner opens the session of the task.
    public string? Open { get; init; }

    public IReadOnlyList<string> Reply { get; init; } = [];

    public UsageLimits Limits { get; init; } = UsageLimits.Unknown;

    // The commands that mean something now.
    public RunCommands Available { get; init; }

    public bool Stopping { get; init; }

    public bool Aborting { get; init; }
}

public enum RunEnd
{
    // No task is left.
    Emptied,

    // The owner stopped the run.
    Stopped,

    // A limit of usage is spent, or a task was paused by one.
    Limited,

    // Something happened that a run does not go on past.
    Problem,

    // The run was killed while it worked.
    Killed,
}

// How a run ended. Why says what ended a run that neither ran out of tasks nor was stopped by a command of the
// owner: the limit, the problem, what a kill left behind, or the task or the number of tasks the run was started
// for. A task that was done in part is counted apart from those that are done.
public sealed record RunResult(RunEnd End, int Done, string? Why = null)
{
    public int Partial { get; init; }
}

// Times as a run says them.
public static class Spoken
{
    // A length of time in the units that matter: `45 s`, `90 s`, `12 min`, `1 h 05 min`.
    public static string Time(TimeSpan time)
    {
        time = time < TimeSpan.Zero ? TimeSpan.Zero : time;
        return time.TotalMinutes < 2 ? string.Create(CultureInfo.InvariantCulture, $"{(int)Math.Ceiling(time.TotalSeconds)} s")
            : time.TotalHours < 1 ? string.Create(CultureInfo.InvariantCulture, $"{(int)time.TotalMinutes} min")
            : string.Create(CultureInfo.InvariantCulture, $"{(int)time.TotalHours} h {time.Minutes:00} min");
    }

    public static string Tasks(int count) => count == 1 ? "1 task" : string.Create(CultureInfo.InvariantCulture, $"{count} tasks");

    public static string Commits(int count) => count == 1 ? "1 commit" : string.Create(CultureInfo.InvariantCulture, $"{count} commits");

    // A task as it is named wherever it is shown: its number, which finds it in the tracker, and its title.
    public static string Task(QueueTask task) => string.Create(CultureInfo.InvariantCulture, $"#{task.Number} {task.Title}");

    public static string Percent(double used) => string.Create(CultureInfo.InvariantCulture, $"{Math.Floor(used):0}%");
}
