using System.Collections.Concurrent;
using System.Globalization;
using TooBusy.Core.Queue;

namespace TooBusy.Core.Run;

// A run of the queue: it takes the tasks one after another, each a `TaskRun`, which has the assistant do it in a session
// of its own, watches the session, and tells the tracker how the task went. It is the run that keeps the task of a
// session: it is moved before its session starts and after it ends, and the session is told so. New tasks are the
// session's to make. A run of several sessions takes tasks side by side, each in a git worktree of its own, as
// `Supervisor.Many.cs` says.
//
// It never goes on past a doubt: a session that ends without saying how the task went, a working tree that is not
// clean, a tracker that refuses, a command of the project that fails, a file of the run that cannot be written, and
// whatever nobody foresaw, each stops the run, and the next task is not built on it; no session works on after that.
// A failure that may pass is not such a doubt yet: a call of the tracker that is safe to make twice, and the sync of
// the branch, are made again when GitHub or the remote did not answer, and stop the run only after that.
//
// Everything it knows of time comes from the clock, and everything it is told comes through `Send`, which only puts
// the command in a line: the run itself looks at the line, in one place. A task goes on a `Flow` of its own, and what
// the flows share is kept under `gate`.
public sealed partial class Supervisor(
    ITaskTracker tracker,
    IAssistant assistant,
    IWorkspace workspace,
    IRunState state,
    IRunSign sign,
    IMachine machine,
    IClock clock,
    IProjectCommands shell,
    RunPlan plan,
    RunPolicy policy,
    IWorkingCopies? copies = null) : IQueueRun
{
    readonly ConcurrentQueue<(RunCommand Command, int? Task)> commands = new();
    readonly Lock gate = new();

    // The working copy the run was started in, and the flow of what is done there and between tasks.
    readonly WorkingCopy home = new("", workspace, state, shell, assistant);
    Flow? main;

    Flow Main => main ??= new Flow(this);

    IRunView view = Silent.View;
    UsageLimits limits = UsageLimits.Unknown;

    // What the owner asked for: to stop after the task, and to stop as soon as possible.
    bool stopping;
    bool aborting;

    // What the sign of the run says, as it was written last; null while the sign is not up.
    RunSign? shown;

    // The tasks the run closed: those that are done, and those that are done in part.
    int done;
    int partial;

    // The tasks of this run that came to an outcome: done, done in part or left to the owner. A run that was
    // started for a number of tasks counts them.
    int taken;

    // Whether the run said that the branch of the tasks has no upstream: it says so once.
    bool unfollowed;

    // Whether the first lines of the run are still to be said, before the first task.
    bool first = true;

    // The task that was taken last: one that comes up again after its session stops the run.
    int? last;

    // How many times the run was started. A supervisor makes one run: what it counts and what it was asked for are
    // never reset, so a second run needs a supervisor of its own.
    int runs;

    // The tracker as a run calls it.
    RunTracker? calls;

    RunTracker Calls => calls ??= new RunTracker(tracker, clock, plan, policy, Say);

    // A task of the run in a working copy, as a new one: what it keeps is its own. A worktree is brought up to date
    // with the branch it lands on, as the working copy is with the branch of the settings.
    TaskRun NewTask(Flow flow, WorkingCopy copy, QueueTask task, int queued) =>
        new(task, queued, flow, new TaskTools(Calls, copy.Assistant, copy.Workspace, copy.State, copy.Shell, clock, copy.Landing is { } landing ? plan with { Branch = landing.Onto.Branch } : plan, policy) { Landing = copy.Landing });

    public void Send(RunCommand command, int? task = null) => commands.Enqueue((command, task));

    public async Task<RunResult> RunAsync(IRunView view, CancellationToken kill)
    {
        if (Interlocked.Increment(ref runs) > 1)
            throw new InvalidOperationException("A supervisor makes one run: a second run needs a supervisor of its own.");

        this.view = view;
        var started = clock.Now;

        // One run goes on in a working copy: a second one would work in the middle of the task of the first. A run
        // whose sign cannot be put up starts nothing either: nobody beside it could tell that it goes on.
        var up = RunSign.Of(Shown());
        string? unstarted;
        try
        {
            unstarted = sign.Raise(up) is { } other ? $"a run is going on in this working copy already: {other.Told}" : null;
        }
        catch (LocalStateException unwritten)
        {
            unstarted = unwritten.Message;
        }

        if (unstarted is not null)
        {
            Say(RunMark.Failed, $"Did not start: {unstarted}");
            Show(Main, new RunStatus(RunPhase.Ended, ""));
            return Ended(RunEnd.Problem, unstarted);
        }

        shown = up;
        RunResult result;
        using (machine.KeepAwake())
        {
            try
            {
                result = (await GuardedAsync(async () => await LoopAsync(kill), Main, kill))!;
            }
            finally
            {
                shown = null;
                Lower();
            }
        }

        Say(RunMark.Note, $"Worked {Spoken.Time(clock.Now - started)}");
        Show(Main, new RunStatus(RunPhase.Ended, ""));
        return result;
    }

    // What ends a flow of the run, or the run: no exception leaves it. A tracker, an assistant and a file of the run
    // that refuse stop the run with their words, and anything else does too, a cancellation that is not the kill among
    // it: a session that worked was stopped where its task was done, and the log names what happened.
    async Task<RunResult?> GuardedAsync(Func<Task<RunResult?>> doing, Flow flow, CancellationToken kill)
    {
        try
        {
            return await doing();
        }
        catch (OperationCanceledException) when (kill.IsCancellationRequested)
        {
            return Ended(RunEnd.Killed, flow.Left);
        }
        catch (TrackerException refused)
        {
            return Stop(refused.Message);
        }
        catch (AssistantException refused)
        {
            return Stop(refused.Message);
        }
        catch (LocalStateException unwritten)
        {
            return Stop(unwritten.Message);
        }
        catch (Exception unforeseen)
        {
            return Stop(Unforeseen(unforeseen), unforeseen.ToString());
        }
    }

    // A run of several sessions goes first where it has worktrees to go on with; a run of one session goes on from
    // there in the working copy, as it does when there are none.
    async Task<RunResult> LoopAsync(CancellationToken kill) => (copies is null ? null : await ManyAsync(kill)) ?? await OneAsync(kill);

    // What the owner wants before the next task: a kill, which is thrown, the end of a stop, or the end of a run that
    // was started for one task or for a number of them, as one that its owner stopped. Null when the run goes on.
    RunResult? Wanted(CancellationToken kill)
    {
        kill.ThrowIfCancellationRequested();
        if (stopping)
        {
            Say(aborting ? RunMark.Interrupted : RunMark.Paused, $"Stopped {(aborting ? "after /abort" : "by /stop")} · {Went()}");
            Notify($"Stopped: {Went()}");
            return Ended(RunEnd.Stopped);
        }

        if (Asked() is not { } asked)
            return null;

        Say(RunMark.Paused, $"{asked} · {Went()}");
        Notify($"{asked}: {Went()}");
        return Ended(RunEnd.Stopped, asked);
    }

    async Task<RunResult> OneAsync(CancellationToken kill)
    {
        while (true)
        {
            Drain();
            if (Wanted(kill) is { } wanted)
                return wanted;

            // The record of an earlier run is looked at before the queue. A task that has one left its changes in
            // the working tree, and they are the only ones a run starts over; a session that wrapped up left none.
            Show(Main, new RunStatus(RunPhase.Preparing, "Checking the working tree…"));
            var before = state.Load();
            if (await workspace.ReadAsync(kill) is not { } tree)
                return Stop("the working tree cannot be read");

            // The tasks are done on the branch of the project, and what is checked out is the owner's: the run does
            // not change it.
            if (plan.Branch is { } branch && tree.Branch != branch)
                return Stop($"the working copy is on {tree.Branch ?? "no branch"}, and the tasks are done on {branch}");
            if (tree.Changes > 0 && (before is null || before.State == RecordState.WrappedUp))
                return Stop("the working tree is not clean: commit or stash the changes first");

            Show(Main, new RunStatus(RunPhase.Preparing, "Reading the queue…"));
            var open = await Calls.OpenTasksAsync(kill);
            var lineup = TaskLineup.Arrange(open, plan.Rules);
            if (before is not null)
            {
                // The record is the proof that it was a run that moved the task, so its status holds nothing back.
                // What was done to the task since does: one that is closed or got a label that keeps it out.
                var (kept, why) = TaskLineup.Recorded(open, plan.Rules, before.Number);
                if (kept is null)
                {
                    if (await UnkeptAsync(home, before, why!, open, lineup, tree, kill) is { } over)
                        return over;
                    continue;
                }

                lineup = lineup.Taking(kept);
            }

            // A run of one task takes it only as a run of the queue would take it now, and says why when it would
            // not. The task of a record comes before any other, and its changes may be in the working tree, so
            // another task is not started over it.
            if (plan.Only is { } only)
            {
                if (before is not null && before.Number != only)
                    return Refuse(only, $"an earlier run left #{before.Number} with a session, and a run goes on with that task before any other");
                if (lineup.Ready.FirstOrDefault(ready => ready.Number == only) is not { } one)
                    return Refuse(only, lineup.Holds(only) ?? (plan.Milestone is null ? "it is not among the open tasks" : $"it is not among the open tasks of the milestone {plan.Milestone}"));

                lineup = TaskLineup.Of(one);
            }

            if (first)
                Head(lineup);
            if (lineup.Ready.Count == 0)
            {
                if (!first && lineup.Held.Count > 0)
                    Say(RunMark.Note, Held(lineup));
                return NoneLeft();
            }

            first = false;
            ReadLimits();
            var task = lineup.Ready[0];
            if (task.Number == last)
                return Stop($"#{task.Number} came up again after its session");

            last = task.Number;
            var queued = lineup.Ready.Count + lineup.Later.Count - 1;
            var ahead = plan.Most is { } most ? Math.Min(queued, most - taken - 1) : queued;
            if (await NewTask(Main, home, task, ahead).TakeAsync(before, kill) is { } ended)
                return ended;

            Taken();
        }
    }

    RunResult NoneLeft()
    {
        Say(RunMark.Done, $"No task is left · {Went()}");
        Notify($"No task is left: {Went()}");
        return Ended(RunEnd.Emptied);
    }

    // Reads the limits of the assistant anew, for what the run shows too.
    UsageLimits ReadLimits()
    {
        lock (gate)
            return limits = assistant.ReadLimits().At(clock.Now);
    }

    // A task that came to an outcome.
    void Taken()
    {
        lock (gate)
            taken++;
    }

    // The task of a record that the queue does not go on with. One that was closed while nobody watched is settled
    // when its session ended with a line for toobusy, so that the work of a session that came to its line is not lost
    // over that, and the run goes on with the next task; any other is forgotten. Null when the run goes on.
    async Task<RunResult?> UnkeptAsync(WorkingCopy copy, TaskRecord before, string why, IReadOnlyList<QueueTask> open, TaskLineup lineup, WorkspaceState tree, CancellationToken kill)
    {
        // The session of the record where it was looked for: it is not looked for again to be stopped.
        IAssistantSession? found = null;

        // A task that is not among the open ones may be closed, as by a commit of its session with
        // `Closes #12` in its message: the queue has only the open ones, so the task is read by its number.
        if (open.All(task => task.Number != before.Number) && await Calls.ClosedTaskAsync(before.Number, kill) is { } closed)
        {
            var waiting = lineup.Ready.Count + lineup.Later.Count;
            var settled = NewTask(Main, copy, closed, plan.Most is { } few ? Math.Min(waiting, few - taken - 1) : waiting);

            // The session of a record that says more than that it worked did not end so.
            found = before.State == RecordState.Working ? await copy.Assistant.FindAsync(before.Session, kill) : null;
            var look = found is null ? null : await found.LookAsync(kill);
            if (look is not null)
                settled.Seen(found!, look);
            if (look?.Phase == SessionPhase.Unseen)
                return Stop($"the sessions of the assistant cannot be read, so the session of #{closed.Number} is left as it is · {found!.Open} · the next run looks again");

            if (look is not null && TaskRun.Over(look))
            {
                if (plan.Only is { } other && other != closed.Number)
                    return Refuse(other, $"an earlier run left #{closed.Number} with a session, and a run settles that task before any other");

                if (first)
                    Head(lineup);
                first = false;
                ReadLimits();
                last = closed.Number;
                if (await settled.FinishAsync(found!, look, kill) is { } over)
                    return over;

                Taken();
                return null;
            }

            why = before.State != RecordState.Working ? "it is closed"
                : found is null ? "it is closed, and nothing is left of its session"
                : "it is closed, and its session did not say how the task went";
        }

        await ForgetAsync(copy, before, why, tree.Changes > 0, found);
        return null;
    }

    // What ends a run that was started for one task or for a number of them, once it has done as much; null while
    // it goes on, and in a run of the whole queue.
    string? Asked() =>
        plan.Only is { } only && taken > 0 ? $"Stopped after #{only}, as you asked"
        : plan.Most is { } most && taken >= most ? $"Stopped after {Spoken.Tasks(taken)}, as you asked"
        : null;

    // The one task of a run is not taken: nothing is started, and the run says why.
    RunResult Refuse(int number, string why)
    {
        var problem = $"#{number} is not taken: {why}";
        Say(RunMark.Failed, $"Stopped: {problem} · no task was started");
        Notify($"Stopped: {problem}");
        return Ended(RunEnd.Problem, problem);
    }

    // A task of the record that is not gone on with: its session is stopped, so that nothing works on with nobody
    // watching, and the record is forgotten. `found` is the session of the record where the run has found it
    // already; otherwise it is looked for here.
    async Task ForgetAsync(WorkingCopy copy, TaskRecord before, string why, bool changes, IAssistantSession? found)
    {
        await Quietly(async () =>
        {
            if ((found ?? await copy.Assistant.FindAsync(before.Session, CancellationToken.None)) is { } session)
                await StopAsync(before.Number, session, CancellationToken.None);
        });
        copy.State.Clear();
        Say(RunMark.Attention, $"#{before.Number} was left with a session by an earlier run, but it is not gone on with: {why} · its session is stopped and forgotten"
            + (changes ? ", and its changes are still in the working tree" : ""));
    }

    // Takes what the owner asked for, in the order it was asked. A command does something only where it is among the
    // commands that mean something at the moment, as the run shows them; otherwise the log says why it does nothing. One
    // for a session that waits goes to the flow of that session.
    void Drain()
    {
        lock (gate)
        {
            var heard = false;
            while (commands.TryDequeue(out var order))
            {
                heard = true;
                var command = order.Command;
                var aimed = command is RunCommand.Nudge or RunCommand.Hold ? Flows.FirstOrDefault(flow => flow.Waits && (order.Task is null || flow.Number == order.Task)) : null;
                var at = aimed?.Status ?? Shown();
                if ((command is RunCommand.Nudge or RunCommand.Hold && aimed is null) || !Available(at).HasFlag(Flag(command)))
                {
                    Say(RunMark.Note, command switch
                    {
                        RunCommand.Stop when aborting => "Already aborting",
                        RunCommand.Stop => "Already stopping after the current task",
                        RunCommand.Continue when aborting => "An /abort cannot be taken back",
                        RunCommand.Continue => "Nothing to take back: the queue goes on",
                        RunCommand.Abort => "Already asked to wrap up",
                        _ => $"/{command.ToString().ToLowerInvariant()} means nothing now",
                    });
                    continue;
                }

                List<RunStatus> tasks = [.. Shown().Tasks];
                var limited = tasks.Where(task => task.Phase == RunPhase.WaitingForLimit).Select(task => task.Task!.Number).ToList();
                switch (command)
                {
                    case RunCommand.Stop:
                        stopping = true;
                        foreach (var number in limited)
                            Say(RunMark.Paused, $"#{number} waits for a limit: it is paused now, and the next run goes on with its session");
                        if (tasks.Count == 0)
                            Say(RunMark.Paused, "The queue stops before the next task");
                        else if (tasks.Count > limited.Count)
                            Say(RunMark.Paused, $"Will stop after {string.Join(", ", tasks.Where(task => task.Phase != RunPhase.WaitingForLimit).Select(task => $"#{task.Task!.Number}"))} · /continue takes tasks again");
                        break;
                    case RunCommand.Continue:
                        stopping = false;
                        Say(RunMark.GoesOn, "The queue goes on after the current task");
                        break;
                    case RunCommand.Abort:
                        (aborting, stopping) = (true, true);
                        if (tasks.Count == 0)
                            Say(RunMark.Interrupted, "Nothing is running: the queue stops before the next task");
                        foreach (var number in limited)
                            Say(RunMark.Interrupted, $"#{number} waits for a limit and cannot wrap up: it is paused with its changes in the working tree");
                        break;
                    case RunCommand.Nudge:
                        aimed!.Nudge();
                        break;
                    default:
                        aimed!.Hold();
                        break;
                }
            }

            if (heard)
                Publish();
        }
    }

    // The flows of the tasks that are worked on.
    IEnumerable<Flow> Flows => going.Count > 0 ? going : [Main];

    // A command among the commands that mean something.
    static RunCommands Flag(RunCommand command) => command switch
    {
        RunCommand.Stop => RunCommands.Stop,
        RunCommand.Continue => RunCommands.Continue,
        RunCommand.Abort => RunCommands.Abort,
        RunCommand.Nudge => RunCommands.Nudge,
        _ => RunCommands.Hold,
    };

    // The first lines of a run: what it works on, the labels it skips, and what it will not take as things are.
    void Head(TaskLineup lineup)
    {
        var queue = plan.Only is { } only ? $"#{only} and no other task"
            : $"{Spoken.Tasks(lineup.Ready.Count + lineup.Later.Count)} in the queue" + (plan.Most is { } most ? $", {most} at most" : "");
        var skips = plan.Rules.Skip.Count > 0 ? $" · skips {string.Join(", ", plan.Rules.Skip)}" : "";
        var sessions = plan.Sessions > 1 ? string.Create(CultureInfo.InvariantCulture, $" · {plan.Sessions} sessions at once") : "";
        Say(RunMark.Head, $"{plan.Milestone ?? "No milestone"} · {queue}{skips} · {plan.Model.Name ?? "the assistant's own model"} · {plan.Effort} effort · {plan.PermissionMode} mode{sessions}");
        if (lineup.Later.Count > 0)
            Say(RunMark.Note, "Opens later: " + string.Join(" · ", lineup.Later.Select(later => $"#{later.Task.Number} after {string.Join(", ", later.After.Select(before => $"#{before}"))}")));
        if (lineup.Held.Count > 0)
            Say(RunMark.Note, Held(lineup));
    }

    static string Held(TaskLineup lineup) => "Held: " + string.Join(" · ", lineup.Held.Select(held => $"#{held.Task.Number} {held.Reason}"));

    // `detail` is what the log on disk keeps of the problem beyond its line: the whole of an error nobody foresaw.
    RunResult Stop(string problem, string? detail = null)
    {
        lock (gate)
        {
            halted = true;
            view.Say(new RunLine(RunMark.Failed, $"Stopped: {problem} · {(going.Count > 1 ? "the sessions beside it finish, and the next task does not start" : "the next task did not start")} · {Went()}") { Detail = detail });
            machine.Notify($"Stopped: {problem}");
            return Ended(RunEnd.Problem, problem);
        }
    }

    // What nobody foresaw, in a line: its kind and its words, for a report of the bug.
    static string Unforeseen(Exception failed) => $"an error toobusy did not foresee: {failed.GetType().Name}: {string.Join(' ', SessionWords.Lines(failed.Message))}";

    // Takes the sign down however the run ended. A sign that stays says nothing once the process of the run ends, so
    // the run ends as it would have, and the log says so.
    void Lower()
    {
        try
        {
            sign.Lower();
        }
        catch (LocalStateException kept)
        {
            Say(RunMark.Attention, $"{kept.Message} · it counts until toobusy ends");
        }
    }

    // How the tasks of the run went so far, as the tape of a run says it: `3 done`, `3 done · 1 done in part`.
    string Went() => string.Create(CultureInfo.InvariantCulture, $"{done} done{(partial > 0 ? $" · {partial} done in part" : "")}");

    RunResult Ended(RunEnd end, string? why = null) => new(end, done, why) { Partial = partial };

    void Say(RunMark mark, string text)
    {
        lock (gate)
            view.Say(new RunLine(mark, text));
    }

    void Notify(string text)
    {
        lock (gate)
            machine.Notify(text);
    }

    void Show(Flow flow, RunStatus next)
    {
        lock (gate)
        {
            flow.Status = next;
            Publish();
        }
    }

    // What the run shows: the one flow it has between tasks and in a run of one session, and otherwise the tasks it
    // works on, the first of them with what is said of the run as a whole.
    RunStatus Shown()
    {
        if (going.Count == 0)
            return Stamped(Main.Status);

        List<RunStatus> beside = [.. going.Skip(1).Select(flow => Stamped(flow.Status))];
        return Stamped(going[0].Status with { Beside = beside, Queued = ahead });
    }

    RunStatus Stamped(RunStatus status) =>
        status with { Limits = limits, Behind = taken, Stopping = (stopping || halted) && !aborting, Aborting = aborting, Available = Available(status) };

    void Publish()
    {
        var status = Shown();
        view.Show(status);
        var now = RunSign.Of(status);
        if (shown is null || now == shown)
            return;

        // A sign that cannot be written stops the run, and is not tried again while the run ends over it.
        try
        {
            sign.Change(now);
            shown = now;
        }
        catch (LocalStateException)
        {
            shown = null;
            throw;
        }
    }

    // The commands that mean something at the moment, of any of the tasks that are shown: the run shows them, and does
    // nothing for any other.
    RunCommands Available(RunStatus at)
    {
        if (at.Phase == RunPhase.Ended)
            return RunCommands.None;

        var limit = at is { Phase: RunPhase.WaitingForLimit, Task: not null };
        return at.Beside.Aggregate(
            (aborting ? RunCommands.None : RunCommands.Abort)
                | (!aborting && (!stopping || limit) ? RunCommands.Stop : RunCommands.None)
                | (stopping && !aborting ? RunCommands.Continue : RunCommands.None)
                | (at.Phase == RunPhase.WaitingForOwner && !aborting ? RunCommands.Nudge : RunCommands.None)
                | (at is { Phase: RunPhase.WaitingForOwner, Until: not null } ? RunCommands.Hold : RunCommands.None),
            (all, beside) => all | Available(beside));
    }

    // Stops the session of a task, so that nothing works on with nobody watching. A session that the assistant still
    // has as working after it is said, for the owner to stop by hand; the run goes on as it would.
    async Task StopAsync(int number, IAssistantSession session, CancellationToken cancellationToken) =>
        await Quietly(async () =>
        {
            try
            {
                await session.StopAsync(cancellationToken);
            }
            catch (AssistantException working)
            {
                Say(RunMark.Attention, $"The session of #{number} did not stop: {working.Message} · stop it by hand · {session.Open}");
            }
        });

    // What is tried for good measure: a refusal of it changes nothing of what the run does next.
    internal static async Task Quietly(Func<Task> attempt)
    {
        try
        {
            await attempt();
        }
        catch (Exception refused) when (refused is TrackerException or AssistantException or OperationCanceledException)
        {
        }
    }

    // What one task goes on: what it asks of the run, and what is known of it for what the run shows. Everything it
    // touches of the run it touches under the gate.
    sealed class Flow(Supervisor run) : ITaskHost
    {
        // The session that waits is to be told now, or never.
        bool nudge;
        bool hold;

        // The number of the task, in a run of several sessions; what is being done with it, and what the flow came to.
        public int? Number { get; init; }

        public RunStatus Status { get; set; } = new(RunPhase.Preparing, "");

        public Task<RunResult?> Doing { get; set; } = Task.FromResult<RunResult?>(null);

        public bool Waits => Status.Phase == RunPhase.WaitingForOwner;

        public void Nudge() => nudge = true;

        public void Hold() => hold = true;

        public (bool Nudge, bool Hold) Hear()
        {
            run.Drain();
            lock (run.gate)
            {
                var heard = (nudge, hold);
                (nudge, hold) = (false, false);
                return heard;
            }
        }

        public bool Stopping
        {
            get
            {
                lock (run.gate)
                    return run.stopping;
            }
        }

        public bool Aborting
        {
            get
            {
                lock (run.gate)
                    return run.aborting;
            }
        }

        public string? Left { get; set; }

        public string Went
        {
            get
            {
                lock (run.gate)
                    return run.Went();
            }
        }

        public void Say(RunMark mark, string text) => run.Say(mark, text);

        public void SayUnfollowed(string branch)
        {
            lock (run.gate)
            {
                if (!run.unfollowed)
                    run.Say(RunMark.Note, $"{branch} has no upstream, so there is nothing to bring it up to date with");
                run.unfollowed = true;
            }
        }

        public void Trace(string text)
        {
            lock (run.gate)
                run.view.Trace(text);
        }

        public void Show(RunStatus status) => run.Show(this, status);

        public void Report(TaskEnd ended)
        {
            lock (run.gate)
                run.view.Report(ended);
        }

        public void Notify(string text) => run.Notify(text);

        public UsageLimits ReadLimits() => run.ReadLimits();

        public void Count(OutcomeKind kind)
        {
            lock (run.gate)
            {
                if (kind == OutcomeKind.Partial)
                    run.partial++;
                else
                    run.done++;
            }
        }

        public RunResult Stop(string problem) => run.Stop(problem);

        public RunResult Ended(RunEnd end, string? why = null)
        {
            lock (run.gate)
                return run.Ended(end, why);
        }

        public Task StopAsync(int number, IAssistantSession session, CancellationToken cancellationToken) => run.StopAsync(number, session, cancellationToken);
    }

    sealed class Silent : IRunView
    {
        public static Silent View { get; } = new();

        public void Say(RunLine line)
        {
        }

        public void Report(TaskEnd ended)
        {
        }

        public void Show(RunStatus status)
        {
        }
    }
}
