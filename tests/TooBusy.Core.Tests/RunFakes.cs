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

    public Dictionary<int, string> Descriptions { get; } = [];

    // What was done, as `status 12 InProgress`, `label 12 +manual`, `comment 12`, `close 12`, `create 31`.
    public List<string> Did { get; } = [];

    public List<(int Number, string Text)> Comments { get; } = [];

    public List<NewTask> Made { get; } = [];

    public List<string?> Read { get; } = [];

    // What the tracker refuses, by the first word of what is done: `status`, `close`, `read`, or `status 12 Done`.
    public HashSet<string> Refused { get; } = [];

    public int Next { get; set; } = 100;

    // A tracker that says a task is closed and goes on listing it among the open ones.
    public bool ForgetsClosing { get; set; }

    public QueueTask Add(int number, string[]? labels = null, BoardStatus status = BoardStatus.Todo, int[]? blockedBy = null, string? title = null)
    {
        var task = new QueueTask(number, title ?? $"Task {number}", $"https://example.com/{number}", labels ?? ["feature"], status, status.ToString(), blockedBy ?? [], []);
        Open.Add(task);
        return task;
    }

    public Task<IReadOnlyList<QueueTask>> ReadOpenAsync(string? milestone, CancellationToken cancellationToken)
    {
        Read.Add(milestone);
        Do("read");
        return Task.FromResult<IReadOnlyList<QueueTask>>([.. Open]);
    }

    public Task<string> ReadDescriptionAsync(int number, CancellationToken cancellationToken)
    {
        Do($"description {number}", remember: false);
        return Task.FromResult(Descriptions.GetValueOrDefault(number) ?? $"Do task {number}.");
    }

    public Task SetStatusAsync(int number, BoardStatus status, CancellationToken cancellationToken)
    {
        Do($"status {number} {status}");
        Change(number, task => task with { Status = status, StatusName = status.ToString() });
        return Task.CompletedTask;
    }

    public Task AddLabelAsync(int number, string label, CancellationToken cancellationToken)
    {
        Do($"label {number} +{label}");
        Change(number, task => task with { Labels = [.. task.Labels, label] });
        return Task.CompletedTask;
    }

    public Task RemoveLabelAsync(int number, string label, CancellationToken cancellationToken)
    {
        Do($"label {number} -{label}");
        Change(number, task => task with { Labels = [.. task.Labels.Where(has => has != label)] });
        return Task.CompletedTask;
    }

    public Task CommentAsync(int number, string text, CancellationToken cancellationToken)
    {
        Do($"comment {number}");
        Comments.Add((number, text));
        return Task.CompletedTask;
    }

    public Task CloseAsync(int number, CancellationToken cancellationToken)
    {
        Do($"close {number}");
        if (ForgetsClosing)
            return Task.CompletedTask;

        Open.RemoveAll(task => task.Number == number);
        foreach (var task in Open.ToList())
            Change(task.Number, waiting => waiting with { BlockedBy = [.. waiting.BlockedBy.Where(blocker => blocker != number)] });
        return Task.CompletedTask;
    }

    public Task<int> CreateAsync(NewTask task, CancellationToken cancellationToken)
    {
        var number = Next++;
        Do($"create {number}");
        Made.Add(task);
        Open.Add(new QueueTask(number, task.Title, $"https://example.com/{number}", task.Labels, BoardStatus.Todo, "Todo", [], []));
        return Task.FromResult(number);
    }

    void Do(string what, bool remember = true)
    {
        if (Refused.Contains(what) || Refused.Contains(what.Split(' ')[0]))
            throw new TrackerException($"The tracker refused: {what}.");
        if (remember && what != "read")
            Did.Add(what);
    }

    void Change(int number, Func<QueueTask, QueueTask> change)
    {
        var index = Open.FindIndex(task => task.Number == number);
        if (index >= 0)
            Open[index] = change(Open[index]);
    }
}

// An assistant whose sessions are written by the test: what each look at a session shows, act by act.
sealed class FakeAssistant : IAssistant
{
    readonly Dictionary<int, FakeSession> sessions = [];

    public List<SessionStart> Started { get; } = [];

    public List<(string Conversation, SessionStart Start)> Resumed { get; } = [];

    public UsageLimits Limits { get; set; } = UsageLimits.Unknown;

    public string? RefuseToStart { get; set; }

    public bool RefuseToResume { get; set; }

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

        Started.Add(start);
        return Task.FromResult<IAssistantSession>(Session(start.Task).Begin());
    }

    public Task<IAssistantSession?> ResumeAsync(string conversation, SessionStart start, CancellationToken cancellationToken)
    {
        Resumed.Add((conversation, start));
        return Task.FromResult<IAssistantSession?>(RefuseToResume ? null : Session(start.Task).Begin());
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

    public string Open => $"fake attach {task}";

    public string? Conversation { get; set; } = $"conversation-{task}";

    public List<string> Told { get; } = [];

    public List<string> Asked { get; } = [];

    public int Stopped { get; private set; }

    // A session that does not go on when it is told something.
    public bool Deaf { get; set; }

    public int Looks { get; private set; }

    public FakeSession Works(int looks = 1, string step = "Editing a file", Action? and = null, PlanStep[]? plan = null)
    {
        for (var index = 0; index < looks; index++)
            Add(new SessionLook(SessionPhase.Working, Step: step, Steps: ++steps, Context: 1000 * steps, Plan: plan), and);
        return this;
    }

    public FakeSession Asks(string? what = "input needed", string? reply = "Which of the two?") =>
        Add(new SessionLook(SessionPhase.Asking, Asks: what, Steps: steps, Reply: reply));

    public FakeSession Ends(string reply) => Add(new SessionLook(SessionPhase.Ended, Steps: steps, Reply: reply));

    public FakeSession Limit(string refusal = "You have hit your usage limit.") => Add(new SessionLook(SessionPhase.Ended, Steps: steps, Limit: refusal));

    public FakeSession Lost(string? reply = null) => Add(new SessionLook(SessionPhase.Lost, Steps: steps, Reply: reply));

    public FakeSession Unseen(int looks = 1)
    {
        for (var index = 0; index < looks; index++)
            Add(new SessionLook(SessionPhase.Unseen));
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

    public Task<bool> TellAsync(string message, CancellationToken cancellationToken)
    {
        Told.Add(message);
        if (Deaf)
            return Task.FromResult(false);

        // The acts written for a question that is asked are not those of a session that is told.
        var next = act + 1;
        if (next < acts.Count && next != onAsk)
            (act, at) = (next, 0);
        return Task.FromResult(true);
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
// the one that stands.
sealed class FakeWorkspace : IWorkspace
{
    public WorkspaceState? State { get; set; } = new(0, 0);

    public Queue<WorkspaceState?> Next { get; } = new();

    public int Read { get; private set; }

    public Task<WorkspaceState?> ReadAsync(CancellationToken cancellationToken)
    {
        Read++;
        return Task.FromResult(Next.Count > 0 ? Next.Dequeue() : State);
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
    public PausedTask? Paused { get; set; }

    public PausedTask? LoadPaused() => Paused;

    public void SavePaused(PausedTask task) => Paused = task;

    public void ClearPaused() => Paused = null;
}

// What a run said and showed.
sealed class Log : IRunView
{
    public List<string> Lines { get; } = [];

    public List<RunStatus> Statuses { get; } = [];

    // The tasks that were told to be over, each as its number, its mark and the seconds it took.
    public List<(int Task, RunMark Mark, int Seconds)> Ends { get; } = [];

    public string Text => string.Join('\n', Lines);

    public void Say(RunLine line) => Lines.Add(line.ToString());

    public void Report(TaskEnd ended) => Ends.Add((ended.Task.Number, ended.Mark, (int)ended.Took.TotalSeconds));

    public void Show(RunStatus status) => Statuses.Add(status);
}
