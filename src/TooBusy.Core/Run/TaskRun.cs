using System.Globalization;
using TooBusy.Core.Queue;

namespace TooBusy.Core.Run;

// What a task asks of the run it is a part of. The run has one task at a time, on its own flow, so nothing here is
// shared between threads.
interface ITaskHost
{
    // Takes what the owner asked for, and gives what is meant for the session that waits: to be told now, or never.
    (bool Nudge, bool Hold) Hear();

    // Whether the owner asked to stop after the task, and to stop as soon as possible.
    bool Stopping { get; }

    bool Aborting { get; }

    // What the pause of a task or a kill said last: it is why the run ended.
    string? Left { get; set; }

    // How the tasks of the run went so far, as the tape of a run says it.
    string Went { get; }

    void Say(RunMark mark, string text);

    // Says that the branch of the tasks has no upstream, once in a run.
    void SayUnfollowed(string branch);

    void Trace(string text);

    void Show(RunStatus status);

    void Report(TaskEnd ended);

    void Notify(string text);

    // Reads the limits of the assistant anew, for what the run shows too.
    UsageLimits ReadLimits();

    // Counts a task that the run closed.
    void Count(OutcomeKind kind);

    RunResult Stop(string problem);

    RunResult Ended(RunEnd end, string? why = null);

    Task StopAsync(int number, IAssistantSession session, CancellationToken cancellationToken);
}

// What a task of a run is done with.
sealed record TaskTools(RunTracker Calls, IAssistant Assistant, IWorkspace Workspace, IRunState State, IProjectCommands Shell, IClock Clock, RunPlan Plan, RunPolicy Policy);

// One task of a run: from the command before it to what the tracker is told of it, whether its session is new, is gone
// on with or ended while nobody watched. What it knows it keeps for itself, and a new task is a new one of these.
sealed class TaskRun(QueueTask task, int queued, ITaskHost host, TaskTools tools)
{
    readonly int number = task.Number;
    readonly RunTracker calls = tools.Calls;
    readonly IAssistant assistant = tools.Assistant;
    readonly IWorkspace workspace = tools.Workspace;
    readonly IRunState state = tools.State;
    readonly IProjectCommands shell = tools.Shell;
    readonly IClock clock = tools.Clock;
    readonly RunPlan plan = tools.Plan;
    readonly RunPolicy policy = tools.Policy;

    // When the task was taken.
    DateTimeOffset since;

    // A task that was paused takes its session with it: nothing is left to stop when the run is killed after that.
    bool paused;

    // The record of the task, as it was written last.
    TaskRecord? record;

    // Whether the task came to where the command before it runs: from there on the command after it runs, however the
    // task goes.
    bool begun;

    // How the session of the task was seen last, in the words of the log on disk; null before its first look.
    string? seen;

    // Takes the task and sees it to its end: null when the run goes on to the next one. `before` is the record of an
    // earlier run that names the task: its session is gone on with, in the way it was left.
    public Task<RunResult?> TakeAsync(TaskRecord? before, CancellationToken kill) => DoAsync(() => TakeTaskAsync(before, kill), kill);

    // Settles the task, closed while nobody watched, whose session ended with a line for toobusy: null when the run goes
    // on to the next one.
    public Task<RunResult?> FinishAsync(IAssistantSession session, SessionLook look, CancellationToken kill) => DoAsync(() => FinishTaskAsync(session, look, kill), kill);

    // Does one task with the commands of the project around it: null when the run goes on to the next one. `take`
    // is what is done with the task, from the moment that is given to it. The command after the task runs when the
    // run has done with it, whatever became of it, the tracker being told already. One that fails stops the run,
    // which does not go on over what the command was to put right; a run that ends anyway only says so.
    async Task<RunResult?> DoAsync(Func<Task<RunResult?>> take, CancellationToken kill)
    {
        since = clock.Now;
        RunResult? result;
        try
        {
            result = await take();

            // A kill does not cut the telling of an outcome or a pause: it ends the run when the task is over, as a
            // kill, unless the task stopped the run over a problem.
            if (result?.End != RunEnd.Problem)
                kill.ThrowIfCancellationRequested();
        }
        catch (Exception) when (begun)
        {
            // Whatever ended the task, the command runs.
            if (await AfterAsync(kill) is { } missed)
                Say(RunMark.Attention, $"{missed} · see to it by hand before the next run");
            throw;
        }

        if (!begun || await AfterAsync(kill) is not { } wrong)
            return result;
        if (result is null)
            return host.Stop($"{wrong} · #{task.Number} stays as it ended");

        Say(RunMark.Attention, $"{wrong} · see to it by hand before the next run");
        return result;
    }

    // Runs the command after a task. A kill that came before it does not cut it: it is what stops what the task
    // started. The owner waits for it then, so it is given less time.
    Task<string?> AfterAsync(CancellationToken kill)
    {
        var killed = kill.IsCancellationRequested;
        return CommandAsync(after: true, killed ? policy.KilledCommandTime : policy.CommandTime, killed ? CancellationToken.None : kill);
    }

    // Runs a command of the project around a task: null when it passed, and when the project has no such command;
    // otherwise what went wrong, after the log was told so with what the command printed.
    async Task<string?> CommandAsync(bool after, TimeSpan time, CancellationToken kill)
    {
        if ((after ? plan.Commands.After : plan.Commands.Before) is not { } command)
            return null;

        var name = $"the command {(after ? "after" : "before")} #{task.Number}";
        Show(new RunStatus(RunPhase.Preparing, $"Running {name}…") { Task = task, Since = since, Queued = queued, Step = after ? "the command after the task" : "the command before the task" });
        Say(RunMark.Note, $"Running {name}: {command}");
        var started = clock.Now;
        CommandEnd end;
        try
        {
            end = await shell.RunAsync(command, Variables(), time, kill);
        }
        catch (OperationCanceledException) when (kill.IsCancellationRequested)
        {
            host.Left = after ? $"{name} was cut by the kill" : $"{name} was cut by the kill, and the task was not taken";
            Say(RunMark.Interrupted, $"Killed {name}: {command}");
            throw;
        }

        if (end is { Status: CommandStatus.Exited, ExitCode: 0 })
        {
            Say(RunMark.Note, $"{name} passed in {Spoken.Time(clock.Now - started)}");
            if (!after)
                Show(new RunStatus(RunPhase.Preparing, $"Starting #{task.Number}…") { Task = task, Since = since, Queued = queued });
            return null;
        }

        var how = end.Status switch
        {
            CommandStatus.Exited => string.Create(CultureInfo.InvariantCulture, $"exited with {end.ExitCode}"),
            CommandStatus.TimedOut => $"did not end in {Spoken.Time(time)} and was stopped",
            _ => "did not start: the shell of the machine cannot be run",
        };
        Say(RunMark.Failed, $"{name} {how}: {command}");

        // The end of what a command printed is where it says what went wrong.
        var printed = SessionWords.Lines(end.Printed);
        if (printed.Count > SessionWords.Quoted)
            Say(RunMark.Note, string.Create(CultureInfo.InvariantCulture, $"│ … {printed.Count - SessionWords.Quoted} more lines before these"));
        foreach (var line in printed.TakeLast(SessionWords.Quoted))
            Say(RunMark.Note, $"│ {line}");
        return $"{name} {how}";
    }

    // Brings the branch of the tasks up to date with its upstream, before a task is taken anew and before the
    // command before it: null when it is up to date, and in a project that names no branch; otherwise what went
    // wrong. The working copy is on that branch: it was checked with the working tree.
    async Task<string?> BringUpAsync(CancellationToken kill)
    {
        if (plan.Branch is not { } branch)
            return null;

        Show(new RunStatus(RunPhase.Preparing, $"Bringing {branch} up to date…") { Task = task, Since = since, Queued = queued, Step = "bringing the branch up to date" });
        var sync = await workspace.SyncAsync(kill);

        // A remote that did not answer may answer a moment later: the sync is made again after each pause of the
        // policy, as a call of the tracker is.
        for (var tried = 0; sync is { Status: SyncStatus.Unreachable, MayPass: true } && tried < policy.RetryPauses.Count; tried++)
        {
            var pause = policy.RetryPauses[tried];
            Say(RunMark.Attention, $"The remote did not answer: {sync.Upstream ?? "the upstream"} is fetched again in {Spoken.Time(pause)}");
            await clock.DelayAsync(pause, kill);
            sync = await workspace.SyncAsync(kill);
        }

        var upstream = sync.Upstream ?? "its upstream";
        switch (sync.Status)
        {
            case SyncStatus.Level:
                break;
            case SyncStatus.Moved:
                Say(RunMark.Note, $"{branch} is moved forward by {Spoken.Commits(sync.Behind)} to {upstream}");
                break;
            case SyncStatus.NoUpstream:
                host.SayUnfollowed(branch);
                break;
            case SyncStatus.Ahead:
                return $"{branch} has {Spoken.Commits(sync.Ahead)} that {upstream} lacks: push {(sync.Ahead == 1 ? "it" : "them")} first, or take {(sync.Ahead == 1 ? "it" : "them")} back";
            case SyncStatus.Parted:
                return $"{branch} and {upstream} have parted: {branch} has {Spoken.Commits(sync.Ahead)} that {upstream} lacks, and lacks {Spoken.Commits(sync.Behind)} of it: rebase or merge it first";
            case SyncStatus.Unreachable:
                return $"{upstream} cannot be fetched: {sync.Said}";
            default:
                return $"{branch} cannot be brought up to date with {upstream}: {sync.Said}";
        }

        Show(new RunStatus(RunPhase.Preparing, $"Starting #{task.Number}…") { Task = task, Since = since, Queued = queued });
        return null;
    }

    // What a command of the project is told of the task, in its environment.
    Dictionary<string, string> Variables() => new()
    {
        ["TOOBUSY_TASK"] = task.Number.ToString(CultureInfo.InvariantCulture),
        ["TOOBUSY_TASK_TITLE"] = task.Title,
        ["TOOBUSY_TASK_URL"] = task.Url,
    };

    // Takes the task and sees it to its end: null when the run goes on to the next one.
    async Task<RunResult?> TakeTaskAsync(TaskRecord? before, CancellationToken kill)
    {
        var again = task.Has(plan.Rules.Labels.Interrupted);
        Show(new RunStatus(RunPhase.Preparing, $"Starting #{number}…") { Task = task, Since = since, Queued = queued });

        var found = before is null ? null : await assistant.FindAsync(before.Session, kill);
        var (taken, stopped) = found is null ? await StartSessionAsync(before, again, kill) : await GoOnAsync(found, before!.State, kill);
        if (taken is null)
            return stopped;

        // The session works from here on, so it is written down before the tracker is told anything more: a
        // tracker that refuses, or a kill, stops the session as it stops the run, and the next run goes on with it.
        var session = taken.Session;
        string? unassigned = null;
        try
        {
            state.Save(record = new TaskRecord(number, session.Trace));

            // A session that is gone on with was told so before the tracker is: its task is moved after it.
            if (found is not null)
                await calls.MoveAsync(number, BoardStatus.InProgress, kill);

            // The one who runs toobusy is the one whose task it is now, with a board and without one. A task that
            // nobody is named on is still worked on: a tracker that refuses this is said, and the run goes on.
            try
            {
                await calls.AssignAsync(number, kill);
            }
            catch (TrackerException refused)
            {
                unassigned = refused.Message;
            }

            if (again)
                await calls.UnlabelAsync(number, plan.Rules.Labels.Interrupted, kill);
        }
        catch (TrackerException refused)
        {
            await StopAsync(session, CancellationToken.None);
            return host.Stop($"{refused.Message} · the session of #{number} is stopped, and the next run goes on with it · {session.Open}");
        }
        catch (OperationCanceledException) when (kill.IsCancellationRequested)
        {
            await StopAsync(session, CancellationToken.None);
            host.Left = $"#{number} was being taken, and the next run goes on with its session · {session.Open}";
            Say(RunMark.Interrupted, $"Killed the session of #{number} while the task was taken: the next run goes on with its session · {session.Open}");
            throw;
        }
        catch (Exception failed)
        {
            if (await BreakOffAsync(session, failed) is { } broken)
                return broken;
            throw;
        }

        Say(RunMark.Started, $"#{number} {task.Title} — {(before is null ? "started" : "taken again")} · {taken.How} · {session.Open}");
        string?[] kept = [plan.Rules.Board ? $"{plan.Rules.Statuses.InProgress} on the board" : null, unassigned is null ? "assigned to the current user" : null];
        if (kept.Any(part => part is not null))
            Say(RunMark.Note, $"#{number} is {string.Join(" and ", kept.Where(part => part is not null))}; the session is told that toobusy keeps its task");
        if (unassigned is not null)
            Say(RunMark.Attention, $"{unassigned} · the run goes on");

        return await SeeThroughAsync(session, taken.Over, kill);
    }

    // A task that was given a session: how that is said, and the look at a session that ended with an outcome while
    // nobody watched: it is not watched, only settled.
    sealed record Taken(IAssistantSession Session, string How, SessionLook? Over = null);

    // Goes on with the session of the record, in the way it was left. No task is taken when the run stops over it, and
    // what it stops with is given instead. The command before the task runs for a session that is told to go on. One
    // that still works is not disturbed by it, and one that ended is only settled; the command after the task runs for
    // them too.
    async Task<(Taken? Taken, RunResult? Stopped)> GoOnAsync(IAssistantSession found, RecordState left, CancellationToken kill)
    {
        var taking = Told(number, left);
        if (taking is null)
        {
            var look = await found.LookAsync(kill);
            Seen(found, look);
            if (look.Phase == SessionPhase.Unseen)
                return (null, host.Stop($"the sessions of the assistant cannot be read, so the session of #{number} is left as it is · {found.Open} · the next run looks again"));

            taking = Looked(number, look);
        }

        var (message, how, over) = taking;
        if (message is not null && await CommandAsync(after: false, policy.CommandTime, kill) is { } unready)
            return (null, host.Stop($"{unready} · the session of #{number} is not told to go on, and the next run tries again"));

        begun = true;

        // The tracker is told only when the session goes on: one that does not is tried again by the next run, with the
        // task and the record as they are.
        var former = found.Trace.Id;
        var tell = message is null ? Telling.WentOn : await found.TellAsync(message, kill);
        if (!tell.Went)
            return (null, host.Stop($"the session of #{number} did not go on: {tell.Why} · {found.Open} · the next run tries again"));

        return (new Taken(found, tell.AsCopy ? $"{how}, {AsCopy(former)}" : how, over), null);
    }

    // Gives the task a new session. A record whose session and conversation are gone gives one too, which is told what
    // it finds. A task that is taken anew starts from what the remote has; one of a record may start over the changes
    // of its session, and nothing is moved under them. No task is taken when the run stops over it, and what it stops
    // with is given instead.
    async Task<(Taken? Taken, RunResult? Stopped)> StartSessionAsync(TaskRecord? before, bool again, CancellationToken kill)
    {
        var gone = before is not null;
        if (!gone && await BringUpAsync(kill) is { } behind)
            return (null, host.Stop($"{behind} · #{number} is not taken"));
        if (await CommandAsync(after: false, policy.CommandTime, kill) is { } unready)
            return (null, host.Stop($"{unready} · #{number} is not taken"));

        begun = true;
        var start = Start(Briefing.Task(task, NewTasks.Of(plan), again && before?.State != RecordState.Paused, lost: before is { State: not RecordState.WrappedUp }, skill: plan.Skill));
        DateTimeOffset? starting = null;
        IAssistantSession session;
        try
        {
            await calls.MoveAsync(number, BoardStatus.InProgress, kill);
            starting = clock.Now;
            session = await assistant.StartAsync(start, kill);
        }
        catch (AssistantException)
        {
            // Nobody works on the task, so it does not stay as one that is worked on.
            await Supervisor.Quietly(() => calls.MoveAsync(number, BoardStatus.Todo, kill, uncut: true));
            throw;
        }
        catch (OperationCanceledException) when (kill.IsCancellationRequested)
        {
            await UnstartAsync(starting is { } asked ? (start, asked) : null, kill);
            throw;
        }

        var fresh = plan.Skill is null ? "a new session" : $"a new session with the skill {plan.Skill}";
        return (new Taken(session, gone ? $"{fresh}: the conversation of the one before is gone" : again ? $"{fresh}; the task was interrupted before" : fresh), null);
    }

    // Watches the session of the task and settles the task when it is over: null when the run goes on to the next one.
    // `over` is the look at a session that ended while nobody watched. However the task ends for this run, it is told
    // once: as one that failed, unless more is known of it.
    async Task<RunResult?> SeeThroughAsync(IAssistantSession session, SessionLook? over, CancellationToken kill)
    {
        var mark = RunMark.Failed;
        try
        {
            Watched watched;
            try
            {
                watched = over is { } ended ? new Watched(ended) : await new SessionWatch(this, task, queued, since, session, host, tools).WatchAsync(kill);
            }
            catch (OperationCanceledException) when (kill.IsCancellationRequested)
            {
                if (!paused)
                {
                    await StopAsync(session, CancellationToken.None);
                    host.Left = $"#{number} stays {plan.Rules.Statuses.InProgress}, and the next run goes on with its session · {session.Open}";
                    Say(RunMark.Interrupted, $"Killed the session of #{number}: the task stays {plan.Rules.Statuses.InProgress}, and the next run goes on with its session · {session.Open}");
                }

                throw;
            }
            catch (Exception failed)
            {
                if (await BreakOffAsync(session, failed) is { } broken)
                    return broken;
                throw;
            }

            if (watched.Paused)
            {
                mark = RunMark.Paused;
                return host.Ended(RunEnd.Limited, host.Left);
            }

            (mark, var result) = await Settlement(session).SettleAsync(watched.Look, kill);
            return result;
        }
        catch (OperationCanceledException) when (kill.IsCancellationRequested)
        {
            mark = paused ? RunMark.Paused : RunMark.Interrupted;
            throw;
        }
        finally
        {
            host.Report(new TaskEnd(task, mark, clock.Now - since));
        }
    }

    // How a session that was found is taken: the message it is told to go on with, null when it is told nothing, how
    // that is said, and the look at a session that ended with an outcome while nobody watched.
    sealed record Taking(string? Message, string How, SessionLook? Over = null);

    // A session that the run stopped itself is told to go on; null for one that was left working, which is looked at.
    static Taking? Told(int number, RecordState left) => left switch
    {
        RecordState.Paused => new Taking(Briefing.AfterLimit(number), "its session goes on after the usage limit"),
        RecordState.WrappedUp => new Taking(Briefing.AfterAbort(number), "its session goes on after /abort"),
        _ => null,
    };

    // A session that was left working, by a look at it.
    static Taking Looked(int number, SessionLook look) =>
        Over(look) ? new Taking(null, "its session ended while nobody watched", look)
        : look.Limit is not null && look.Phase != SessionPhase.Working ? new Taking(Briefing.AfterLimit(number), "its session goes on after the usage limit")
        : look.Phase == SessionPhase.Lost ? new Taking(Briefing.AfterStop(number), "its session was stopped, and goes on")
        : new Taking(null, "its session still works, and is watched again");

    // A kill while a task is moved to In Progress and its session is started: nobody works on the task, so it goes
    // back to Todo, as a task whose session did not start does. A start that was cut may have reached the assistant
    // all the same, and no record names what it started: such a session is looked for by the name the start gave it
    // and stopped, so that nothing works on with nobody watching. `started` is the start and when it was made, null
    // when the kill came before it. The kill cuts none of this: the owner waits for it.
    async Task UnstartAsync((SessionStart Start, DateTimeOffset Since)? started, CancellationToken kill)
    {
        string? unmoved = null;
        try
        {
            await calls.MoveAsync(number, BoardStatus.Todo, kill, uncut: true);
        }
        catch (TrackerException refused)
        {
            unmoved = refused.Message;
        }

        IAssistantSession? found = null;
        if (started is { } cut)
        {
            await Supervisor.Quietly(async () =>
            {
                if (await assistant.FindStartedAsync(cut.Start, cut.Since, CancellationToken.None) is { } session)
                {
                    await StopAsync(session, CancellationToken.None);
                    found = session;
                }
            });
        }

        var todo = plan.Rules.Statuses.Todo;
        host.Left = $"#{number} was being started: {(!plan.Rules.Board ? "it is left as it was" : unmoved is null ? $"it is back in {todo}" : $"it could not be moved back to {todo}")}"
            + (found is not null ? $", and the session that started for it all the same is stopped · {found.Open}"
                : started is not null ? ", and no session was found for it"
                : "");
        Say(RunMark.Interrupted, $"Killed while {host.Left}");
        if (unmoved is not null)
            Say(RunMark.Attention, $"{unmoved} · set it right by hand before the next run");
    }

    // A failure that is neither the tracker's nor the kill's while a session works, as a file of the run that cannot
    // be written: the session is stopped first, unless a pause stopped it, so that nothing works on with nobody
    // watching. A file of the run stops the run here, with its words; anything else is said, and the one who catches
    // it throws it on to where the run ends over what nobody foresaw: null then.
    async Task<RunResult?> BreakOffAsync(IAssistantSession session, Exception failed)
    {
        if (!paused)
            await StopAsync(session, CancellationToken.None);
        if (failed is LocalStateException unwritten)
            return host.Stop($"{unwritten.Message} · the session of #{number} is stopped · {session.Open}");

        Say(RunMark.Interrupted, $"Stopped the session of #{number}: the run cannot go on · {session.Open}");
        return null;
    }

    // Whether the look shows a session that ended with a line for toobusy: it is not watched, only settled.
    public static bool Over(SessionLook look) => look.Limit is null && look.Phase is SessionPhase.Ended or SessionPhase.Lost && Outcome.Of(look) is not null;

    // Settles a task that was closed while nobody watched and whose session ended with a line for toobusy: null when
    // the run goes on to the next one. Nothing is moved and nobody is assigned before: the task is not taken again,
    // only what its session said is checked and written into it.
    async Task<RunResult?> FinishTaskAsync(IAssistantSession session, SessionLook look, CancellationToken kill)
    {
        Show(new RunStatus(RunPhase.Preparing, $"Settling #{number}…") { Task = task, Since = since, Queued = queued });
        begun = true;
        Say(RunMark.Started, $"#{number} {task.Title} — taken again · it was closed since, and its session ended while nobody watched · {session.Open}");

        var mark = RunMark.Failed;
        try
        {
            (mark, var result) = await Settlement(session).SettleAsync(look, kill, closed: true);
            return result;
        }
        catch (OperationCanceledException) when (kill.IsCancellationRequested)
        {
            mark = RunMark.Interrupted;
            throw;
        }
        finally
        {
            host.Report(new TaskEnd(task, mark, clock.Now - since));
        }
    }

    SessionStart Start(string message) => new(task.Number, $"#{task.Number} {task.Title}", message, plan.Model, plan.Effort);

    // What the session is found by is written when it is known and whenever it changes: a look may learn it, and
    // a session that was told something may be known by another name.
    public void Remember(IAssistantSession session)
    {
        if (record?.Session != session.Trace)
            state.Save(record = new TaskRecord(number, session.Trace));
    }

    // A session ran into a usage limit: the reset is waited for and the session goes on, or its task is paused for
    // the next run. False when it is paused.
    public async Task<bool> LimitAsync(IAssistantSession session, SessionLook look, string refusal, int waits, CancellationToken kill)
    {
        Say(RunMark.Paused, $"#{number} ran into a usage limit: {refusal.Split('\n')[0].Trim()}");
        var limits = host.ReadLimits();
        if (limits.Far is { } spent && spent.Used >= policy.Spent)
            return await PauseAsync(session, $"{Limit(spent)} is spent{Reset(spent)}", kill);
        if (waits > policy.LimitWaits)
            return await PauseAsync(session, $"the limit did not reset after {policy.LimitWaits} waits", kill);

        // A kill while the session is stopped for the wait pauses the task, as one during the wait does.
        bool waited;
        try
        {
            await StopAsync(session, kill);
            kill.ThrowIfCancellationRequested();

            var known = limits.Near is { ResetsAt: not null } near && near.Used >= policy.Spent ? limits.Near : null;
            var until = known is { ResetsAt: { } reset } ? reset + policy.ResetMargin : clock.Now + policy.UnknownLimitWait;
            Say(RunMark.Paused, $"#{number} waits {Spoken.Time(until - clock.Now)} for {(known is null ? "the limit" : Limit(known))} to reset, then goes on · /stop pauses it for the next run");
            Show(new RunStatus(RunPhase.WaitingForLimit, "") { Task = task, Since = since, Until = until, Open = session.Open, Plan = look.Plan ?? [] });
            waited = await WaitAsync(until, kill);
        }
        catch (OperationCanceledException) when (kill.IsCancellationRequested)
        {
            await PauseAsync(session, "toobusy was stopped while it waited for the limit", kill);
            throw;
        }

        if (!waited)
            return await PauseAsync(session, "toobusy was stopped while it waited for the limit", kill);
        var former = session.Trace.Id;
        var tell = await session.TellAsync(Briefing.AfterLimit(number), kill);
        if (!tell.Went)
            return await PauseAsync(session, $"its session did not go on after the reset: {tell.Why}", kill);

        Say(RunMark.GoesOn, $"#{number} goes on after the reset of the limit{(tell.AsCopy ? $", {AsCopy(former)}" : "")} · {session.Open}");
        return true;
    }

    // Leaves the task for the next run with its changes in the working tree: the session is stopped, the task is
    // marked and goes back among those to do, and what the session is gone on with is remembered. Always false, for
    // the one who asks whether the session goes on. A kill does not cut a pause, whether it came before it or comes
    // in the middle of it: a task that is half paused is neither worked on nor waiting. After the kill nothing of it
    // is made twice, as the owner waits for it.
    async Task<bool> PauseAsync(IAssistantSession session, string why, CancellationToken kill)
    {
        paused = true;
        var missing = new List<string>();
        await StopAsync(session, CancellationToken.None);
        await RunTracker.Try(missing, () => calls.LabelAsync(number, plan.Rules.Labels.Interrupted, kill, uncut: true));
        await RunTracker.Try(missing, () => calls.MoveAsync(number, BoardStatus.Todo, kill, uncut: true));
        await RunTracker.Try(missing, () => calls.CommentAsync(number, Briefing.Paused(why, session.Open)));
        state.Save(record = new TaskRecord(number, session.Trace, RecordState.Paused));
        if (session.Trace.Conversation is null)
            missing.Add("the conversation of its session is not known");

        host.Left = $"#{number} is paused: {why}. Its changes stay in the working tree, and the next run goes on with its session";
        Say(RunMark.Paused, $"{host.Left} · {host.Went}");
        if (missing.Count > 0)
            Say(RunMark.Attention, $"The pause of #{number} is not whole: {string.Join(" · ", missing)} · set it right by hand before the next run");
        host.Notify($"#{number} is paused by a usage limit");
        return false;
    }

    // Waits for a moment to come; false when the owner asked to stop before it did.
    async Task<bool> WaitAsync(DateTimeOffset until, CancellationToken kill)
    {
        while (clock.Now < until)
        {
            host.Hear();
            kill.ThrowIfCancellationRequested();
            if (host.Stopping)
                return false;

            await clock.DelayAsync(policy.Tick, kill);
        }

        return true;
    }

    // Writes into the log on disk how the session of a task is seen, when that changed since its last look: what it
    // is doing, and what it waits for, the limit it ran into or what the assistant cannot make of it. The screen does
    // not show it.
    public void Seen(IAssistantSession session, SessionLook look)
    {
        var now = look.Phase.ToString().ToLowerInvariant() + ((look.Asks ?? look.Limit ?? look.Odd) is { } why ? $" ({string.Join(' ', SessionWords.Lines(why))})" : "");
        if (now == seen)
            return;

        var trace = session.Trace;
        host.Trace($"#{number} session {trace.Id ?? trace.Conversation}: {(seen is null ? now : $"{seen} → {now}")}");
        seen = now;
    }

    // How a session that went on as a copy is said to go on: the copy is named after the session it was of.
    static string AsCopy(string? former) => former is null ? "as a copy of its session" : $"as a copy of {former}";

    // A session that went on as a copy in the middle of its task is noted: it is opened by another name from now on.
    public void NoteCopy(string? former, IAssistantSession session) =>
        Say(RunMark.Note, $"#{number} goes on {AsCopy(former)}: Claude Code started a new session with the whole conversation · {session.Open}");

    static string Limit(UsageWindow window) => $"the {window.Name} limit";

    static string Reset(UsageWindow window) =>
        window.ResetsAt is { } reset ? $", it resets {Spoken.Moment(reset)}" : "";

    TaskSettlement Settlement(IAssistantSession session) => new(task, session, since, host, tools);

    void Say(RunMark mark, string text) => host.Say(mark, text);

    void Show(RunStatus status) => host.Show(status);

    Task StopAsync(IAssistantSession session, CancellationToken cancellationToken) => host.StopAsync(number, session, cancellationToken);
}

// What a session and a command of the project said, as a run quotes it.
static class SessionWords
{
    // As many lines of what a session said are put into the log.
    public const int Quoted = 12;

    // What a session said, line by line, without the empty lines and without the line that is for toobusy.
    public static List<string> Said(string? reply) => [.. Lines(reply ?? "").Where(line => !line.Trim().StartsWith(Outcome.Mark, StringComparison.OrdinalIgnoreCase))];

    // The lines of a text that are not empty, without what ends them.
    public static List<string> Lines(string text) => [.. text.Split('\n').Select(line => line.TrimEnd()).Where(line => line.Length > 0)];

    // What a session said, in the log: its first lines, and where the rest is.
    public static void Quote(ITaskHost host, string? reply, IAssistantSession session)
    {
        var lines = Said(reply);
        foreach (var line in lines.Take(Quoted))
            host.Say(RunMark.Note, $"│ {line}");
        if (lines.Count > Quoted)
            host.Say(RunMark.Note, $"│ … {session.Open} shows the whole session");
    }
}
