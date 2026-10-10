using System.Globalization;
using TooBusy.Core.Assistant;
using TooBusy.Core.Queue;

namespace TooBusy.Core.Run;

// What a run works on: the milestone, null for tasks whatever their milestone, the rules of the queue, and the model
// and the effort the tasks are done with.
public sealed record RunPlan(string? Milestone, QueueRules Rules, ModelChoice Model, string Effort);

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

    // At this much of a limit, in percent, no task is started; at Spent a limit counts as run into.
    public double Limit { get; init; } = 96;

    public double Spent { get; init; } = 99;

    // How long after the reset of a limit a run goes on, how long it waits for a reset it does not know the time
    // of, and how many times a task waits for a limit at most.
    public TimeSpan ResetMargin { get; init; } = TimeSpan.FromMinutes(1);

    public TimeSpan UnknownLimitWait { get; init; } = TimeSpan.FromMinutes(30);

    public int LimitWaits { get; init; } = 10;

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

// What a run is doing right now. Text says it in words when no task is being done. Since is when the task was
// taken, Step what its session is doing, Context the size of its conversation, Queued how many tasks follow it.
public sealed record RunStatus(RunPhase Phase, string Text)
{
    public QueueTask? Task { get; init; }

    public DateTimeOffset? Since { get; init; }

    public string? Step { get; init; }

    public long Context { get; init; }

    public int Queued { get; init; }

    public DateTimeOffset? Until { get; init; }

    // How the owner opens the session of the task.
    public string? Open { get; init; }

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

public sealed record RunResult(RunEnd End, int Done, string? Problem = null);

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

    public static string Percent(double used) => string.Create(CultureInfo.InvariantCulture, $"{Math.Floor(used):0}%");
}
