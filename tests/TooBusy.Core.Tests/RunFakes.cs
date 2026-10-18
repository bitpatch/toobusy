using TooBusy.Core.Queue;
using TooBusy.Core.Run;

namespace TooBusy.Core.Tests;

// A clock that moves only when somebody waits: a delay takes no time of the test, and what is planned for a moment
// happens when the clock passes it. A run that never ends is stopped after two days of its own time.
sealed class FakeClock : IClock
{
    public static readonly DateTimeOffset Start = new(2030, 1, 1, 9, 0, 0, TimeSpan.Zero);

    readonly List<(DateTimeOffset At, Action Do)> planned = [];

    public DateTimeOffset Now { get; private set; } = Start;

    // Plans something for the moment that is this long after the start.
    public void At(TimeSpan after, Action action) => planned.Add((Start + after, action));

    public Task DelayAsync(TimeSpan time, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Now += time;
        Assert.True(Now - Start < TimeSpan.FromDays(2), "The run did not end in two days of its own time.");
        foreach (var due in planned.Where(plan => plan.At <= Now).OrderBy(plan => plan.At).ToList())
        {
            planned.Remove(due);
            due.Do();
        }

        cancellationToken.ThrowIfCancellationRequested();
        return Task.CompletedTask;
    }
}

// The tasks of a project in memory. What a run changes is changed here, so that the next reading of the queue shows
// it, and is remembered in the order it was done.
sealed class FakeTracker : ITaskTracker
{
    public List<QueueTask> Open { get; } = [];

    // What was done, as `status 12 InProgress`, `assign 12`, `label 12 +manual`, `comment 12`, `close 12`.
    public List<string> Did { get; } = [];

    public List<(int Number, string Text)> Comments { get; } = [];

    public List<string?> Read { get; } = [];

    // The tasks that are closed, and the numbers of those that were asked for as closed ones.
    public List<QueueTask> Closed { get; } = [];

    public List<int> Looked { get; } = [];

    // What the tracker refuses, by the first word of what is done: `status`, `assign`, `close`, `read`, or `status 12 Done`.
    public HashSet<string> Refused { get; } = [];

    // What the tracker does not answer, as GitHub that is away for a moment, by the first word of what is done or by
    // the whole of it as Refused, with how many times in a row. Such a failure may pass, and says how many there were.
    public Dictionary<string, int> Unanswered { get; } = [];

    public int Silences { get; private set; }

    // Everything that was asked, answered or not, in its order: a call that is made again is here once for each try.
    public List<string> Asked { get; } = [];

    // What happens as something is done, before it is: a kill that comes in the middle of it, for one. A call whose
    // token the kill cancels is cut then, and neither asked nor done.
    public Action<string>? Doing { get; set; }

    // A tracker that says a task is closed and goes on listing it among the open ones.
    public bool ForgetsClosing { get; set; }

    public QueueTask Add(int number, string[]? labels = null, BoardStatus status = BoardStatus.Todo, int[]? blockedBy = null, string? title = null, string? milestone = null)
    {
        var task = new QueueTask(number, title ?? $"Task {number}", $"https://example.com/{number}", labels ?? ["feature"], status, status.ToString(), blockedBy ?? [], [], milestone);
        Open.Add(task);
        return task;
    }

    public Task<IReadOnlyList<QueueTask>> ReadOpenAsync(string? milestone, CancellationToken cancellationToken)
    {
        Read.Add(milestone);
        Do("read", cancellationToken);
        return Task.FromResult<IReadOnlyList<QueueTask>>([.. Open]);
    }

    public Task<QueueTask?> ReadClosedAsync(int number, CancellationToken cancellationToken)
    {
        Looked.Add(number);
        cancellationToken.ThrowIfCancellationRequested();
        Ask($"look {number}");
        if (Refused.Contains("look"))
            throw new TrackerException($"The tracker refused: look {number}.");
        return Task.FromResult(Closed.Find(task => task.Number == number));
    }

    // Closes a task as something else than the run does: a commit of its session, or its owner.
    public void Shut(int number)
    {
        Closed.AddRange(Open.Where(task => task.Number == number));
        Open.RemoveAll(task => task.Number == number);
    }

    public Task SetStatusAsync(int number, BoardStatus status, CancellationToken cancellationToken)
    {
        Do($"status {number} {status}", cancellationToken);
        Change(number, task => task with { Status = status, StatusName = status.ToString() });
        return Task.CompletedTask;
    }

    public Task AssignAsync(int number, CancellationToken cancellationToken)
    {
        Do($"assign {number}", cancellationToken);
        return Task.CompletedTask;
    }

    public Task AddLabelAsync(int number, string label, CancellationToken cancellationToken)
    {
        Do($"label {number} +{label}", cancellationToken);
        Change(number, task => task with { Labels = [.. task.Labels, label] });
        return Task.CompletedTask;
    }

    public Task RemoveLabelAsync(int number, string label, CancellationToken cancellationToken)
    {
        Do($"label {number} -{label}", cancellationToken);
        Change(number, task => task with { Labels = [.. task.Labels.Where(has => has != label)] });
        return Task.CompletedTask;
    }

    public Task CommentAsync(int number, string text, CancellationToken cancellationToken)
    {
        Do($"comment {number}", cancellationToken);
        Comments.Add((number, text));
        return Task.CompletedTask;
    }

    public Task CloseAsync(int number, CancellationToken cancellationToken)
    {
        Do($"close {number}", cancellationToken);
        if (ForgetsClosing)
            return Task.CompletedTask;

        Shut(number);
        foreach (var task in Open.ToList())
            Change(task.Number, waiting => waiting with { BlockedBy = [.. waiting.BlockedBy.Where(blocker => blocker != number)] });
        return Task.CompletedTask;
    }

    void Do(string what, CancellationToken cancellationToken)
    {
        Doing?.Invoke(what);
        cancellationToken.ThrowIfCancellationRequested();
        Ask(what);
        if (Refused.Contains(what) || Refused.Contains(what.Split(' ')[0]))
            throw new TrackerException($"The tracker refused: {what}.");
        if (what != "read")
            Did.Add(what);
    }

    void Ask(string what)
    {
        Asked.Add(what);
        foreach (var key in (string[])[what, what.Split(' ')[0]])
        {
            if (Unanswered.TryGetValue(key, out var left) && left > 0)
            {
                Unanswered[key] = left - 1;
                throw new TrackerException($"The tracker did not answer: {what} ({++Silences}).", mayPass: true);
            }
        }
    }

    void Change(int number, Func<QueueTask, QueueTask> change)
    {
        foreach (var tasks in (List<QueueTask>[])[Open, Closed])
        {
            var index = tasks.FindIndex(task => task.Number == number);
            if (index >= 0)
                tasks[index] = change(tasks[index]);
        }
    }
}

// An assistant whose sessions are written by the test: what each look at a session shows, act by act.
sealed class FakeAssistant : IAssistant
{
    readonly Dictionary<int, FakeSession> sessions = [];

    public List<SessionStart> Started { get; } = [];

    // The sessions of earlier runs that were asked for, each by what was kept of it.
    public List<SessionTrace> Found { get; } = [];

    public UsageLimits Limits { get; set; } = UsageLimits.Unknown;

    public string? RefuseToStart { get; set; }

    // What happens as a session is started, before it is: a kill that comes in the middle of it, for one. A start
    // whose token the kill cancels is cut then.
    public Action<SessionStart>? Starting { get; set; }

    // Whether a start that was cut off started its session all the same: it is found by the name of the start then.
    public bool StartsAnyway { get; set; }

    // The starts that were cut off and looked for, each with the moment its session was looked for since.
    public List<(SessionStart Start, DateTimeOffset Since)> LookedFor { get; } = [];

    // An assistant that has nothing left of the sessions of earlier runs.
    public bool Gone { get; set; }

    // The session of a task; one that is not written works for a look and ends with the task done.
    public FakeSession Session(int task)
    {
        if (!sessions.TryGetValue(task, out var session))
            sessions.Add(task, session = new FakeSession(task));
        return session;
    }

    public Task<IAssistantSession> StartAsync(SessionStart start, CancellationToken cancellationToken)
    {
        if (RefuseToStart is not null)
            throw new AssistantException(RefuseToStart);

        Starting?.Invoke(start);
        cancellationToken.ThrowIfCancellationRequested();
        Started.Add(start);
        return Task.FromResult<IAssistantSession>(Session(start.Task).Begin());
    }

    // A session of an earlier run is the one of the task its conversation is named after, `conversation-5`.
    public Task<IAssistantSession?> FindAsync(SessionTrace trace, CancellationToken cancellationToken)
    {
        Found.Add(trace);
        return Task.FromResult<IAssistantSession?>(Gone ? null : Session(int.Parse(trace.Conversation!.Split('-')[^1], System.Globalization.CultureInfo.InvariantCulture)).Begin());
    }

    public Task<IAssistantSession?> FindStartedAsync(SessionStart start, DateTimeOffset since, CancellationToken cancellationToken)
    {
        LookedFor.Add((start, since));
        return Task.FromResult<IAssistantSession?>(StartsAnyway ? Session(start.Task).Begin() : null);
    }

    public UsageLimits ReadLimits() => Limits;
}

// A session as a play: an act is what the looks at it show one after another, the last of them for as long as the
// act lasts. Telling the session something starts the next act; asking it something starts the act written for that.
sealed class FakeSession(int task) : IAssistantSession
{
    readonly List<List<(SessionLook Look, Action? Do)>> acts = [[]];
    int? onAsk;
    int act;
    int at;
    int steps;

    // What the session is known by: another name once it went on as a copy.
    public string Id { get; private set; } = $"fake-{task}";

    public string Open => $"fake attach {Id["fake-".Length..]}";

    public string? Conversation { get; set; } = $"conversation-{task}";

    public SessionTrace Trace => new(Id, Conversation, FakeClock.Start);

    public List<string> Told { get; } = [];

    public List<string> Asked { get; } = [];

    public int Stopped { get; private set; }

    // What happens as the session is stopped, before it is: a kill that comes in the middle of it, for one. A stop
    // whose token the kill cancels is cut then, and not counted.
    public Action? Stopping { get; set; }

    // Why the session does not go on when it is told something; null for one that goes on.
    public string? Deaf { get; set; }

    // A session that goes on as a copy the next time it is told something: it is known by another name from then
    // on, with a conversation of its own.
    public bool Copies { get; set; }

    public int Looks { get; private set; }

    public FakeSession Works(int looks = 1, string step = "Editing a file", Action? and = null, PlanStep[]? plan = null)
    {
        for (var index = 0; index < looks; index++)
            Add(new SessionLook(SessionPhase.Working, Step: step, Steps: ++steps, Context: 1000 * steps, Plan: plan), and);
        return this;
    }

    // A session that the assistant marks as waiting for an answer; it has reported when its reply ends with the line
    // for toobusy, as a conversation that the assistant reads says.
    public FakeSession Asks(string? what = "input needed", string? reply = "Which of the two?") =>
        Add(new SessionLook(SessionPhase.Asking, Asks: what, Steps: steps, Reply: reply, Reported: Outcome.Ends(reply)));

    public FakeSession Ends(string reply) => Add(new SessionLook(SessionPhase.Ended, Steps: steps, Reply: reply, Reported: Outcome.Ends(reply)));

    public FakeSession Limit(string refusal = "You have hit your usage limit.", SessionPhase phase = SessionPhase.Ended, string? reply = null) =>
        Add(new SessionLook(phase, Steps: steps, Reply: reply, Limit: refusal, Reported: Outcome.Ends(reply)));

    public FakeSession Lost(string? reply = null) => Add(new SessionLook(SessionPhase.Lost, Steps: steps, Reply: reply));

    public FakeSession Unseen(int looks = 1, Action? and = null)
    {
        for (var index = 0; index < looks; index++)
            Add(new SessionLook(SessionPhase.Unseen), and);
        return this;
    }

    // What follows is shown after the session is told something.
    public FakeSession Then()
    {
        acts.Add([]);
        return this;
    }

    // What follows is shown after the session is asked something.
    public FakeSession WhenAsked()
    {
        acts.Add([]);
        onAsk = acts.Count - 1;
        return this;
    }

    public FakeSession Begin()
    {
        if (acts[0].Count == 0)
            Works().Ends("Did it.\nTOOBUSY: done");
        (act, at) = (0, 0);
        return this;
    }

    public Task<SessionLook> LookAsync(CancellationToken cancellationToken)
    {
        Looks++;
        var (look, and) = acts[act][Math.Min(at++, acts[act].Count - 1)];
        and?.Invoke();
        return Task.FromResult(look);
    }

    public Task<Telling> TellAsync(string message, CancellationToken cancellationToken)
    {
        Told.Add(message);
        if (Deaf is { } why)
            return Task.FromResult(new Telling(why));

        var copied = Copies;
        if (copied)
        {
            Id = $"fake-{task}-copy";
            Conversation = $"copy-{task}";
            Copies = false;
        }

        // The acts written for a question that is asked are not those of a session that is told.
        var next = act + 1;
        if (next < acts.Count && next != onAsk)
            (act, at) = (next, 0);
        return Task.FromResult(copied ? Telling.Copied : Telling.WentOn);
    }

    public Task AskAsync(string message, CancellationToken cancellationToken)
    {
        Asked.Add(message);
        if (onAsk is { } asked)
            (act, at) = (asked, 0);
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        Stopping?.Invoke();
        cancellationToken.ThrowIfCancellationRequested();
        Stopped++;
        return Task.CompletedTask;
    }

    FakeSession Add(SessionLook look, Action? and = null)
    {
        acts[^1].Add((look, and));
        return this;
    }
}

// The working copy as the test says it is: the states that are waiting are given one after another, and after them
// the one that stands. A sync of its branch comes out as the test says, in the same way, and is counted.
sealed class FakeWorkspace : IWorkspace
{
    public WorkspaceState? State { get; set; } = new(0, 0);

    public Queue<WorkspaceState?> Next { get; } = new();

    public int Read { get; private set; }

    public BranchSync Sync { get; set; } = new(SyncStatus.Level, "origin/develop");

    public Queue<BranchSync> Syncs { get; } = new();

    public int Synced { get; private set; }

    // What happens when the branch is brought up to date, for a test that looks at what was done before it.
    public Action? Syncing { get; set; }

    // What a reading of the working copy throws, as a bug would; null while it reads.
    public Exception? Failure { get; set; }

    public Task<WorkspaceState?> ReadAsync(CancellationToken cancellationToken)
    {
        Read++;
        if (Failure is not null)
            throw Failure;
        return Task.FromResult(Next.Count > 0 ? Next.Dequeue() : State);
    }

    public Task<BranchSync> SyncAsync(CancellationToken cancellationToken)
    {
        Synced++;
        Syncing?.Invoke();
        return Task.FromResult(Syncs.Count > 0 ? Syncs.Dequeue() : Sync);
    }
}

// The shell of the machine: every command passes, but for those the test gives another end. A command takes as long
// of the clock as the test says, and what is planned for that time happens while it runs.
sealed class FakeCommands(FakeClock clock) : IProjectCommands
{
    // The commands that were run, each with the number of the task it was told and the time it was given.
    public List<(string Command, string Task, TimeSpan Time)> Ran { get; } = [];

    public List<IReadOnlyDictionary<string, string>> Told { get; } = [];

    public Dictionary<string, CommandEnd> Ends { get; } = [];

    public TimeSpan Takes { get; set; } = TimeSpan.FromSeconds(2);

    // Something that happens as a command starts.
    public Action<string>? Running { get; set; }

    public async Task<CommandEnd> RunAsync(string command, IReadOnlyDictionary<string, string> environment, TimeSpan time, CancellationToken cancellationToken)
    {
        Ran.Add((command, environment["TOOBUSY_TASK"], time));
        Told.Add(environment);
        Running?.Invoke(command);
        await clock.DelayAsync(Takes, cancellationToken);
        return Ends.GetValueOrDefault(command, new CommandEnd(CommandStatus.Exited, 0, ""));
    }
}

sealed class FakeMachine : IMachine
{
    public bool Awake { get; private set; }

    public int Kept { get; private set; }

    public List<string> Notified { get; } = [];

    public IDisposable KeepAwake()
    {
        Kept++;
        Awake = true;
        return new Release(this);
    }

    public void Notify(string text) => Notified.Add(text);

    sealed class Release(FakeMachine machine) : IDisposable
    {
        public void Dispose() => machine.Awake = false;
    }
}

sealed class FakeState : IRunState
{
    public TaskRecord? Record { get; set; }

    // Every record that was written, in its order; null is a record that was forgotten.
    public List<TaskRecord?> Written { get; } = [];

    // What a record that is written, and one that is forgotten, fails with, as a full disk; null while it works.
    public LocalStateException? Unwritable { get; set; }

    public LocalStateException? Unclearable { get; set; }

    public TaskRecord? Load() => Record;

    public void Save(TaskRecord record)
    {
        if (Unwritable is not null)
            throw Unwritable;
        Written.Add(Record = record);
    }

    public void Clear()
    {
        if (Unclearable is not null)
            throw Unclearable;
        if (Record is not null)
            Written.Add(Record = null);
    }
}

// What a run said and showed.
// The sign of a run: what stood on it, in its order, null where it was taken down. `Other` is the sign of another
// run that goes on.
sealed class FakeSign : IRunSign
{
    public RunSign? Other { get; set; }

    public List<RunSign?> Written { get; } = [];

    // What putting the sign up, saying anew what it says and taking it down fail with; null while they work. A
    // change that fails is counted.
    public LocalStateException? Unraisable { get; set; }

    public LocalStateException? Unwritable { get; set; }

    public LocalStateException? Unlowerable { get; set; }

    public int Refused { get; private set; }

    public RunSign? Raise(RunSign sign)
    {
        if (Other is not null)
            return Other;
        if (Unraisable is not null)
            throw Unraisable;

        Written.Add(sign);
        return null;
    }

    public void Change(RunSign sign)
    {
        if (Unwritable is not null)
        {
            Refused++;
            throw Unwritable;
        }

        Written.Add(sign);
    }

    public void Lower()
    {
        if (Unlowerable is not null)
            throw Unlowerable;
        Written.Add(null);
    }
}

sealed class Log : IRunView
{
    public List<string> Lines { get; } = [];

    public List<RunStatus> Statuses { get; } = [];

    // The tasks that were told to be over, each as its number, its mark and the seconds it took.
    public List<(int Task, RunMark Mark, int Seconds)> Ends { get; } = [];

    public string Text => string.Join('\n', Lines);

    // The lines as they were said, with what a screen does not show of them.
    public List<RunLine> Said { get; } = [];

    public void Say(RunLine line)
    {
        Lines.Add(line.ToString());
        Said.Add(line);
    }

    public void Report(TaskEnd ended) => Ends.Add((ended.Task.Number, ended.Mark, (int)ended.Took.TotalSeconds));

    public void Show(RunStatus status) => Statuses.Add(status);
}
