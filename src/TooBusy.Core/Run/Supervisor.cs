using System.Collections.Concurrent;
using System.Globalization;
using TooBusy.Core.Queue;

namespace TooBusy.Core.Run;

// A run of the queue: it takes the tasks one after another, has the assistant do each in a session of its own,
// watches the session, and tells the tracker how the task went. It is the run that keeps the tracker: a task is
// moved before its session starts and after it ends, and the session is told so.
//
// It never goes on past a doubt: a session that ends without saying how the task went, a working tree that is not
// clean, a tracker that refuses, each stops the run, and the next task is not built on it.
//
// Everything it knows of time comes from the clock, and everything it is told comes through `Send`, which only puts
// the command in a line: the run itself looks at the line, in one place, so nothing here is shared between threads.
public sealed class Supervisor(
    ITaskTracker tracker,
    IAssistant assistant,
    IWorkspace workspace,
    IRunState state,
    IMachine machine,
    IClock clock,
    RunPlan plan,
    RunPolicy policy) : IQueueRun
{
    // As many lines of what a session said are put into the log.
    const int Quoted = 12;

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

    // A task that was paused takes its session with it: nothing is left to stop when the run is killed after that.
    bool paused;

    // The record of the task that is worked on, as it was written last.
    TaskRecord? record;
    int done;

    // What the pause of a task or a kill said last: it is why the run ended.
    string? left;

    public void Send(RunCommand command) => commands.Enqueue(command);

    public async Task<RunResult> RunAsync(IRunView view, CancellationToken kill)
    {
        this.view = view;
        var started = clock.Now;
        RunResult result;
        using (machine.KeepAwake())
        {
            try
            {
                result = await LoopAsync(kill);
            }
            catch (OperationCanceledException) when (kill.IsCancellationRequested)
            {
                result = new RunResult(RunEnd.Killed, done, left);
            }
            catch (TrackerException refused)
            {
                result = Stop(refused.Message);
            }
            catch (AssistantException refused)
            {
                result = Stop(refused.Message);
            }
        }

        Say(RunMark.Note, $"Worked {Spoken.Time(clock.Now - started)}");
        Show(new RunStatus(RunPhase.Ended, ""));
        return result;
    }

    async Task<RunResult> LoopAsync(CancellationToken kill)
    {
        int? last = null;
        var first = true;
        while (true)
        {
            Drain();
            kill.ThrowIfCancellationRequested();
            if (stopping)
            {
                Say(aborting ? RunMark.Interrupted : RunMark.Paused, $"Stopped {(aborting ? "after /abort" : "by /stop")} · {done} done");
                machine.Notify($"Stopped: {done} done");
                return new RunResult(RunEnd.Stopped, done);
            }

            // The record of an earlier run is looked at before the queue. A task that has one left its changes in
            // the working tree, and they are the only ones a run starts over; a session that wrapped up left none.
            Show(new RunStatus(RunPhase.Preparing, "Checking the working tree…"));
            var before = state.Load();
            if (await workspace.ReadAsync(kill) is not { } tree)
                return Stop("the working tree cannot be read");
            if (tree.Changes > 0 && (before is null || before.State == RecordState.WrappedUp))
                return Stop("the working tree is not clean: commit or stash the changes first");

            Show(new RunStatus(RunPhase.Preparing, "Reading the queue…"));
            var open = await tracker.ReadOpenAsync(plan.Milestone, kill);
            var lineup = TaskLineup.Arrange(open, plan.Rules);
            if (before is not null)
            {
                // The record is the proof that it was a run that moved the task, so its status holds nothing back.
                // What was done to the task since does: one that is closed or got a label that keeps it out.
                var (kept, why) = TaskLineup.Recorded(open, plan.Rules, before.Number);
                if (kept is null)
                {
                    await ForgetAsync(before, why!, tree.Changes > 0);
                    continue;
                }

                lineup = lineup.Taking(kept);
            }

            if (first)
                Head(lineup);
            if (lineup.Ready.Count == 0)
            {
                if (!first && lineup.Held.Count > 0)
                    Say(RunMark.Note, Held(lineup));
                Say(RunMark.Done, $"No task is left · {done} done");
                machine.Notify($"No task is left: {done} done");
                return new RunResult(RunEnd.Emptied, done);
            }

            first = false;
            limits = assistant.ReadLimits().At(clock.Now);
            if (limits.Near is { ResetsAt: { } reset } near && near.Used >= policy.Limit)
            {
                var until = reset + policy.ResetMargin;
                Say(RunMark.Paused, $"{Capital(Limit(near))} is at {Spoken.Percent(near.Used)}: waiting {Spoken.Time(until - clock.Now)} for its reset");
                Show(new RunStatus(RunPhase.WaitingForLimit, $"Waiting for {Limit(near)} to reset") { Until = until });
                if (await WaitAsync(until, kill))
                    Say(RunMark.GoesOn, $"{Capital(Limit(near))} has reset");
                continue;
            }

            var task = lineup.Ready[0];
            if (task.Number == last)
                return Stop($"#{task.Number} came up again after its session");

            last = task.Number;
            if (await DoAsync(task, before, lineup.Ready.Count + lineup.Later.Count - 1, kill) is { } ended)
                return ended;
        }
    }

    // A task of the record that is not gone on with: its session is stopped, so that nothing works on with nobody
    // watching, and the record is forgotten.
    async Task ForgetAsync(TaskRecord before, string why, bool changes)
    {
        await Quietly(async () =>
        {
            if (await assistant.FindAsync(before.Session, CancellationToken.None) is { } session)
                await session.StopAsync(CancellationToken.None);
        });
        state.Clear();
        Say(RunMark.Attention, $"#{before.Number} was left with a session by an earlier run, but it is not gone on with: {why} · its session is stopped and forgotten"
            + (changes ? ", and its changes are still in the working tree" : ""));
    }

    // Does one task: null when the run goes on to the next one. `before` is the record of an earlier run that names
    // the task: its session is gone on with, in the way it was left.
    async Task<RunResult?> DoAsync(QueueTask task, TaskRecord? before, int queued, CancellationToken kill)
    {
        var number = task.Number;
        var since = clock.Now;
        var again = task.Has(plan.Rules.Interrupted);
        Show(new RunStatus(RunPhase.Preparing, $"Starting #{number}…") { Task = task, Since = since, Queued = queued });

        // The session of the record, how the task is taken with it, and the look at a session that ended with an
        // outcome while nobody watched: it is not watched, only settled.
        IAssistantSession? session = null;
        SessionLook? over = null;
        var how = "";
        if (before is not null && await assistant.FindAsync(before.Session, kill) is { } found)
        {
            // A session that the run stopped itself is told to go on; one that was left working is looked at.
            var message = before.State switch
            {
                RecordState.Paused => Briefing.AfterLimit(number),
                RecordState.WrappedUp => Briefing.AfterAbort(number),
                _ => null,
            };
            how = before.State == RecordState.Paused ? "its session goes on after the usage limit" : "its session goes on after /abort";
            if (message is null)
            {
                var look = await found.LookAsync(kill);
                var stopped = look.Phase == SessionPhase.Lost || (look.Limit is not null && look.Phase != SessionPhase.Working);
                if (look.Phase == SessionPhase.Unseen)
                    return Stop($"the sessions of the assistant cannot be read, so the session of #{number} is left as it is · {found.Open} · the next run looks again");

                if (look.Limit is null && look.Phase is SessionPhase.Ended or SessionPhase.Lost && Outcome.Read(look.Reply) is not null)
                    (over, how) = (look, "its session ended while nobody watched");
                else if (stopped)
                    (message, how) = (look.Limit is null ? Briefing.AfterStop(number) : Briefing.AfterLimit(number), look.Limit is null ? "its session was stopped, and goes on" : "its session goes on after the usage limit");
                else
                    how = "its session still works, and is watched again";
            }

            // The tracker is told only when the session goes on: one that does not is tried again by the next
            // run, with the task and the record as they are.
            if (message is not null && !await found.TellAsync(message, kill))
                return Stop($"the session of #{number} did not go on · {found.Open} · the next run tries again");

            session = found;
            await tracker.SetStatusAsync(number, BoardStatus.InProgress, kill);
        }

        if (session is null)
        {
            // A record whose session and conversation are gone gives a new session, which is told what it finds.
            var gone = before is not null;
            await tracker.SetStatusAsync(number, BoardStatus.InProgress, kill);
            try
            {
                session = await assistant.StartAsync(Start(task, Briefing.Task(task, again && before?.State != RecordState.Paused, lost: before is { State: not RecordState.WrappedUp })), kill);
            }
            catch (AssistantException)
            {
                // Nobody works on the task, so it does not stay as one that is worked on.
                await Quietly(() => tracker.SetStatusAsync(number, BoardStatus.Todo, CancellationToken.None));
                throw;
            }

            how = gone ? "a new session: the conversation of the one before is gone"
                : again ? "a new session; the task was interrupted before"
                : "a new session";
        }

        state.Save(record = new TaskRecord(number, session.Trace));
        if (again)
            await tracker.RemoveLabelAsync(number, plan.Rules.Interrupted, kill);

        Say(RunMark.Started, $"#{number} {task.Title} — {(before is null ? "started" : "taken again")} · {how} · {session.Open}");
        if (plan.Rules.Board)
            Say(RunMark.Note, $"#{number} is In Progress on the board; the session is told that toobusy keeps the tracker");

        // However the task ends for this run, it is told once: as one that failed, unless more is known of it.
        var mark = RunMark.Failed;
        try
        {
            Watched watched;
            try
            {
                watched = over is { } ended ? new Watched(ended) : await WatchAsync(task, session, since, queued, kill);
            }
            catch (OperationCanceledException) when (kill.IsCancellationRequested)
            {
                if (!paused)
                {
                    await Quietly(() => session.StopAsync(CancellationToken.None));
                    left = $"#{number} stays In Progress, and the next run goes on with its session · {session.Open}";
                    Say(RunMark.Interrupted, $"Killed the session of #{number}: the task stays In Progress, and the next run goes on with its session · {session.Open}");
                }

                throw;
            }

            if (watched.Paused)
            {
                mark = RunMark.Paused;
                return new RunResult(RunEnd.Limited, done, left);
            }

            (mark, var result) = await SettleAsync(task, session, watched.Look, since, kill);
            return result;
        }
        catch (OperationCanceledException) when (kill.IsCancellationRequested)
        {
            mark = paused ? RunMark.Paused : RunMark.Interrupted;
            throw;
        }
        finally
        {
            view.Report(new TaskEnd(task, mark, clock.Now - since));
        }
    }

    SessionStart Start(QueueTask task, string message) => new(task.Number, $"#{task.Number} {task.Title}", message, plan.Model, plan.Effort);

    // The last look at a session that is over for this run; Paused tells that a usage limit stopped its task.
    readonly record struct Watched(SessionLook Look, bool Paused = false);

    // Watches the session until its turn ends with an outcome, or until nothing more can come of it.
    async Task<Watched> WatchAsync(QueueTask task, IAssistantSession session, DateTimeOffset since, int queued, CancellationToken kill)
    {
        var number = task.Number;
        var look = new SessionLook(SessionPhase.Working);
        var next = clock.Now;

        // How many times the session was told to go on alone, how many of the last ones brought no step of its
        // own, and how many steps it had made when it was told last.
        var told = 0;
        var idle = 0;
        var steps = 0;
        var limitWaits = 0;

        // Whether the session waits for the owner, and when it is told to go on alone; null is never.
        var waiting = false;
        DateTimeOffset? sendAt = null;
        var unseen = false;

        // Whether the session was asked to wrap up, when, and whether its turn was cut for it.
        var asked = false;
        var askedAt = clock.Now;
        var cut = false;

        RunStatus Now() => new(waiting ? RunPhase.WaitingForOwner : RunPhase.Working, "")
        {
            Task = task,
            Since = since,
            Step = unseen ? "its session cannot be read" : look.Step,
            Context = look.Context,
            Plan = look.Plan ?? [],
            Queued = queued,
            Until = sendAt,
            Open = session.Open,
            Reply = waiting ? Said(look.Reply) : [],
        };

        while (true)
        {
            Drain();
            kill.ThrowIfCancellationRequested();

            Remember(number, session);

            if (aborting && !asked)
            {
                // A session that works reads the request after its next step. One that waits makes no step, so
                // its turn is cut at once, as it is for one that did not wrap up in time.
                asked = true;
                askedAt = clock.Now;
                sendAt = null;
                if (!waiting)
                {
                    await session.AskAsync(Briefing.WrapUp(number), kill);
                    Say(RunMark.Interrupted, $"Asked #{number} to wrap up after its current step: nothing committed, a report in the task, the {plan.Rules.Interrupted} label");
                }
            }

            if (aborting && !cut && (waiting || clock.Now - askedAt >= policy.AbortGrace))
            {
                cut = true;
                Say(RunMark.Interrupted, waiting
                    ? $"#{number} waits for the owner: its turn is cut, and it is told to wrap up"
                    : $"#{number} did not wrap up in {Spoken.Time(policy.AbortGrace)}: its turn is cut, and it is asked again");
                waiting = false;
                if (!await session.TellAsync(Briefing.WrapUp(number), kill))
                    return new Watched(look with { Phase = SessionPhase.Lost });

                look = look with { Phase = SessionPhase.Working };
                next = clock.Now + policy.Poll;
                Show(Now());
            }

            if (clock.Now >= next)
            {
                look = await session.LookAsync(kill);
                next = clock.Now + policy.Poll;
                Remember(number, session);
                if (look.Phase == SessionPhase.Unseen)
                {
                    if (!unseen)
                    {
                        unseen = true;
                        Say(RunMark.Paused, $"The sessions of the assistant cannot be read: #{number} may still be working · {session.Open}");
                        Show(Now());
                    }
                }
                else
                {
                    if (unseen)
                        Say(RunMark.GoesOn, $"The sessions are read again: #{number} is watched");
                    unseen = false;

                    // A turn that ends without saying how the task went waits for the owner as a question does.
                    var silent = look.Phase == SessionPhase.Ended && Outcome.Read(look.Reply) is null && !aborting;
                    if (look.Limit is { } refusal && look.Phase != SessionPhase.Working)
                    {
                        if (!await LimitAsync(task, session, since, refusal, ++limitWaits, kill))
                            return new Watched(look, Paused: true);

                        (waiting, sendAt) = (false, null);
                        look = look with { Phase = SessionPhase.Working, Limit = null };
                        next = clock.Now + policy.Poll;
                    }
                    else if (look.Phase == SessionPhase.Lost || (look.Phase == SessionPhase.Ended && !silent))
                    {
                        return new Watched(look);
                    }
                    else if (look.Phase == SessionPhase.Working)
                    {
                        (waiting, sendAt) = (false, null);
                    }
                    else if (!waiting)
                    {
                        // A step of its own since it was told last makes this a new stop, not the old one again.
                        waiting = true;
                        idle = told > 0 && look.Steps == steps ? idle + 1 : 0;
                        var left = idle >= policy.IdleMessages ? $"{policy.IdleMessages} messages in a row brought no step of its own"
                            : told >= policy.Messages ? $"it was told to go on alone {policy.Messages} times"
                            : null;
                        sendAt = left is null && !aborting ? clock.Now + policy.OwnerWait : null;
                        Say(RunMark.Paused, (look.Phase == SessionPhase.Asking ? $"#{number} waits for the owner: {look.Asks ?? "an answer is needed"}" : $"#{number} ended its turn without saying how the task went")
                            + (sendAt is not null ? $" · it is told to go on alone in {Spoken.Time(policy.OwnerWait)}: /nudge does it now, /hold never"
                                : left is not null ? $" · it is left to the owner: {left}"
                                : "")
                            + $" · {session.Open}");
                        Quote(look.Reply, session);
                        if (!aborting)
                            machine.Notify($"#{number} waits for an answer");
                        if (look.Phase == SessionPhase.Ended && left is not null)
                            return new Watched(look);
                    }

                    Show(Now());
                }
            }

            if (waiting && hold && sendAt is not null)
            {
                sendAt = null;
                Say(RunMark.Paused, $"#{number} gets no message and waits for the owner · /nudge tells it to go on alone · {session.Open}");
                Show(Now());
            }

            if (waiting && !aborting && (nudge || (sendAt is { } due && clock.Now >= due)))
            {
                told++;
                steps = look.Steps;
                (waiting, sendAt) = (false, null);
                var message = Briefing.Alone(number);
                Say(RunMark.GoesOn, $"#{number} is told to go on without the owner: its turn is cut, and this is the next message of its conversation · {session.Open}");
                Say(RunMark.Note, $"“{message}”");
                if (!await session.TellAsync(message, kill))
                {
                    Say(RunMark.Failed, $"#{number} did not go on with the message");
                    return new Watched(look with { Phase = SessionPhase.Lost });
                }

                look = look with { Phase = SessionPhase.Working };
                next = clock.Now + policy.Poll;
                Show(Now());
            }

            (nudge, hold) = (false, false);
            await clock.DelayAsync(policy.Tick, kill);
        }
    }

    // What the session is found by is written when it is known and whenever it changes: a look may learn it, and
    // a session that was told something may be known by another name.
    void Remember(int number, IAssistantSession session)
    {
        if (record?.Session != session.Trace)
            state.Save(record = new TaskRecord(number, session.Trace));
    }

    // A session ran into a usage limit: the reset is waited for and the session goes on, or its task is paused for
    // the next run. False when it is paused.
    async Task<bool> LimitAsync(QueueTask task, IAssistantSession session, DateTimeOffset since, string refusal, int waits, CancellationToken kill)
    {
        var number = task.Number;
        Say(RunMark.Paused, $"#{number} ran into a usage limit: {refusal.Split('\n')[0].Trim()}");
        limits = assistant.ReadLimits().At(clock.Now);
        if (limits.Far is { } spent && spent.Used >= policy.Spent)
            return await PauseAsync(task, session, $"{Limit(spent)} is spent{Reset(spent)}", kill);
        if (waits > policy.LimitWaits)
            return await PauseAsync(task, session, $"the limit did not reset after {policy.LimitWaits} waits", kill);

        await session.StopAsync(kill);
        var known = limits.Near is { ResetsAt: not null } near && near.Used >= policy.Spent ? limits.Near : null;
        var until = known is { ResetsAt: { } reset } ? reset + policy.ResetMargin : clock.Now + policy.UnknownLimitWait;
        Say(RunMark.Paused, $"#{number} waits {Spoken.Time(until - clock.Now)} for {(known is null ? "the limit" : Limit(known))} to reset, then goes on · /stop pauses it for the next run");
        Show(new RunStatus(RunPhase.WaitingForLimit, "") { Task = task, Since = since, Until = until, Open = session.Open, Plan = status.Plan });

        bool waited;
        try
        {
            waited = await WaitAsync(until, kill);
        }
        catch (OperationCanceledException) when (kill.IsCancellationRequested)
        {
            await PauseAsync(task, session, "toobusy was stopped while it waited for the limit", CancellationToken.None);
            throw;
        }

        if (!waited)
            return await PauseAsync(task, session, "toobusy was stopped while it waited for the limit", kill);
        if (!await session.TellAsync(Briefing.AfterLimit(number), kill))
            return await PauseAsync(task, session, "its session did not go on after the reset", kill);

        Say(RunMark.GoesOn, $"#{number} goes on after the reset of the limit · {session.Open}");
        return true;
    }

    // Leaves the task for the next run with its changes in the working tree: the session is stopped, the task is
    // marked and goes back among those to do, and what the session is gone on with is remembered. Always false, for
    // the one who asks whether the session goes on.
    async Task<bool> PauseAsync(QueueTask task, IAssistantSession session, string why, CancellationToken cancellationToken)
    {
        var number = task.Number;
        paused = true;
        var missing = new List<string>();
        async Task Try(Func<Task> change)
        {
            try
            {
                await change();
            }
            catch (TrackerException refused)
            {
                missing.Add(refused.Message);
            }
        }

        await Quietly(() => session.StopAsync(cancellationToken));
        await Try(() => tracker.AddLabelAsync(number, plan.Rules.Interrupted, cancellationToken));
        await Try(() => tracker.SetStatusAsync(number, BoardStatus.Todo, cancellationToken));
        await Try(() => tracker.CommentAsync(number, Briefing.Paused(why, session.Open), cancellationToken));
        state.Save(record = new TaskRecord(number, session.Trace, RecordState.Paused));
        if (session.Trace.Conversation is null)
            missing.Add("the conversation of its session is not known");

        left = $"#{number} is paused: {why}. Its changes stay in the working tree, and the next run goes on with its session";
        Say(RunMark.Paused, $"{left} · {done} done");
        if (missing.Count > 0)
            Say(RunMark.Attention, $"The pause of #{number} is not whole: {string.Join(" · ", missing)} · set it right by hand before the next run");
        machine.Notify($"#{number} is paused by a usage limit");
        return false;
    }

    // Tells the tracker how the task went, after checking what the session says against the working copy. Gives the
    // mark of how it went, and how the run ends with it: null when the run goes on to the next task.
    async Task<(RunMark Mark, RunResult? Ended)> SettleAsync(QueueTask task, IAssistantSession session, SessionLook look, DateTimeOffset since, CancellationToken kill)
    {
        var number = task.Number;
        var took = Spoken.Time(clock.Now - since);
        await Quietly(() => session.StopAsync(kill));
        kill.ThrowIfCancellationRequested();

        // A failure is for the owner: the record is forgotten, and no run goes on with the task by itself.
        if (Outcome.Read(look.Reply) is not { } outcome)
        {
            state.Clear();
            Say(RunMark.Failed, $"#{number} {task.Title} — ended after {took} without saying how the task went");
            return (RunMark.Failed, Stop($"the session of #{number} did not say how the task went · {session.Open}"));
        }

        if (outcome.Kind == OutcomeKind.Failed)
        {
            state.Clear();
            Say(RunMark.Failed, $"#{number} {task.Title} — failed after {took}: {outcome.Reason ?? "no reason is given"}");
            Quote(outcome.Report, session);
            return (RunMark.Failed, Stop($"#{number} failed · {session.Open}"));
        }

        var tree = await workspace.ReadAsync(kill);

        // The task has come to an outcome, and its record is forgotten before the tracker is told, so that nothing
        // is told twice. Only a task that was wrapped up keeps it: the next run goes on with the same conversation.
        if (outcome.Kind == OutcomeKind.Interrupted && tree is { Changes: 0 })
            state.Save(record = new TaskRecord(number, session.Trace, RecordState.WrappedUp));
        else
            state.Clear();

        if (tree is null)
            return (RunMark.Failed, Stop($"the working tree cannot be read after #{number} · {session.Open}"));
        if (tree.Changes > 0)
            return (RunMark.Failed, Stop($"#{number} left changes in the working tree · {session.Open}"));
        if (outcome.Kind is OutcomeKind.Done or OutcomeKind.Partial && tree.Unpushed > 0)
            return (RunMark.Failed, Stop($"#{number} left {(tree.Unpushed == 1 ? "a commit that is" : $"{tree.Unpushed} commits that are")} not pushed · {session.Open}"));

        var rules = plan.Rules;
        switch (outcome.Kind)
        {
            case OutcomeKind.Done:
                await tracker.CommentAsync(number, Briefing.Comment(OutcomeKind.Done, outcome.Report, session.Open), kill);
                await CloseAsync(number, kill);
                Say(RunMark.Done, $"#{number} {task.Title} — done in {took}");
                Say(RunMark.Note, $"#{number} is closed, with the report of the session as its comment");
                return (RunMark.Done, null);

            case OutcomeKind.Partial:
                // What is left is a task of its own: what the session wrote of it, and the description of the task
                // it comes from. It has the labels that make the task a task to take, and the one of the owner.
                var rest = outcome.Rest ?? new RestTask("", outcome.Report);
                var title = rest.Title.Length > 0 ? rest.Title : Briefing.RestTitle(task);
                string[] labels = [rules.Owner, .. task.Labels.Where(label => rules.Take.Contains(label, StringComparer.OrdinalIgnoreCase) && !label.Equals(rules.Owner, StringComparison.OrdinalIgnoreCase))];
                var made = await tracker.CreateAsync(new NewTask(title, Briefing.Rest(rest, task, await tracker.ReadDescriptionAsync(number, kill)), labels, plan.Milestone), kill);
                await tracker.CommentAsync(number, Briefing.Comment(OutcomeKind.Partial, outcome.Report, session.Open, made), kill);
                await CloseAsync(number, kill);
                Say(RunMark.Partial, $"#{number} {task.Title} — done in part in {took}");
                Say(RunMark.Note, $"#{number} is closed; what is left is #{made} “{title}”, which waits for the owner with the {rules.Owner} label");
                return (RunMark.Partial, null);

            case OutcomeKind.Owner:
                await tracker.AddLabelAsync(number, rules.Owner, kill);
                await tracker.CommentAsync(number, Briefing.Comment(OutcomeKind.Owner, outcome.Report, session.Open, label: rules.Owner), kill);
                await tracker.SetStatusAsync(number, BoardStatus.Todo, kill);
                Say(RunMark.Owner, $"#{number} {task.Title} — waits for the owner after {took}");
                Say(RunMark.Note, $"#{number} has the {rules.Owner} label and a comment that says what is needed; no run takes it while it has the label");
                return (RunMark.Owner, null);

            default:
                await tracker.AddLabelAsync(number, rules.Interrupted, kill);
                await tracker.CommentAsync(number, Briefing.Comment(OutcomeKind.Interrupted, outcome.Report, session.Open), kill);
                await tracker.SetStatusAsync(number, BoardStatus.Todo, kill);
                Say(RunMark.Interrupted, $"#{number} {task.Title} — interrupted after {took}: the report is in the task, and the next run takes it first");
                Quote(outcome.Report, session);
                return (RunMark.Interrupted, aborting ? null : Stop($"#{number} was interrupted, and nobody asked for it · {session.Open}"));
        }
    }

    // A task that is done is closed. Moving it on the board follows, and a board that cannot take it there does not
    // undo what is done: it is said, and the run goes on.
    async Task CloseAsync(int number, CancellationToken kill)
    {
        await tracker.CloseAsync(number, kill);
        done++;
        try
        {
            await tracker.SetStatusAsync(number, BoardStatus.Done, kill);
        }
        catch (TrackerException refused)
        {
            Say(RunMark.Attention, refused.Message);
        }
    }

    // Waits for a moment to come; false when the owner asked to stop before it did.
    async Task<bool> WaitAsync(DateTimeOffset until, CancellationToken kill)
    {
        while (clock.Now < until)
        {
            Drain();
            kill.ThrowIfCancellationRequested();
            if (stopping)
                return false;

            await clock.DelayAsync(policy.Tick, kill);
        }

        return true;
    }

    // Takes what the owner asked for, in the order it was asked.
    void Drain()
    {
        var heard = false;
        while (commands.TryDequeue(out var command))
        {
            heard = true;
            var waitsForLimit = status is { Phase: RunPhase.WaitingForLimit, Task: not null };
            switch (command)
            {
                case RunCommand.Stop when aborting:
                    Say(RunMark.Note, "Already aborting");
                    break;
                case RunCommand.Stop when stopping && !waitsForLimit:
                    Say(RunMark.Note, "Already stopping after the current task");
                    break;
                case RunCommand.Stop:
                    stopping = true;
                    Say(RunMark.Paused, waitsForLimit ? $"#{status.Task!.Number} waits for a limit: it is paused now, and the next run goes on with its session"
                        : status.Task is { } current ? $"Will stop after #{current.Number} · /continue takes tasks again"
                        : "The queue stops before the next task");
                    break;

                case RunCommand.Continue when aborting:
                    Say(RunMark.Note, "An /abort cannot be taken back");
                    break;
                case RunCommand.Continue when stopping:
                    stopping = false;
                    Say(RunMark.GoesOn, "The queue goes on after the current task");
                    break;
                case RunCommand.Continue:
                    Say(RunMark.Note, "Nothing to take back: the queue goes on");
                    break;

                case RunCommand.Abort when aborting:
                    Say(RunMark.Note, "Already asked to wrap up");
                    break;
                case RunCommand.Abort:
                    (aborting, stopping) = (true, true);
                    if (status.Task is null)
                        Say(RunMark.Interrupted, "Nothing is running: the queue stops before the next task");
                    else if (waitsForLimit)
                        Say(RunMark.Interrupted, $"#{status.Task.Number} waits for a limit and cannot wrap up: it is paused with its changes in the working tree");
                    break;

                case RunCommand.Nudge when status.Phase == RunPhase.WaitingForOwner && !aborting:
                    nudge = true;
                    break;
                case RunCommand.Hold when status is { Phase: RunPhase.WaitingForOwner, Until: not null }:
                    hold = true;
                    break;
                default:
                    Say(RunMark.Note, $"/{command.ToString().ToLowerInvariant()} means nothing now");
                    break;
            }
        }

        if (heard)
            Show(status);
    }

    // The first lines of a run: what it works on, and what it will not take as things are.
    void Head(TaskLineup lineup)
    {
        Say(RunMark.Head, $"{plan.Milestone ?? "No milestone"} · {Spoken.Tasks(lineup.Ready.Count + lineup.Later.Count)} in the queue · {plan.Model.Name ?? "the assistant's own model"} · {plan.Effort} effort");
        if (lineup.Later.Count > 0)
            Say(RunMark.Note, "Opens later: " + string.Join(" · ", lineup.Later.Select(later => $"#{later.Task.Number} after {string.Join(", ", later.After.Select(before => $"#{before}"))}")));
        if (lineup.Held.Count > 0)
            Say(RunMark.Note, Held(lineup));
    }

    static string Held(TaskLineup lineup) => "Held: " + string.Join(" · ", lineup.Held.Select(held => $"#{held.Task.Number} {held.Reason}"));

    // What a session said, line by line, without the empty lines and without the line that is for toobusy.
    static List<string> Said(string? reply) =>
        [.. (reply ?? "").Split('\n').Select(line => line.TrimEnd()).Where(line => line.Length > 0 && !line.Trim().StartsWith(Outcome.Mark, StringComparison.OrdinalIgnoreCase))];

    // What a session said, in the log: its first lines, and where the rest is.
    void Quote(string? reply, IAssistantSession session)
    {
        var lines = Said(reply);
        foreach (var line in lines.Take(Quoted))
            Say(RunMark.Note, $"│ {line}");
        if (lines.Count > Quoted)
            Say(RunMark.Note, $"│ … {session.Open} shows the whole session");
    }

    RunResult Stop(string problem)
    {
        Say(RunMark.Failed, $"Stopped: {problem} · the next task did not start · {done} done");
        machine.Notify($"Stopped: {problem}");
        return new RunResult(RunEnd.Problem, done, problem);
    }

    void Say(RunMark mark, string text) => view.Say(new RunLine(mark, text));

    void Show(RunStatus next)
    {
        status = next with { Limits = limits, Stopping = stopping && !aborting, Aborting = aborting, Available = Available(next) };
        view.Show(status);
    }

    // The commands that mean something at the moment.
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

    static string Limit(UsageWindow window) => $"the {window.Name} limit";

    static string Reset(UsageWindow window) =>
        window.ResetsAt is { } reset ? string.Create(CultureInfo.InvariantCulture, $", it resets {reset.UtcDateTime:yyyy-MM-dd HH:mm} UTC") : "";

    static string Capital(string text) => text.Length == 0 ? text : char.ToUpperInvariant(text[0]) + text[1..];

    // What is tried for good measure: a refusal of it changes nothing of what the run does next.
    static async Task Quietly(Func<Task> attempt)
    {
        try
        {
            await attempt();
        }
        catch (Exception refused) when (refused is TrackerException or AssistantException or OperationCanceledException)
        {
        }
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
