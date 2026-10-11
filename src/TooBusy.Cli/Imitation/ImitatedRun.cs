using TooBusy.Core.Queue;
using TooBusy.Core.Run;

namespace TooBusy.Cli.Imitation;

// A run of a demo: the real run over made-up tasks and made-up sessions, with a working tree that is always clean
// and a machine that is left alone. It shows what a run meets: a task that is done, a session that stops with a
// question and is told to go on alone, a task done in part whose rest becomes a task for the owner, a usage limit
// that is waited out, a task that opens after another, and tasks that are held. Nothing is changed anywhere.
public static class ImitatedRun
{
    // A demo does not make anybody wait as a run does.
    public static RunPolicy Pace { get; } = RunPolicy.Default with
    {
        Poll = TimeSpan.FromSeconds(1),
        Tick = TimeSpan.FromMilliseconds(100),
        OwnerWait = TimeSpan.FromSeconds(10),
        AbortGrace = TimeSpan.FromSeconds(15),
        ResetMargin = TimeSpan.FromSeconds(1),
        UnknownLimitWait = TimeSpan.FromSeconds(5),
    };

    // The tracker is the one the demo keeps for the menu and for the runs after this one.
    public static IQueueRun Open(RunPlan plan, IClock clock, ITaskTracker tasks) =>
        new Supervisor(tasks, new ImitatedAssistant(clock), new CleanWorkspace(), new ForgottenState(), new QuietMachine(), clock, plan, Pace);

    sealed class CleanWorkspace : IWorkspace
    {
        public Task<WorkspaceState?> ReadAsync(CancellationToken cancellationToken) => Task.FromResult<WorkspaceState?>(new WorkspaceState(0, 0));
    }

    sealed class ForgottenState : IRunState
    {
        PausedTask? paused;

        public PausedTask? LoadPaused() => paused;

        public void SavePaused(PausedTask task) => paused = task;

        public void ClearPaused() => paused = null;
    }

    sealed class QuietMachine : IMachine
    {
        public IDisposable KeepAwake() => new Nothing();

        public void Notify(string text)
        {
        }

        sealed class Nothing : IDisposable
        {
            public void Dispose()
            {
            }
        }
    }
}

// The tasks of a demo, made up to fit the rules of the project: each has a label that lets it in, one waits for
// another, and, where the rules can hold a task, one is held by a label and one by its status. What a run changes
// is changed here and nowhere else.
public sealed class ImitatedTasks : ITaskTracker
{
    static readonly string[] Types = ["feature", "bug", "chore", "docs"];

    readonly List<QueueTask> open = [];
    readonly Dictionary<int, string> descriptions = [];
    int next = 108;

    public ImitatedTasks(QueueRules rules)
    {
        string Type(int kind) => rules.Take.Count > 0 ? rules.Take[kind % rules.Take.Count] : Types[kind];
        void Add(int number, string title, string description, string[] labels, BoardStatus status = BoardStatus.Todo, int[]? after = null)
        {
            var stands = rules.Board ? status : BoardStatus.Missing;
            open.Add(new QueueTask(number, title, $"https://github.com/example/project/issues/{number}", labels, stands, stands == BoardStatus.InProgress ? "In Progress" : "Todo", after ?? [], []));
            descriptions[number] = description;
        }

        Add(101, "Show the total of an order in its header", "The header of an order shows its number and its date. Add the total, with the currency of the order.", [Type(0)]);
        Add(102, "Export the orders as a file", "The list of the orders gets a button that saves what the list shows as a file.", [Type(0)]);
        Add(103, "Fix the rounding of a discount", "A discount of 12.5% on 9.99 gives 8.74125, and the price is shown as 8.74 in the cart and as 8.75 in the order. Round once, in one place.", [Type(1)]);
        Add(104, "Update the dependencies", "Take the packages to their latest versions and make the tests pass.", [Type(2)]);
        Add(105, "Describe the export in the manual", "A page of the manual on saving the orders as a file.", [Type(3)], after: [102]);
        if (rules.Blocking.Count > 0)
            Add(106, "Move the site to the new hosting", "The contract with the old hosting ends in March.", [Type(2), rules.Blocking[0]]);
        if (rules.Board)
            Add(107, "Add a dark theme", "Follow the setting of the system.", [Type(0)], BoardStatus.InProgress);
    }

    // Whether a task carries the label that a run puts on one it had to stop.
    public bool HasInterrupted(string label) => open.Any(task => task.Has(label));

    public Task<IReadOnlyList<QueueTask>> ReadOpenAsync(string? milestone, CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<QueueTask>>([.. open]);

    public Task<TaskText> ReadTextAsync(int number, CancellationToken cancellationToken) =>
        Task.FromResult(new TaskText(descriptions.GetValueOrDefault(number, ""), []));

    public Task SetStatusAsync(int number, BoardStatus status, CancellationToken cancellationToken)
    {
        Change(number, task => task.Status == BoardStatus.Missing ? task : task with { Status = status, StatusName = status == BoardStatus.InProgress ? "In Progress" : status.ToString() });
        return Task.CompletedTask;
    }

    public Task AddLabelAsync(int number, string label, CancellationToken cancellationToken)
    {
        Change(number, task => task with { Labels = [.. task.Labels, label] });
        return Task.CompletedTask;
    }

    public Task RemoveLabelAsync(int number, string label, CancellationToken cancellationToken)
    {
        Change(number, task => task with { Labels = [.. task.Labels.Where(has => !has.Equals(label, StringComparison.OrdinalIgnoreCase))] });
        return Task.CompletedTask;
    }

    public Task CommentAsync(int number, string text, CancellationToken cancellationToken) => Task.CompletedTask;

    public Task CloseAsync(int number, CancellationToken cancellationToken)
    {
        open.RemoveAll(task => task.Number == number);
        foreach (var waiting in open.ToList())
            Change(waiting.Number, task => task with { BlockedBy = [.. task.BlockedBy.Where(before => before != number)] });
        return Task.CompletedTask;
    }

    public Task<int> CreateAsync(NewTask task, CancellationToken cancellationToken)
    {
        var number = next++;
        var status = open.Any(other => other.Status != BoardStatus.Missing) ? BoardStatus.Todo : BoardStatus.Missing;
        open.Add(new QueueTask(number, task.Title, $"https://github.com/example/project/issues/{number}", task.Labels, status, "Todo", [], []));
        descriptions[number] = task.Description;
        return Task.FromResult(number);
    }

    void Change(int number, Func<QueueTask, QueueTask> change)
    {
        var index = open.FindIndex(task => task.Number == number);
        if (index >= 0)
            open[index] = change(open[index]);
    }
}

// The assistant of a demo. Its sessions are plays written here, one for each made-up task, and they last as long as
// the clock says: a session works through its steps, each a few seconds long, and then ends its turn, asks, or runs
// into a usage limit. What it is told starts its next act. A session with a play keeps a plan of it, a step of the
// plan for each step of the acts it has started: the plan grows when it goes on with the next act.
public sealed class ImitatedAssistant(IClock clock) : IAssistant
{
    static readonly TimeSpan Reset = TimeSpan.FromSeconds(6);

    // A session that has no play of its own: a task that a run takes after those that were made up.
    static readonly Act[] Plain =
    [
        new([(2, "Read README.md"), (3, "Edit src/App.cs"), (2, "Bash: git push")], Ended("The task is done as it is described.\n\n- Checked with the tests: all pass.\n\nTOOBUSY: done")),
    ];

    static readonly Dictionary<int, Act[]> Plays = new()
    {
        [101] =
        [
            new(
                [(2, "Read src/Orders/Order.cs"), (2, "Edit src/Orders/OrderHeader.razor"), (3, "Bash: dotnet test"), (2, "Bash: git push")],
                Ended("The header of an order shows its total with the currency of the order.\n\n- Checked with the tests of the orders: 42 pass.\n- Commit a1b2c3d.\n\nTOOBUSY: done")),
        ],
        [102] =
        [
            new(
                [(2, "Read docs/orders.md"), (2, "Read src/Orders/OrderList.cs")],
                new SessionLook(SessionPhase.Asking, Asks: "input needed", Reply: "The task does not say what kind of file the export is. Should it be CSV or JSON?")),
            new(
                [(2, "Edit src/Export/CsvExport.cs"), (3, "Bash: dotnet test"), (2, "Bash: git push")],
                Ended("The list of the orders has a button that saves it as a CSV file.\n\n- Decided without the owner: CSV, because the manual speaks of spreadsheets.\n- Checked with the tests of the export: 9 pass.\n- Commit b2c3d4e.\n\nTOOBUSY: done")),
        ],
        [103] =
        [
            new(
                [(2, "Read src/Pricing/Discount.cs"), (3, "Edit src/Pricing/Discount.cs"), (3, "Bash: dotnet test"), (2, "Bash: git push")],
                Ended(
                    "A discount is rounded once, to the cent, half up, and the cart and the order show the same price.\n\n- Checked with the tests of the prices: 31 pass.\n- Commit c3d4e5f.\n\n"
                    + "TOOBUSY-REST: Decide what a discount of more than the price does\n"
                    + "A discount may be larger than the price it is taken from, and the price becomes negative. The rounding of #103 is done and does not depend on this.\n\n"
                    + "What is left: say what such a discount does, in `src/Pricing/Discount.cs`, with a test.\n\n"
                    + "- [ ] The price is zero.\n- [ ] The discount is refused when it is entered.\n"
                    + "TOOBUSY: partial")),
        ],
        [104] =
        [
            new(
                [(2, "Bash: dotnet list package --outdated"), (2, "Edit Directory.Packages.props")],
                new SessionLook(SessionPhase.Ended, Limit: "You have hit your usage limit.")),
            new(
                [(3, "Bash: dotnet test"), (2, "Bash: git push")],
                Ended("Seven packages are at their latest versions.\n\n- Checked with all the tests: 214 pass.\n- Commit d4e5f6a.\n\nTOOBUSY: done")),
        ],
        [105] =
        [
            new(
                [(2, "Read src/Export/CsvExport.cs"), (3, "Edit docs/manual/export.md"), (2, "Bash: git push")],
                Ended("The manual has a page on saving the orders as a file.\n\n- Commit e5f6a7b.\n\nTOOBUSY: done")),
        ],
    };

    // What a session does when it is asked to wrap up.
    static readonly Act WrapUp = new(
        [(2, "Bash: git restore --staged --worktree ."), (1, "Bash: git status")],
        Ended("Found out: where the change goes, and that the tests of this part are quick.\n\nUndone: what this session changed; the working copy is as it was.\n\nLeft: the task as it is described.\n\nTOOBUSY: interrupted"));

    readonly IClock clock = clock;
    UsageWindow near = new("5-hour", 41, null);
    double far = 12;

    public Task<IAssistantSession> StartAsync(SessionStart start, CancellationToken cancellationToken)
    {
        near = near with { Used = Math.Min(95, near.Used + 6) };
        far += 1;
        return Task.FromResult<IAssistantSession>(new MadeUpSession(this, start.Task, Plays.GetValueOrDefault(start.Task, Plain)));
    }

    public Task<IAssistantSession?> ResumeAsync(string conversation, SessionStart start, CancellationToken cancellationToken) =>
        Task.FromResult<IAssistantSession?>(null);

    // The near window starts anew a few seconds after a session ran into it.
    public UsageLimits ReadLimits()
    {
        if (near.ResetsAt is { } reset && reset <= clock.Now)
            near = new UsageWindow("5-hour", 3, null);
        return new UsageLimits(near, new UsageWindow("weekly", far, null));
    }

    static SessionLook Ended(string reply) => new(SessionPhase.Ended, Reply: reply);

    // An act of a play: the steps the session makes, each for so many seconds, and how the act ends.
    sealed record Act((int Seconds, string Step)[] Steps, SessionLook End);

    sealed class MadeUpSession(ImitatedAssistant assistant, int task, Act[] acts) : IAssistantSession
    {
        Act act = acts[0];
        int played;
        DateTimeOffset since = assistant.clock.Now;

        // The steps of the acts that are over, and the size of the conversation they left.
        int steps;

        // The plan: whether the act that is played is of the play, and so of the plan, how many steps the plan
        // has, and how many of them the acts that are over have done.
        bool planned = acts != Plain;
        int cells = acts != Plain ? acts[0].Steps.Length : 0;
        int done;

        public string Open => $"claude attach 4f2a{task} (made up)";

        public string? Conversation => null;

        public Task<SessionLook> LookAsync(CancellationToken cancellationToken)
        {
            var passed = (assistant.clock.Now - since).TotalSeconds;
            for (var index = 0; index < act.Steps.Length; index++)
            {
                passed -= act.Steps[index].Seconds;
                if (passed < 0)
                    return Task.FromResult(new SessionLook(SessionPhase.Working, Step: act.Steps[index].Step, Steps: steps + index + 1, Context: Context(steps + index + 1), Plan: Plan(index)));
            }

            // A session that runs into the limit uses the near window up; it starts anew a little later.
            if (act.End.Limit is not null && assistant.near.Used < 100)
                assistant.near = new UsageWindow("5-hour", 100, assistant.clock.Now + Reset);
            return Task.FromResult(act.End with { Steps = steps + act.Steps.Length, Context = Context(steps + act.Steps.Length), Plan = Plan(act.Steps.Length) });
        }

        public Task<bool> TellAsync(string message, CancellationToken cancellationToken)
        {
            if (message == Briefing.WrapUp(task))
                Next(WrapUp, false);
            else if (played + 1 < acts.Length)
                Next(acts[++played], true);
            else
                Next(Plain[0], false);
            return Task.FromResult(true);
        }

        public Task AskAsync(string message, CancellationToken cancellationToken)
        {
            Next(WrapUp, false);
            return Task.CompletedTask;
        }

        public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

        void Next(Act next, bool ofPlay)
        {
            steps += act.Steps.Length;
            if (planned)
                done += act.Steps.Length;
            if (ofPlay)
                cells += next.Steps.Length;
            (act, since, planned) = (next, assistant.clock.Now, ofPlay);
        }

        // The plan when so many steps of the act are made: the step the session is at is the next of them.
        IReadOnlyList<PlanStep>? Plan(int made)
        {
            var at = planned ? done + made : done;
            return cells == 0 ? null : [.. Enumerable.Range(0, cells).Select(step => step < at ? PlanStep.Done : step == at && planned ? PlanStep.Active : PlanStep.Pending)];
        }

        static long Context(int steps) => 18_000 + (steps * 3_400L);
    }
}
