using TooBusy.Core.Assistant;

namespace TooBusy.Core.Run;

// The assistant that does the tasks, as a run needs it: a session for each task, which is started with a message,
// looked at while it works, told something more, asked to stop, and stopped.
public interface IAssistant
{
    // Starts a session for a task. Throws AssistantException when it does not start.
    Task<IAssistantSession> StartAsync(SessionStart start, CancellationToken cancellationToken);

    // Goes on with the conversation of a session of an earlier run; the message of the start is the next one of
    // it. Null when it cannot be gone on with.
    Task<IAssistantSession?> ResumeAsync(string conversation, SessionStart start, CancellationToken cancellationToken);

    // How much of the usage limits of the assistant is used, as far as it is known.
    UsageLimits ReadLimits();
}

// Task is the number of the task and Name what the session is called among the sessions of the assistant.
public sealed record SessionStart(int Task, string Name, string Message, ModelChoice Model, string Effort);

public sealed class AssistantException(string message) : Exception(message);

public interface IAssistantSession
{
    // How the owner opens the session, as a command: every message about a session names it.
    string Open { get; }

    // The conversation of the session, by which a later run goes on with it; null when it cannot be gone on with.
    string? Conversation { get; }

    Task<SessionLook> LookAsync(CancellationToken cancellationToken);

    // Cuts the turn of the session and goes on with the message as the next one of the same conversation.
    // False when the session did not go on.
    Task<bool> TellAsync(string message, CancellationToken cancellationToken);

    // Lets the session read the message after its next step, cutting nothing.
    Task AskAsync(string message, CancellationToken cancellationToken);

    // Stops the session; its conversation is kept.
    Task StopAsync(CancellationToken cancellationToken);
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
// the plan the session keeps of its work, step by step in its order; null when it keeps none.
public sealed record SessionLook(SessionPhase Phase, string? Asks = null, string? Step = null, int Steps = 0, long Context = 0, string? Reply = null, string? Limit = null, IReadOnlyList<PlanStep>? Plan = null);

// A window of usage, by the word it is known by among the limits of the assistant, as `weekly` is: how much of it
// is used, in percent, and when it starts anew. Null is a time that is not known.
public sealed record UsageWindow(string Name, double Used, DateTimeOffset? ResetsAt);

// The limits of the assistant. Near is the window that starts anew soon enough to wait for it, and a run does;
// Far is the one that does not, and when it is spent a run stops.
public sealed record UsageLimits(UsageWindow? Near, UsageWindow? Far)
{
    public static UsageLimits Unknown { get; } = new(null, null);

    // The limits as they stand at the moment: a window whose reset has passed is not used at all.
    public UsageLimits At(DateTimeOffset now) => new(Fresh(Near, now), Fresh(Far, now));

    static UsageWindow? Fresh(UsageWindow? window, DateTimeOffset now) =>
        window is { ResetsAt: { } reset } && reset <= now ? window with { Used = 0, ResetsAt = null } : window;
}

// The working copy of the project: how many files differ from what is committed, and how many commits of the
// branch are not pushed. Null when it cannot be read.
public interface IWorkspace
{
    Task<WorkspaceState?> ReadAsync(CancellationToken cancellationToken);
}

public sealed record WorkspaceState(int Changes, int Unpushed);

public interface IClock
{
    DateTimeOffset Now { get; }

    Task DelayAsync(TimeSpan time, CancellationToken cancellationToken);
}

// The machine a run goes on: it is kept awake for as long as what is given back is not disposed, and it tells the
// owner what a run wants them to know while they are away from the terminal.
public interface IMachine
{
    IDisposable KeepAwake();

    void Notify(string text);
}

// What a run leaves for the next one: the task a usage limit stopped, with the conversation of its session.
public interface IRunState
{
    PausedTask? LoadPaused();

    void SavePaused(PausedTask task);

    void ClearPaused();
}

public sealed record PausedTask(int Number, string Conversation);

// Where a run tells what happens: a line of its log for everything that happened, every task that is over, once,
// and what is going on right now.
public interface IRunView
{
    void Say(RunLine line);

    void Report(TaskEnd ended);

    void Show(RunStatus status);
}

// A run of the queue. Commands may be sent from any thread; the cancellation is the kill: the session is stopped at
// once, and its task stays as it is.
public interface IQueueRun
{
    Task<RunResult> RunAsync(IRunView view, CancellationToken kill);

    void Send(RunCommand command);
}
