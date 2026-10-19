using System.Globalization;
using TooBusy.Core.Assistant;

namespace TooBusy.Core.Run;

// The assistant that does the tasks, as a run needs it: a session for each task, which is started with a message,
// looked at while it works, told something more, asked to stop, and stopped.
public interface IAssistant
{
    // Starts a session for a task. Throws AssistantException when it does not start.
    Task<IAssistantSession> StartAsync(SessionStart start, CancellationToken cancellationToken);

    // The session of an earlier run, by what was kept of it: it is looked at, told and stopped as one that was
    // started here. Null when nothing is left of it: no session that lives, and no conversation to go on with.
    Task<IAssistantSession?> FindAsync(SessionTrace trace, CancellationToken cancellationToken);

    // The session that a start which was cut off may have started all the same: the one that has the name of the start
    // and was started since the moment. Null when there is none, and when the sessions cannot be read.
    Task<IAssistantSession?> FindStartedAsync(SessionStart start, DateTimeOffset since, CancellationToken cancellationToken);

    // How much of the usage limits of the assistant is used, as far as it is known.
    UsageLimits ReadLimits();
}

// Task is the number of the task and Name what the session is called among the sessions of the assistant.
public sealed record SessionStart(int Task, string Name, string Message, ModelChoice Model, string Effort);

// What is kept of a session for a later run. Id is what the assistant knows the session by while it lives, and
// Conversation what it is gone on with when it does not; either is null while it is not known. Since is when the
// session was started or told something last: what it said before that does not count.
public sealed record SessionTrace(string? Id, string? Conversation, DateTimeOffset Since);

public sealed class AssistantException(string message) : Exception(message);

public interface IAssistantSession
{
    // How the owner opens the session, as a command: every message about a session names it.
    string Open { get; }

    // What a later run finds the session by. It changes as the session does: its conversation is learnt after
    // its start, and a session that goes on may be known by another name.
    SessionTrace Trace { get; }

    Task<SessionLook> LookAsync(CancellationToken cancellationToken);

    // Cuts the turn of the session and goes on with the message as the next one of the same conversation. Says
    // why when the session did not go on, and whether it goes on as a copy.
    Task<Telling> TellAsync(string message, CancellationToken cancellationToken);

    // Lets the session read the message after its next step, cutting nothing. A message that cannot be left where
    // the session reads it, a file of the machine, throws LocalStateException.
    Task AskAsync(string message, CancellationToken cancellationToken);

    // Stops the session; its conversation is kept.
    Task StopAsync(CancellationToken cancellationToken);
}

// How a session took a message. Why is null when it went on, and otherwise says why it did not, in words for the
// log. AsCopy tells that the assistant went on with a copy of the conversation, a new session that has all of it
// and the message: Open and Trace name the copy from then on.
public sealed record Telling(string? Why = null, bool AsCopy = false)
{
    public bool Went => Why is null;

    public static Telling WentOn { get; } = new();

    public static Telling Copied { get; } = new(AsCopy: true);
}

public enum SessionPhase
{
    Working,

    // The session waits for somebody to answer it.
    Asking,

    // The session ended its turn.
    Ended,

    // The session is gone, or ended in a way that is not the end of a turn.
    Lost,

    // The sessions of the assistant cannot be read, so nothing is known of this one.
    Unseen,
}

// A step of the plan a session keeps of its work, as far as it is.
public enum PlanStep
{
    Pending,

    // The session is at it.
    Active,
    Done,
}

// A look at a session. Asks is what a session that waits wants; Step is what it is doing; Steps counts the steps
// it has made on its own since the run started it; Context is the size of its conversation in tokens; Reply is the
// last thing it said; Limit is what it was refused with when it ran into a usage limit, and null otherwise. Plan is
// the plan the session keeps of its work, step by step in its order; null when it keeps none. Reported tells that the
// session has reported: the reply is the last thing it did, and it ends with a line for toobusy, as `Outcome.Ends`
// reads it, in every phase: the run reads how a task went from no other reply. Odd is what the assistant cannot make
// of how the session stands, in words for the screen and the log, as a state it does not know; null when there is
// nothing.
public sealed record SessionLook(SessionPhase Phase, string? Asks = null, string? Step = null, int Steps = 0, long Context = 0, string? Reply = null, string? Limit = null, IReadOnlyList<PlanStep>? Plan = null, bool Reported = false, string? Odd = null);

// A window of usage, by the word it is known by among the limits of the assistant, as `weekly` is: how much of it
// is used, in percent, and when it starts anew. Null is a time that is not known.
public sealed record UsageWindow(string Name, double Used, DateTimeOffset? ResetsAt);

// The limits of the assistant. Near is the window that starts anew soon enough to wait for it, and a run does;
// Far is the one that does not, and when it is spent a run stops.
public sealed record UsageLimits(UsageWindow? Near, UsageWindow? Far)
{
    public static UsageLimits Unknown { get; } = new(null, null);

    // The limits as they stand at the moment: a window whose reset has passed started anew since it was told of, and
    // how much of it is used now is not known.
    public UsageLimits At(DateTimeOffset now) => new(Fresh(Near, now), Fresh(Far, now));

    static UsageWindow? Fresh(UsageWindow? window, DateTimeOffset now) =>
        window is { ResetsAt: { } reset } && reset <= now ? null : window;
}

// The working copy of the project: how many files differ from what is committed, how many commits of the branch are
// not pushed, and which branch is checked out. Null when it cannot be read.
public interface IWorkspace
{
    Task<WorkspaceState?> ReadAsync(CancellationToken cancellationToken);

    // Brings the branch that is checked out up to date with its upstream: what the remote has is fetched, and the
    // branch is moved forward to it when it can simply be moved. A branch that cannot is left as it is. Only a
    // cancellation throws.
    Task<BranchSync> SyncAsync(CancellationToken cancellationToken);
}

// Branch is the name of the branch that is checked out; null when none is, as on a detached HEAD.
public sealed record WorkspaceState(int Changes, int Unpushed)
{
    public string? Branch { get; init; }
}

public enum SyncStatus
{
    // The branch has what its upstream has, and nothing more: nothing was moved.
    Level,

    // The branch was moved forward to its upstream.
    Moved,

    // The branch follows no upstream, so it has nothing to be brought up to date with.
    NoUpstream,

    // The branch has commits that its upstream lacks, and lacks none of its: it is not moved.
    Ahead,

    // Each has commits that the other lacks: the branch cannot simply be moved.
    Parted,

    // What the remote has could not be fetched: it did not answer, or refused. MayPass tells the two apart.
    Unreachable,

    // Something else went wrong: the branch could not be read, its upstream is gone, or git refused to move it.
    Failed,
}

// How the branch came out of its sync. Upstream is what it follows, as `origin/develop`; Ahead and Behind count the
// commits of the branch that its upstream lacks and those of the upstream that it lacked, once the remote was fetched.
// Said is what git said of a failure, in a line. MayPass says of a remote that could not be fetched that the failure
// may pass by itself, as when the remote did not answer in time or the network was away: another sync may reach it.
public sealed record BranchSync(SyncStatus Status, string? Upstream = null, int Ahead = 0, int Behind = 0, string Said = "")
{
    public bool MayPass { get; init; }
}

public interface IClock
{
    DateTimeOffset Now { get; }

    Task DelayAsync(TimeSpan time, CancellationToken cancellationToken);
}

// The commands of the project a run runs around a task. A command is a line for the shell of the machine: it runs at
// the root of the working copy, with what it is told in its environment beside what toobusy has in its own, and is
// waited for no longer than the time. How it went is told by the result; only a cancellation throws, and it stops the
// command with everything it started.
public interface IProjectCommands
{
    Task<CommandEnd> RunAsync(string command, IReadOnlyDictionary<string, string> environment, TimeSpan time, CancellationToken cancellationToken);
}

public enum CommandStatus
{
    Exited,

    // The shell could not be run.
    NotStarted,

    // The command did not end in its time and was stopped.
    TimedOut,
}

// ExitCode is that of a command that exited. Printed is what the command wrote, to its output and to where its
// errors go, as far as it came.
public sealed record CommandEnd(CommandStatus Status, int ExitCode, string Printed);

// The machine a run goes on: it is kept awake for as long as what is given back is not disposed, and it tells the
// owner what a run wants them to know while they are away from the terminal.
public interface IMachine
{
    IDisposable KeepAwake();

    void Notify(string text);
}

// A file a run keeps on the machine could not be written or taken away: the record, the sign, the request of a
// session. The message says which, in words for the user, with what the system said. A run does not go on without
// them: it stops the session of its task and stops.
public sealed class LocalStateException(string message, Exception? cause = null) : Exception(message, cause);

// What a run leaves for the next one: the record of the task it has a session for. It is written as soon as the
// session is started and forgotten when the task comes to an outcome, so whatever stops a run in between, the next
// one finds the task and its session. A record that cannot be read is none; one that cannot be written or forgotten
// throws LocalStateException.
public interface IRunState
{
    TaskRecord? Load();

    void Save(TaskRecord record);

    void Clear();
}

// How the session of a record was left, as far as the run knows.
public enum RecordState
{
    // The session was started and nothing more is known: it works, or something stopped it or the run.
    Working,

    // A usage limit stopped the session, and the run stopped it for the next one.
    Paused,

    // The session wrapped the task up after `/abort`: its changes are undone and its report is in the task.
    WrappedUp,
}

public sealed record TaskRecord(int Number, SessionTrace Session, RecordState State = RecordState.Working);

// The sign a run puts up in the working copy for as long as it goes on, so that whoever works beside it can tell: a
// second run, which does not start, and `toobusy status`. Unlike the record it does not outlive its run: it is taken
// down when the run ends, and the sign of a run that died says nothing. A sign that cannot be written or taken down
// throws LocalStateException.
public interface IRunSign
{
    // Puts the sign up. When another run goes on in the working copy, nothing is put up and its sign is given: of two
    // runs that put theirs up at the same moment, one is given the sign of the other.
    RunSign? Raise(RunSign sign);

    // Says anew what the run is doing.
    void Change(RunSign sign);

    void Lower();
}

// What a run says of itself on its sign: where it is, the task it works on, with its title and its address, null
// between tasks, how the session of the task is opened, null while it has none, and until when the run waits for a
// usage limit.
public sealed record RunSign(RunPhase Phase)
{
    public int? Task { get; init; }

    public string? Title { get; init; }

    public string? Url { get; init; }

    public string? Open { get; init; }

    public DateTimeOffset? Until { get; init; }

    // What is said of a run on its sign, of all it shows: a sign changes when one of these does, not with every step
    // of a session.
    public static RunSign Of(RunStatus status) => new(status.Phase)
    {
        Task = status.Task?.Number,
        Title = status.Task?.Title,
        Url = status.Task?.Url,
        Open = status.Task is null ? null : status.Open,
        Until = status.Phase == RunPhase.WaitingForLimit ? status.Until : null,
    };

    // The run in a few words, as `toobusy status` and a run that does not start say it: the task and how its session
    // is opened, with what the run waits for, or that it is between tasks.
    public string Told
    {
        get
        {
            if (Task is not { } number)
                return Phase == RunPhase.WaitingForLimit ? "it waits for a usage limit" : "between tasks";

            var waits = Phase switch
            {
                RunPhase.WaitingForLimit when Until is { } until => $" · waits for a usage limit until {Spoken.Moment(until)}",
                RunPhase.WaitingForLimit => " · waits for a usage limit",
                RunPhase.WaitingForOwner => " · its session waits for the owner",
                _ => "",
            };
            return string.Create(CultureInfo.InvariantCulture, $"#{number} {Title}{waits}{(Open is null ? "" : $" · {Open}")}");
        }
    }
}

// Where a run tells what happens: a line of its log for everything that happened, every task that is over, once,
// and what is going on right now. Trace is what only the log on disk keeps, as how the run sees its session as it
// changes: a view that has no such log does nothing with it.
public interface IRunView
{
    void Say(RunLine line);

    void Report(TaskEnd ended);

    void Show(RunStatus status);

    void Trace(string text)
    {
    }
}

// A run of the queue. It is made once: a second RunAsync throws InvalidOperationException, and a second run is a new
// one. Commands may be sent from any thread; the cancellation is the kill: the session is stopped at once, and its
// task stays as it is. A task whose session was being started goes back to Todo, and one whose outcome or pause was
// being told is told to its end.
public interface IQueueRun
{
    Task<RunResult> RunAsync(IRunView view, CancellationToken kill);

    void Send(RunCommand command);
}
