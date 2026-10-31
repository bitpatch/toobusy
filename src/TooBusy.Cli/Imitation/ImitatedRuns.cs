using System.Globalization;
using TooBusy.Core.Queue;
using TooBusy.Core.Run;
using TooBusy.Core.Settings;
using TooBusy.Infrastructure.Run;

namespace TooBusy.Cli.Imitation;

// The runs of a demo: the real run over made-up tasks and made-up sessions, with a working tree that is always clean
// and a machine that is left alone. Each shows what a run meets: a task whose session a kill stopped and which goes
// on with it, a task that is done, a session that stops with a question and is told to go on alone, a task done in
// part whose session makes a task of what is left and is marked as waiting after its report, a usage limit that is
// waited out, a task that opens after another, and tasks that are held; the others are done plainly. Nothing is
// changed anywhere. The made-up tasks and the record are kept in memory for as long as the demo goes on, and nowhere
// else: the menu and the next run see a task that an abort left interrupted. The menu counts the tasks and takes a
// moment over it, as a tracker does, so that the count can be looked at, unless nobody watches. A demo of several
// sessions has made-up worktrees, which keep their records in memory too.
public sealed class ImitatedRuns(IClock clock, bool watched) : IProjectRuns
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

    static readonly TimeSpan Wait = TimeSpan.FromSeconds(1);

    readonly UnwrittenState remembered = new();
    readonly ImitatedWorktrees worktrees = new();
    ImitatedTasks? kept;

    public IReadOnlyList<TaskRecord> Records() => [.. new[] { remembered.Load() }.OfType<TaskRecord>(), .. worktrees.Records];

    // A demo looks at no sign, as it puts none up.
    public GoingRun? Going() => null;

    public async Task<IReadOnlyList<QueueTask>?> ReadOpenAsync(ProjectSettings settings, string? milestone, CancellationToken cancellationToken)
    {
        if (watched)
            await clock.DelayAsync(Wait, cancellationToken);
        return await Tasks(QueueRules.Of(settings)).ReadOpenAsync(milestone, cancellationToken);
    }

    // A demo runs no command of the project and checks no branch, whatever the settings say, and puts up no sign: it
    // works on nothing, so it goes on beside a run, and a run beside it. A demo of a run of one task has no task that
    // a run was killed over, unless it is the one: the record of that task would keep the run from any other.
    public IQueueRun Open(ProjectSettings settings, RunPlan plan)
    {
        var tasks = Tasks(plan.Rules, killed: plan.Only is null or ImitatedTasks.Killed);
        var assistant = new ImitatedAssistant(clock, tasks);
        return new Supervisor(tasks, assistant, new CleanWorkspace(), remembered, new NoSign(), new QuietMachine(), clock, new NoCommands(), plan with { Commands = TaskCommands.None, Branch = null }, Pace, worktrees.Over(assistant));
    }

    // The made-up tasks: those that were kept while one of them is interrupted or has a record, and otherwise tasks
    // that are made up anew, so that a demo can be run again and again. The first of them have a task that a run was
    // killed over, with its record, so that a demo starts by going on with a session. Settings that were changed in
    // the demo so that the kept tasks no longer fit them start it over: the tasks are made up anew for them, and the
    // record of the old ones is forgotten.
    ImitatedTasks Tasks(QueueRules rules, bool killed = true)
    {
        if (kept is not null && !kept.Fits(rules))
        {
            kept = null;
            remembered.Clear();
            worktrees.Clear();
        }

        if (kept is null && !killed)
        {
            kept = new ImitatedTasks(rules, killed: false);
        }
        else if (kept is null)
        {
            kept = new ImitatedTasks(rules, killed: true);
            remembered.Save(new TaskRecord(ImitatedTasks.Killed, ImitatedAssistant.Trace(ImitatedTasks.Killed, clock.Now)));
        }
        else if (remembered.Load() is null && worktrees.Records.Count == 0 && !kept.HasInterrupted(rules.Labels.Interrupted))
        {
            kept = new ImitatedTasks(rules, killed: false);
        }

        return kept;
    }

    internal sealed class NoCommands : IProjectCommands
    {
        public Task<CommandEnd> RunAsync(string command, IReadOnlyDictionary<string, string> environment, TimeSpan time, CancellationToken cancellationToken) =>
            Task.FromResult(new CommandEnd(CommandStatus.NotStarted, 0, ""));
    }

    sealed class NoSign : IRunSign
    {
        public RunSign? Raise(RunSign sign) => null;

        public void Change(RunSign sign)
        {
        }

        public void Lower()
        {
        }
    }

    internal sealed class CleanWorkspace : IWorkspace
    {
        public Task<WorkspaceState?> ReadAsync(CancellationToken cancellationToken) => Task.FromResult<WorkspaceState?>(new WorkspaceState(0, 0) { Branch = DemoProject.Branch });

        public Task<BranchSync> SyncAsync(CancellationToken cancellationToken) => Task.FromResult(new BranchSync(SyncStatus.Level));
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

// The worktrees of a demo: folders that are not there, always clean, each with its record in memory for as long as the
// demo goes on, so that the next run finds what a kill or an abort left. Over is them for the sessions of one run.
public sealed class ImitatedWorktrees
{
    readonly Lock gate = new();
    readonly SortedDictionary<int, UnwrittenState> states = [];

    public IReadOnlyList<TaskRecord> Records
    {
        get
        {
            lock (gate)
                return [.. states.Values.Select(state => state.Load()).OfType<TaskRecord>()];
        }
    }

    public void Clear()
    {
        lock (gate)
            states.Clear();
    }

    public IWorkingCopies Over(IAssistant assistant) => new Copies(this, assistant);

    sealed class Copies(ImitatedWorktrees worktrees, IAssistant assistant) : IWorkingCopies
    {
        static readonly Upstream Remote = new("origin", DemoProject.Branch);

        public Task<IReadOnlyList<WorkingCopy>> FindAsync(CancellationToken cancellationToken)
        {
            lock (worktrees.gate)
                return Task.FromResult<IReadOnlyList<WorkingCopy>>([.. worktrees.states.Select(left => Copy(left.Key, left.Value))]);
        }

        public Task<Upstream?> FollowedAsync(string branch, CancellationToken cancellationToken) => Task.FromResult<Upstream?>(Remote);

        public Task<WorkingCopy> MakeAsync(int task, Upstream onto, CancellationToken cancellationToken)
        {
            lock (worktrees.gate)
                return Task.FromResult(Copy(task, worktrees.states[task] = new UnwrittenState()));
        }

        public Task RemoveAsync(WorkingCopy copy, CancellationToken cancellationToken)
        {
            lock (worktrees.gate)
                worktrees.states.Remove(int.Parse(copy.Folder.Split('/')[^1], CultureInfo.InvariantCulture));
            return Task.CompletedTask;
        }

        WorkingCopy Copy(int task, UnwrittenState state) =>
            new(string.Create(CultureInfo.InvariantCulture, $".toobusy/local/worktrees/{task}"), new ImitatedRuns.CleanWorkspace(), state, new ImitatedRuns.NoCommands(), assistant)
            {
                Landing = new Landing(string.Create(CultureInfo.InvariantCulture, $"toobusy/{task}"), Remote),
            };
    }
}

// The record of a demo: it is remembered for as long as the demo goes on, and nowhere.
public sealed class UnwrittenState : IRunState
{
    TaskRecord? record;

    public TaskRecord? Load() => record;

    public void Save(TaskRecord record) => this.record = record;

    public void Clear() => record = null;
}

// The tasks of a demo, the work of a small online shop, made up to fit the rules of the project: each has a label
// that lets it in, one waits for another, one is held for the owner, and, where the rules can hold a task, one is held
// by a label and one by its status. What a run changes is changed here and nowhere else. The first tasks of a demo
// have one more: a task that a run was killed over, which stands as one that is worked on and has a record.
public sealed class ImitatedTasks : ITaskTracker
{
    // The task a run was killed over.
    public const int Killed = 100;

    static readonly string[] Types = ["feature", "bug", "chore", "docs"];

    // The sessions of a demo of several work side by side.
    readonly Lock gate = new();
    readonly List<QueueTask> open = [];
    readonly QueueRules made;
    readonly string manual;
    readonly BoardStatuses statuses;

    public ImitatedTasks(QueueRules rules, bool killed)
    {
        made = rules;
        manual = rules.Labels.Manual;
        statuses = rules.Statuses;
        string Type(int kind) => rules.Labels.Take.Count > 0 ? rules.Labels.Take[kind % rules.Labels.Take.Count] : Types[kind];
        void Add(int number, string title, string[] labels, BoardStatus status = BoardStatus.Todo, int[]? after = null)
        {
            var stands = rules.Board ? status : BoardStatus.Missing;
            open.Add(new QueueTask(number, title, Address(number), labels, stands, statuses.NameOf(stands == BoardStatus.InProgress ? stands : BoardStatus.Todo), after ?? [], []));
        }

        if (killed)
            Add(Killed, "Rename the settings page", [Type(2)], BoardStatus.InProgress);
        Add(101, "Show the total of an order in its header", [Type(0)]);
        Add(102, "Export the orders as a CSV file", [Type(0)]);
        Add(103, "Fix the rounding of a discount", [Type(1)]);
        Add(104, "Update the dependencies", [Type(2)]);
        Add(105, "Describe the export in the manual", [Type(3)], after: [102]);
        Add(106, "Let a customer save a cart for later", [Type(0)]);
        Add(107, "Send an email when an order ships", [Type(0)]);
        Add(108, "Find products whatever accents the search has", [Type(1)]);
        Add(109, "Show the stock of a product on its page", [Type(0)]);
        Add(110, "Add a page of frequently asked questions", [Type(3)]);
        Add(111, "Load the list of orders page by page", [Type(2)]);
        Add(112, "Choose the payment provider", [Type(2), manual]);
        if (rules.Labels.Blocking.Count > 0)
            Add(113, "Redesign the checkout page", [Type(0), rules.Labels.Blocking[^1]]);
        if (rules.Board)
            Add(114, "Add a dark theme", [Type(0)], BoardStatus.InProgress);
    }

    // What a made-up session does when it makes a task of what is left of its own: the new task has the manual
    // label and the labels of the first one, and stands as that one does. It is made once.
    public void Leave(int number, string title, int from)
    {
        lock (gate)
        {
            if (open.Find(task => task.Number == from) is { } first && open.TrueForAll(task => task.Number != number))
                open.Add(first with { Number = number, Title = title, Url = Address(number), Labels = [manual, .. first.Labels], Status = first.Status == BoardStatus.Missing ? BoardStatus.Missing : BoardStatus.Todo, StatusName = statuses.Todo, BlockedBy = [], Parts = [] });
        }
    }

    // Whether a task carries the label that a run puts on one it had to stop.
    public bool HasInterrupted(string label)
    {
        lock (gate)
            return open.Any(task => task.Has(label));
    }

    // Whether the tasks were made up for rules such as these: the same labels, the same board and the same statuses
    // on it. The labels a run skips do not make other tasks.
    public bool Fits(QueueRules rules) =>
        rules.Board == made.Board
        && rules.Statuses == made.Statuses
        && rules.Labels.Manual == made.Labels.Manual
        && rules.Labels.Interrupted == made.Labels.Interrupted
        && rules.Labels.Blocking.SequenceEqual(made.Labels.Blocking)
        && rules.Labels.Take.SequenceEqual(made.Labels.Take);

    public Task<IReadOnlyList<QueueTask>> ReadOpenAsync(string? milestone, CancellationToken cancellationToken)
    {
        lock (gate)
            return Task.FromResult<IReadOnlyList<QueueTask>>([.. open]);
    }

    // No commit of a made-up session closes its task.
    public Task<QueueTask?> ReadClosedAsync(int number, CancellationToken cancellationToken) => Task.FromResult<QueueTask?>(null);

    public Task SetStatusAsync(int number, BoardStatus status, CancellationToken cancellationToken)
    {
        Change(number, task => task.Status == BoardStatus.Missing ? task : task with { Status = status, StatusName = statuses.NameOf(status) });
        return Task.CompletedTask;
    }

    public Task AssignAsync(int number, CancellationToken cancellationToken) => Task.CompletedTask;

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
        lock (gate)
        {
            open.RemoveAll(task => task.Number == number);
            foreach (var waiting in open.ToList())
                Change(waiting.Number, task => task with { BlockedBy = [.. task.BlockedBy.Where(before => before != number)] });
        }

        return Task.CompletedTask;
    }

    static string Address(int number) => DemoProject.Issue(number);

    void Change(int number, Func<QueueTask, QueueTask> change)
    {
        lock (gate)
        {
            var index = open.FindIndex(task => task.Number == number);
            if (index >= 0)
                open[index] = change(open[index]);
        }
    }
}

// The assistant of a demo. Its sessions are plays written here, one for each made-up task, and they last as long as
// the clock says: a session works through its steps, each a few seconds long, and then ends its turn, asks, or runs
// into a usage limit. What it is told starts its next act. A session makes a new task as a real one does, by itself:
// here in the made-up tracker. A session with a play keeps a plan of it, a step of the
// plan for each step of the acts it has started: the plan grows when it goes on with the next act. A session of an
// earlier run is found stopped, as a kill left it, and plays its play when it is told to go on.
public sealed class ImitatedAssistant(IClock clock, ImitatedTasks tasks) : IAssistant
{
    static readonly TimeSpan Reset = TimeSpan.FromSeconds(6);

    // A session that has no play of its own: a task that a run takes after those that were made up.
    static readonly Act[] Plain =
    [
        new([(2, "Read README.md"), (3, "Edit src/App.cs"), (2, "Bash: git push")], Ended("The task is done as it is described.\n\n- Checked with the tests: all pass.\n\nTOOBUSY: done")),
    ];

    static readonly Dictionary<int, Act[]> Plays = new()
    {
        [ImitatedTasks.Killed] =
        [
            new(
                [(2, "Edit src/Settings/SettingsPage.razor"), (2, "Bash: dotnet test"), (2, "Bash: git push")],
                Ended("The page is called Settings in the menu, in its title and in the manual.\n\n- Went on after toobusy was stopped: the menu was renamed already, the title and the manual were left.\n- Checked with the tests of the pages: 18 pass.\n- Commit f6a7b8c.\n\nTOOBUSY: done")),
        ],
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
                new SessionLook(SessionPhase.Asking, Asks: "input needed", Reply: "The task does not say which orders the file has. All of them, or those the list shows with its filters?")),
            new(
                [(2, "Edit src/Export/CsvExport.cs"), (3, "Bash: dotnet test"), (2, "Bash: git push")],
                Ended("The list of the orders has a button that saves the orders it shows as a CSV file.\n\n- Decided without the owner: the orders the list shows with its filters, as the button stands over the list.\n- Checked with the tests of the export: 9 pass.\n- Commit b2c3d4e.\n\nTOOBUSY: done")),
        ],
        [103] =
        [
            new(
                [(2, "Read src/Pricing/Discount.cs"), (3, "Edit src/Pricing/Discount.cs"), (3, "Bash: dotnet test"), (2, "Bash: git push")],
                Reported(Rounded))
            {
                Leaves = (115, "Decide what a discount of more than the price does"),
            },
            new([], Reported(Rounded)),
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
                Ended("The manual has a page on saving the orders as a CSV file.\n\n- Commit e5f6a7b.\n\nTOOBUSY: done")),
        ],
        [106] = Plainly("Edit src/Cart/SavedCarts.cs", "A cart can be saved for later from its page and is found again under Saved carts.\n\n- Checked with the tests of the cart: 27 pass.\n- Commit a7b8c9d."),
        [107] = Plainly("Edit src/Mail/OrderShipped.cs", "The customer gets an email with the tracking number when an order is marked as shipped.\n\n- Checked with the tests of the mail: 14 pass.\n- Commit b8c9d0e."),
        [108] = Plainly("Edit src/Search/ProductSearch.cs", "The search compares the names of the products without their accents: “creme” finds “Crème brûlée”.\n\n- Checked with the tests of the search: 19 pass.\n- Commit c9d0e1f."),
        [109] = Plainly("Edit src/Products/ProductPage.razor", "The page of a product says how many are in stock, and “Out of stock” when none are.\n\n- Checked with the tests of the pages: 21 pass.\n- Commit d0e1f2a."),
        [110] =
        [
            new(
                [(2, "Edit docs/faq.md"), (1, "Bash: git push")],
                Ended("The site has a page of frequently asked questions, on delivery, returns and payment, linked from its footer.\n\n- Commit e1f2a3b.\n\nTOOBUSY: done")),
        ],
        [111] = Plainly("Edit src/Orders/OrderList.cs", "The list of the orders loads fifty of them at a time, and the next fifty as it is scrolled.\n\n- Checked with the tests of the orders: 44 pass.\n- Commit f2a3b4c."),
    };

    // The play of a task that is done plainly: a change, the tests and the push, and the report.
    static Act[] Plainly(string change, string report) =>
    [
        new([(1, change), (2, "Bash: dotnet test"), (1, "Bash: git push")], Ended($"{report}\n\nTOOBUSY: done")),
    ];

    // What a session does when it is asked to wrap up.
    static readonly Act WrapUp = new(
        [(2, "Bash: git restore --staged --worktree ."), (1, "Bash: git status")],
        Ended("Found out: where the change goes, and that the tests of this part are quick.\n\nUndone: what this session changed; the working copy is as it was.\n\nLeft: the task as it is described.\n\nTOOBUSY: interrupted"));

    readonly IClock clock = clock;
    readonly ImitatedTasks tasks = tasks;
    readonly Lock gate = new();
    UsageWindow near = new("5-hour", 41, null);
    double far = 12;

    public Task<IAssistantSession> StartAsync(SessionStart start, CancellationToken cancellationToken)
    {
        lock (gate)
        {
            near = near with { Used = Math.Min(95, near.Used + 6) };
            far += 1;
        }

        return Task.FromResult<IAssistantSession>(new MadeUpSession(this, start.Task, Plays.GetValueOrDefault(start.Task, Plain)));
    }

    // The session of a record is known by the number of its task, which its made-up name ends with.
    public Task<IAssistantSession?> FindAsync(SessionTrace trace, CancellationToken cancellationToken) =>
        Task.FromResult<IAssistantSession?>(trace.Id is { } id && id.StartsWith(Name, StringComparison.Ordinal) && int.TryParse(id[Name.Length..], out var task)
            ? new MadeUpSession(this, task, Plays.GetValueOrDefault(task, Plain), earlier: true)
            : null);

    // A made-up session starts at once, so no start is cut off with a session left behind it.
    public Task<IAssistantSession?> FindStartedAsync(SessionStart start, DateTimeOffset since, CancellationToken cancellationToken) =>
        Task.FromResult<IAssistantSession?>(null);

    // The near window starts anew a few seconds after a session ran into it.
    public UsageLimits ReadLimits()
    {
        lock (gate)
        {
            if (near.ResetsAt is { } reset && reset <= clock.Now)
                near = new UsageWindow("5-hour", 3, null);
            return new UsageLimits(near, new UsageWindow("weekly", far, null));
        }
    }

    // A session that runs into the limit uses the near window up; it starts anew a little later.
    void Spend()
    {
        lock (gate)
        {
            if (near.Used < 100)
                near = new UsageWindow("5-hour", 100, clock.Now + Reset);
        }
    }

    static SessionLook Ended(string reply) => new(SessionPhase.Ended, Reply: reply, Reported: Outcome.Ends(reply));

    // A session that ended its turn with the line for toobusy and that Claude Code marks as waiting all the same, as
    // it marks a report that leaves something to the owner. Told that no answer will come, it answers with the same.
    static SessionLook Reported(string reply) => new(SessionPhase.Asking, Asks: "input needed", Reply: reply, Reported: Outcome.Ends(reply));

    // The report of a task done in part.
    const string Rounded =
        "A discount is rounded once, to the cent, half up, and the cart and the order show the same price.\n\n- Checked with the tests of the prices: 31 pass.\n- Commit c3d4e5f.\n\n"
        + "Left for the owner: a discount may be larger than the price it is taken from, and the price becomes negative. What such a discount does is theirs to say. "
        + "I made #115 “Decide what a discount of more than the price does” of it, with the manual label and the two answers I see: the price is zero, or the discount is refused when it is entered.\n\n"
        + "TOOBUSY: partial";

    // What the name of a made-up session starts with.
    const string Name = "4f2a";

    // A session that a kill stopped: it does nothing until it is told to go on.
    static readonly Act Stopped = new([], new SessionLook(SessionPhase.Lost));

    // What is kept of the session of the task a run was killed over.
    public static SessionTrace Trace(int task, DateTimeOffset since) => new($"{Name}{task}", null, since);

    // An act of a play: the steps the session makes, each for so many seconds, and how the act ends.
    // Leaves is the task the session makes of what is left as the act ends.
    sealed record Act((int Seconds, string Step)[] Steps, SessionLook End)
    {
        public (int Number, string Title)? Leaves { get; init; }
    }

    sealed class MadeUpSession(ImitatedAssistant assistant, int task, Act[] acts, bool earlier = false) : IAssistantSession
    {
        readonly DateTimeOffset started = assistant.clock.Now;
        Act act = earlier ? Stopped : acts[0];
        int played = earlier ? -1 : 0;
        DateTimeOffset since = assistant.clock.Now;

        // The steps of the acts that are over, and the size of the conversation they left.
        int steps;

        // The plan: whether the act that is played is of the play, and so of the plan, how many steps the plan
        // has, and how many of them the acts that are over have done.
        bool planned = acts != Plain && !earlier;
        int cells = acts != Plain && !earlier ? acts[0].Steps.Length : 0;
        int done;

        public string Open => $"claude attach {Name}{task} (made up)";

        public SessionTrace Trace => ImitatedAssistant.Trace(task, started);

        public Task<SessionLook> LookAsync(CancellationToken cancellationToken)
        {
            var passed = (assistant.clock.Now - since).TotalSeconds;
            for (var index = 0; index < act.Steps.Length; index++)
            {
                passed -= act.Steps[index].Seconds;
                if (passed < 0)
                    return Task.FromResult(new SessionLook(SessionPhase.Working, Step: act.Steps[index].Step, Steps: steps + index + 1, Context: Context(steps + index + 1), Plan: Plan(index)));
            }

            if (act.Leaves is var (number, title))
                assistant.tasks.Leave(number, title, task);

            if (act.End.Limit is not null)
                assistant.Spend();
            return Task.FromResult(act.End with { Steps = steps + act.Steps.Length, Context = Context(steps + act.Steps.Length), Plan = Plan(act.Steps.Length) });
        }

        public Task<Telling> TellAsync(string message, CancellationToken cancellationToken)
        {
            if (message == Briefing.WrapUp(task))
                Next(WrapUp, false);
            else if (played + 1 < acts.Length)
                Next(acts[++played], acts != Plain);
            else
                Next(Plain[0], false);
            return Task.FromResult(Telling.WentOn);
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
