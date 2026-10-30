using System.Collections.Concurrent;
using System.Globalization;
using TooBusy.Core.Queue;

namespace TooBusy.Core.Run;

// A run of the queue: it takes the tasks one after another, each a `TaskRun`, which has the assistant do it in a session
// of its own, watches the session, and tells the tracker how the task went. It is the run that keeps the task of a
// session: it is moved before its session starts and after it ends, and the session is told so. New tasks are the
// session's to make.
//
// It never goes on past a doubt: a session that ends without saying how the task went, a working tree that is not
// clean, a tracker that refuses, a command of the project that fails, a file of the run that cannot be written, and
// whatever nobody foresaw, each stops the run, and the next task is not built on it; no session works on after that.
// A failure that may pass is not such a doubt yet: a call of the tracker that is safe to make twice, and the sync of
// the branch, are made again when GitHub or the remote did not answer, and stop the run only after that.
//
// Everything it knows of time comes from the clock, and everything it is told comes through `Send`, which only puts
// the command in a line: the run itself looks at the line, in one place, so nothing here is shared between threads.
public sealed class Supervisor(
    ITaskTracker tracker,
    IAssistant assistant,
    IWorkspace workspace,
    IRunState state,
    IRunSign sign,
    IMachine machine,
    IClock clock,
    IProjectCommands shell,
    RunPlan plan,
    RunPolicy policy) : IQueueRun, ITaskHost
{
    readonly ConcurrentQueue<RunCommand> commands = new();

    IRunView view = Silent.View;
    RunStatus status = new(RunPhase.Preparing, "");
    UsageLimits limits = UsageLimits.Unknown;

    // What the owner asked for: to stop after the task, to stop as soon as possible, and, of the session that waits,
    // to be told now or never.
    bool stopping;
    bool aborting;
    bool nudge;
    bool hold;

    // What the sign of the run says, as it was written last; null while the sign is not up.
    RunSign? shown;

    // The tasks the run closed: those that are done, and those that are done in part.
    int done;
    int partial;

    // The tasks of this run that came to an outcome: done, done in part or left to the owner. A run that was
    // started for a number of tasks counts them.
    int taken;

    // What the pause of a task or a kill said last: it is why the run ended.
    string? left;

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

    // A task of the run, as a new one: what it keeps is its own.
    TaskRun NewTask(QueueTask task, int queued) => new(task, queued, this, new TaskTools(Calls, assistant, workspace, state, shell, clock, plan, policy));

    public void Send(RunCommand command) => commands.Enqueue(command);

    public async Task<RunResult> RunAsync(IRunView view, CancellationToken kill)
    {
        if (Interlocked.Increment(ref runs) > 1)
            throw new InvalidOperationException("A supervisor makes one run: a second run needs a supervisor of its own.");

        this.view = view;
        var started = clock.Now;

        // One run goes on in a working copy: a second one would work in the middle of the task of the first. A run
        // whose sign cannot be put up starts nothing either: nobody beside it could tell that it goes on.
        var up = RunSign.Of(status);
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
            Show(new RunStatus(RunPhase.Ended, ""));
            return Ended(RunEnd.Problem, unstarted);
        }

        shown = up;
        RunResult result;
        using (machine.KeepAwake())
        {
            try
            {
                result = await LoopAsync(kill);
            }
            catch (OperationCanceledException) when (kill.IsCancellationRequested)
            {
                result = Ended(RunEnd.Killed, left);
            }
            catch (TrackerException refused)
            {
                result = Stop(refused.Message);
            }
            catch (AssistantException refused)
            {
                result = Stop(refused.Message);
            }
            catch (LocalStateException unwritten)
            {
                result = Stop(unwritten.Message);
            }
            catch (Exception unforeseen)
            {
                // What nothing of the run foresaw stops it all the same, a cancellation that is not the kill among
                // it: a session that worked was stopped where its task was done, and the log names what happened.
                result = Stop(Unforeseen(unforeseen), unforeseen.ToString());
            }
            finally
            {
                shown = null;
                Lower();
            }
        }

        Say(RunMark.Note, $"Worked {Spoken.Time(clock.Now - started)}");
        Show(new RunStatus(RunPhase.Ended, ""));
        return result;
    }

    async Task<RunResult> LoopAsync(CancellationToken kill)
    {
        while (true)
        {
            Drain();
            kill.ThrowIfCancellationRequested();
            if (stopping)
            {
                Say(aborting ? RunMark.Interrupted : RunMark.Paused, $"Stopped {(aborting ? "after /abort" : "by /stop")} · {Went()}");
                machine.Notify($"Stopped: {Went()}");
                return Ended(RunEnd.Stopped);
            }

            // A run that was started for one task, or for a number of them, ends as one that its owner stopped.
            if (Asked() is { } asked)
            {
                Say(RunMark.Paused, $"{asked} · {Went()}");
                machine.Notify($"{asked}: {Went()}");
                return Ended(RunEnd.Stopped, asked);
            }

            // The record of an earlier run is looked at before the queue. A task that has one left its changes in
            // the working tree, and they are the only ones a run starts over; a session that wrapped up left none.
            Show(new RunStatus(RunPhase.Preparing, "Checking the working tree…"));
            var before = state.Load();
            if (await workspace.ReadAsync(kill) is not { } tree)
                return Stop("the working tree cannot be read");

            // The tasks are done on the branch of the project, and what is checked out is the owner's: the run does
            // not change it.
            if (plan.Branch is { } branch && tree.Branch != branch)
                return Stop($"the working copy is on {tree.Branch ?? "no branch"}, and the tasks are done on {branch}");
            if (tree.Changes > 0 && (before is null || before.State == RecordState.WrappedUp))
                return Stop("the working tree is not clean: commit or stash the changes first");

            Show(new RunStatus(RunPhase.Preparing, "Reading the queue…"));
            var open = await Calls.OpenTasksAsync(kill);
            var lineup = TaskLineup.Arrange(open, plan.Rules);
            if (before is not null)
            {
                // The record is the proof that it was a run that moved the task, so its status holds nothing back.
                // What was done to the task since does: one that is closed or got a label that keeps it out.
                var (kept, why) = TaskLineup.Recorded(open, plan.Rules, before.Number);
                if (kept is null)
                {
                    if (await UnkeptAsync(before, why!, open, lineup, tree, kill) is { } over)
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
                Say(RunMark.Done, $"No task is left · {Went()}");
                machine.Notify($"No task is left: {Went()}");
                return Ended(RunEnd.Emptied);
            }

            first = false;
            limits = assistant.ReadLimits().At(clock.Now);
            var task = lineup.Ready[0];
            if (task.Number == last)
                return Stop($"#{task.Number} came up again after its session");

            last = task.Number;
            var queued = lineup.Ready.Count + lineup.Later.Count - 1;
            var ahead = plan.Most is { } most ? Math.Min(queued, most - taken - 1) : queued;
            if (await NewTask(task, ahead).TakeAsync(before, kill) is { } ended)
                return ended;

            taken++;
        }
    }

    // The task of a record that the queue does not go on with. One that was closed while nobody watched is settled
    // when its session ended with a line for toobusy, so that the work of a session that came to its line is not lost
    // over that, and the run goes on with the next task; any other is forgotten. Null when the run goes on.
    async Task<RunResult?> UnkeptAsync(TaskRecord before, string why, IReadOnlyList<QueueTask> open, TaskLineup lineup, WorkspaceState tree, CancellationToken kill)
    {
        // The session of the record where it was looked for: it is not looked for again to be stopped.
        IAssistantSession? found = null;

        // A task that is not among the open ones may be closed, as by a commit of its session with
        // `Closes #12` in its message: the queue has only the open ones, so the task is read by its number.
        if (open.All(task => task.Number != before.Number) && await Calls.ClosedTaskAsync(before.Number, kill) is { } closed)
        {
            var waiting = lineup.Ready.Count + lineup.Later.Count;
            var settled = NewTask(closed, plan.Most is { } few ? Math.Min(waiting, few - taken - 1) : waiting);

            // The session of a record that says more than that it worked did not end so.
            found = before.State == RecordState.Working ? await assistant.FindAsync(before.Session, kill) : null;
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
                limits = assistant.ReadLimits().At(clock.Now);
                last = closed.Number;
                if (await settled.FinishAsync(found!, look, kill) is { } over)
                    return over;

                taken++;
                return null;
            }

            why = before.State != RecordState.Working ? "it is closed"
                : found is null ? "it is closed, and nothing is left of its session"
                : "it is closed, and its session did not say how the task went";
        }

        await ForgetAsync(before, why, tree.Changes > 0, found);
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
        machine.Notify($"Stopped: {problem}");
        return Ended(RunEnd.Problem, problem);
    }

    // A task of the record that is not gone on with: its session is stopped, so that nothing works on with nobody
    // watching, and the record is forgotten. `found` is the session of the record where the run has found it
    // already; otherwise it is looked for here.
    async Task ForgetAsync(TaskRecord before, string why, bool changes, IAssistantSession? found)
    {
        await Quietly(async () =>
        {
            if ((found ?? await assistant.FindAsync(before.Session, CancellationToken.None)) is { } session)
                await StopAsync(before.Number, session, CancellationToken.None);
        });
        state.Clear();
        Say(RunMark.Attention, $"#{before.Number} was left with a session by an earlier run, but it is not gone on with: {why} · its session is stopped and forgotten"
            + (changes ? ", and its changes are still in the working tree" : ""));
    }

    // Takes what the owner asked for, in the order it was asked. A command does something only where it is among the
    // commands that mean something at the moment, as the run shows them; otherwise the log says why it does nothing.
    void Drain()
    {
        var heard = false;
        while (commands.TryDequeue(out var command))
        {
            heard = true;
            if (!Available(status).HasFlag(Flag(command)))
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

            var waitsForLimit = status is { Phase: RunPhase.WaitingForLimit, Task: not null };
            switch (command)
            {
                case RunCommand.Stop:
                    stopping = true;
                    Say(RunMark.Paused, waitsForLimit ? $"#{status.Task!.Number} waits for a limit: it is paused now, and the next run goes on with its session"
                        : status.Task is { } current ? $"Will stop after #{current.Number} · /continue takes tasks again"
                        : "The queue stops before the next task");
                    break;
                case RunCommand.Continue:
                    stopping = false;
                    Say(RunMark.GoesOn, "The queue goes on after the current task");
                    break;
                case RunCommand.Abort:
                    (aborting, stopping) = (true, true);
                    if (status.Task is null)
                        Say(RunMark.Interrupted, "Nothing is running: the queue stops before the next task");
                    else if (waitsForLimit)
                        Say(RunMark.Interrupted, $"#{status.Task.Number} waits for a limit and cannot wrap up: it is paused with its changes in the working tree");
                    break;
                case RunCommand.Nudge:
                    nudge = true;
                    break;
                default:
                    hold = true;
                    break;
            }
        }

        if (heard)
            Show(status);
    }

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
        Say(RunMark.Head, $"{plan.Milestone ?? "No milestone"} · {queue}{skips} · {plan.Model.Name ?? "the assistant's own model"} · {plan.Effort} effort · {plan.PermissionMode} mode");
        if (lineup.Later.Count > 0)
            Say(RunMark.Note, "Opens later: " + string.Join(" · ", lineup.Later.Select(later => $"#{later.Task.Number} after {string.Join(", ", later.After.Select(before => $"#{before}"))}")));
        if (lineup.Held.Count > 0)
            Say(RunMark.Note, Held(lineup));
    }

    static string Held(TaskLineup lineup) => "Held: " + string.Join(" · ", lineup.Held.Select(held => $"#{held.Task.Number} {held.Reason}"));

    // `detail` is what the log on disk keeps of the problem beyond its line: the whole of an error nobody foresaw.
    RunResult Stop(string problem, string? detail = null)
    {
        view.Say(new RunLine(RunMark.Failed, $"Stopped: {problem} · the next task did not start · {Went()}") { Detail = detail });
        machine.Notify($"Stopped: {problem}");
        return Ended(RunEnd.Problem, problem);
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

    void Say(RunMark mark, string text) => view.Say(new RunLine(mark, text));

    void Show(RunStatus next)
    {
        status = next with { Limits = limits, Behind = taken, Stopping = stopping && !aborting, Aborting = aborting, Available = Available(next) };
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

    // The commands that mean something at the moment: the run shows them, and does nothing for any other.
    RunCommands Available(RunStatus at)
    {
        if (at.Phase == RunPhase.Ended)
            return RunCommands.None;

        var limit = at is { Phase: RunPhase.WaitingForLimit, Task: not null };
        return (aborting ? RunCommands.None : RunCommands.Abort)
            | (!aborting && (!stopping || limit) ? RunCommands.Stop : RunCommands.None)
            | (stopping && !aborting ? RunCommands.Continue : RunCommands.None)
            | (at.Phase == RunPhase.WaitingForOwner && !aborting ? RunCommands.Nudge : RunCommands.None)
            | (at is { Phase: RunPhase.WaitingForOwner, Until: not null } ? RunCommands.Hold : RunCommands.None);
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

    (bool Nudge, bool Hold) ITaskHost.Hear()
    {
        Drain();
        var heard = (nudge, hold);
        (nudge, hold) = (false, false);
        return heard;
    }

    bool ITaskHost.Stopping => stopping;

    bool ITaskHost.Aborting => aborting;

    string? ITaskHost.Left
    {
        get => left;
        set => left = value;
    }

    string ITaskHost.Went => Went();

    void ITaskHost.Say(RunMark mark, string text) => Say(mark, text);

    void ITaskHost.SayUnfollowed(string branch)
    {
        if (!unfollowed)
            Say(RunMark.Note, $"{branch} has no upstream, so there is nothing to bring it up to date with");
        unfollowed = true;
    }

    void ITaskHost.Trace(string text) => view.Trace(text);

    void ITaskHost.Show(RunStatus status) => Show(status);

    void ITaskHost.Report(TaskEnd ended) => view.Report(ended);

    void ITaskHost.Notify(string text) => machine.Notify(text);

    UsageLimits ITaskHost.ReadLimits() => limits = assistant.ReadLimits().At(clock.Now);

    void ITaskHost.Count(OutcomeKind kind)
    {
        if (kind == OutcomeKind.Partial)
            partial++;
        else
            done++;
    }

    RunResult ITaskHost.Stop(string problem) => Stop(problem);

    RunResult ITaskHost.Ended(RunEnd end, string? why) => Ended(end, why);

    Task ITaskHost.StopAsync(int number, IAssistantSession session, CancellationToken cancellationToken) => StopAsync(number, session, cancellationToken);

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
