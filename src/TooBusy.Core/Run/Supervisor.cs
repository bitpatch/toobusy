using System.Collections.Concurrent;
using System.Globalization;
using TooBusy.Core.Queue;

namespace TooBusy.Core.Run;

// A run of the queue: it takes the tasks one after another, has the assistant do each in a session of its own,
// watches the session, and tells the tracker how the task went. It is the run that keeps the task of a session: it is
// moved before its session starts and after it ends, and the session is told so. New tasks are the session's to make.
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

    // What the sign of the run says, as it was written last; null while the sign is not up.
    RunSign? shown;

    // The tasks the run closed: those that are done, and those that are done in part.
    int done;
    int partial;

    // The tasks of this run that came to an outcome: done, done in part or left to the owner. A run that was
    // started for a number of tasks counts them.
    int taken;

    // Whether the task that is being done came to where the command before it runs: from there on the command after
    // it runs, however the task goes.
    bool begun;

    // What the pause of a task or a kill said last: it is why the run ended.
    string? left;

    // Whether the run said that the branch of the tasks has no upstream: it says so once.
    bool unfollowed;

    // How many times the run was started. A supervisor makes one run: what it counts and what it was asked for are
    // never reset, so a second run needs a supervisor of its own.
    int runs;

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
        int? last = null;
        var first = true;
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
            var open = await OpenTasksAsync(kill);
            var lineup = TaskLineup.Arrange(open, plan.Rules);
            if (before is not null)
            {
                // The record is the proof that it was a run that moved the task, so its status holds nothing back.
                // What was done to the task since does: one that is closed or got a label that keeps it out.
                var (kept, why) = TaskLineup.Recorded(open, plan.Rules, before.Number);
                if (kept is null)
                {
                    // The session of the record where it was looked for: it is not looked for again to be stopped.
                    IAssistantSession? found = null;

                    // A task that is not among the open ones may be closed, as by a commit of its session with
                    // `Closes #12` in its message: the queue has only the open ones, so the task is read by its number.
                    if (open.All(task => task.Number != before.Number) && await ClosedTaskAsync(before.Number, kill) is { } closed)
                    {
                        // The work of a session that came to its line is not lost over that: its task is settled.
                        // The session of a record that says more than that it worked did not end so.
                        found = before.State == RecordState.Working ? await assistant.FindAsync(before.Session, kill) : null;
                        var look = found is null ? null : await found.LookAsync(kill);
                        if (look is not null)
                            Seen(before.Number, found!, look);
                        if (look?.Phase == SessionPhase.Unseen)
                            return Stop($"the sessions of the assistant cannot be read, so the session of #{closed.Number} is left as it is · {found!.Open} · the next run looks again");

                        if (look is not null && Over(look))
                        {
                            if (plan.Only is { } other && other != closed.Number)
                                return Refuse(other, $"an earlier run left #{closed.Number} with a session, and a run settles that task before any other");

                            if (first)
                                Head(lineup);
                            first = false;
                            limits = assistant.ReadLimits().At(clock.Now);
                            last = closed.Number;
                            var waiting = lineup.Ready.Count + lineup.Later.Count;
                            var after = plan.Most is { } few ? Math.Min(waiting, few - taken - 1) : waiting;
                            if (await DoAsync(closed, after, since => FinishAsync(closed, found!, look, since, after, kill), kill) is { } over)
                                return over;

                            taken++;
                            continue;
                        }

                        why = before.State != RecordState.Working ? "it is closed"
                            : found is null ? "it is closed, and nothing is left of its session"
                            : "it is closed, and its session did not say how the task went";
                    }

                    await ForgetAsync(before, why!, tree.Changes > 0, found);
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
            if (await DoAsync(task, ahead, since => TakeAsync(task, before, since, ahead, kill), kill) is { } ended)
                return ended;

            taken++;
        }
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
                await session.StopAsync(CancellationToken.None);
        });
        state.Clear();
        Say(RunMark.Attention, $"#{before.Number} was left with a session by an earlier run, but it is not gone on with: {why} · its session is stopped and forgotten"
            + (changes ? ", and its changes are still in the working tree" : ""));
    }

    // Does one task with the commands of the project around it: null when the run goes on to the next one. `take`
    // is what is done with the task, from the moment that is given to it. The command after the task runs when the
    // run has done with it, whatever became of it, the tracker being told already. One that fails stops the run,
    // which does not go on over what the command was to put right; a run that ends anyway only says so.
    async Task<RunResult?> DoAsync(QueueTask task, int queued, Func<DateTimeOffset, Task<RunResult?>> take, CancellationToken kill)
    {
        var since = clock.Now;
        begun = false;
        RunResult? result;
        try
        {
            result = await take(since);

            // A kill does not cut the telling of an outcome or a pause: it ends the run when the task is over, as a
            // kill, unless the task stopped the run over a problem.
            if (result?.End != RunEnd.Problem)
                kill.ThrowIfCancellationRequested();
        }
        catch (Exception) when (begun)
        {
            // Whatever ended the task, the command runs.
            if (await AfterAsync(task, since, queued, kill) is { } missed)
                Say(RunMark.Attention, $"{missed} · see to it by hand before the next run");
            throw;
        }

        if (!begun || await AfterAsync(task, since, queued, kill) is not { } wrong)
            return result;
        if (result is null)
            return Stop($"{wrong} · #{task.Number} stays as it ended");

        Say(RunMark.Attention, $"{wrong} · see to it by hand before the next run");
        return result;
    }

    // Runs the command after a task. A kill that came before it does not cut it: it is what stops what the task
    // started. The owner waits for it then, so it is given less time.
    Task<string?> AfterAsync(QueueTask task, DateTimeOffset since, int queued, CancellationToken kill)
    {
        var killed = kill.IsCancellationRequested;
        return CommandAsync(after: true, task, since, queued, killed ? policy.KilledCommandTime : policy.CommandTime, killed ? CancellationToken.None : kill);
    }

    // Runs a command of the project around a task: null when it passed, and when the project has no such command;
    // otherwise what went wrong, after the log was told so with what the command printed.
    async Task<string?> CommandAsync(bool after, QueueTask task, DateTimeOffset since, int queued, TimeSpan time, CancellationToken kill)
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
            end = await shell.RunAsync(command, Variables(task), time, kill);
        }
        catch (OperationCanceledException) when (kill.IsCancellationRequested)
        {
            left = after ? $"{name} was cut by the kill" : $"{name} was cut by the kill, and the task was not taken";
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
        var printed = Lines(end.Printed);
        if (printed.Count > Quoted)
            Say(RunMark.Note, string.Create(CultureInfo.InvariantCulture, $"│ … {printed.Count - Quoted} more lines before these"));
        foreach (var line in printed.TakeLast(Quoted))
            Say(RunMark.Note, $"│ {line}");
        return $"{name} {how}";
    }

    // Brings the branch of the tasks up to date with its upstream, before a task is taken anew and before the
    // command before it: null when it is up to date, and in a project that names no branch; otherwise what went
    // wrong. The working copy is on that branch: it was checked with the working tree.
    async Task<string?> BringUpAsync(QueueTask task, DateTimeOffset since, int queued, CancellationToken kill)
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
                if (!unfollowed)
                    Say(RunMark.Note, $"{branch} has no upstream, so there is nothing to bring it up to date with");
                unfollowed = true;
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
    static Dictionary<string, string> Variables(QueueTask task) => new()
    {
        ["TOOBUSY_TASK"] = task.Number.ToString(CultureInfo.InvariantCulture),
        ["TOOBUSY_TASK_TITLE"] = task.Title,
        ["TOOBUSY_TASK_URL"] = task.Url,
    };

    // Takes a task and sees it to its end: null when the run goes on to the next one. `before` is the record of an
    // earlier run that names the task: its session is gone on with, in the way it was left.
    async Task<RunResult?> TakeAsync(QueueTask task, TaskRecord? before, DateTimeOffset since, int queued, CancellationToken kill)
    {
        seen = null;
        var number = task.Number;
        var again = task.Has(plan.Rules.Labels.Interrupted);
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
                Seen(number, found, look);
                var stopped = look.Phase == SessionPhase.Lost || (look.Limit is not null && look.Phase != SessionPhase.Working);
                if (look.Phase == SessionPhase.Unseen)
                    return Stop($"the sessions of the assistant cannot be read, so the session of #{number} is left as it is · {found.Open} · the next run looks again");

                if (Over(look))
                    (over, how) = (look, "its session ended while nobody watched");
                else if (stopped)
                    (message, how) = (look.Limit is null ? Briefing.AfterStop(number) : Briefing.AfterLimit(number), look.Limit is null ? "its session was stopped, and goes on" : "its session goes on after the usage limit");
                else
                    how = "its session still works, and is watched again";
            }

            // The command before the task runs for a session that is told to go on. One that still works is not
            // disturbed by it, and one that ended is only settled; the command after the task runs for them too.
            if (message is not null && await CommandAsync(after: false, task, since, queued, policy.CommandTime, kill) is { } unready)
                return Stop($"{unready} · the session of #{number} is not told to go on, and the next run tries again");

            begun = true;

            // The tracker is told only when the session goes on: one that does not is tried again by the next
            // run, with the task and the record as they are.
            var former = found.Trace.Id;
            var tell = message is null ? Telling.WentOn : await found.TellAsync(message, kill);
            if (!tell.Went)
                return Stop($"the session of #{number} did not go on: {tell.Why} · {found.Open} · the next run tries again");
            if (tell.AsCopy)
                how += $", {AsCopy(former)}";

            session = found;
        }

        // A session that is gone on with was told so before the tracker is: its task is moved after it.
        var goesOn = session is not null;
        if (session is null)
        {
            // A record whose session and conversation are gone gives a new session, which is told what it finds.
            // A task that is taken anew starts from what the remote has; one of a record may start over the changes
            // of its session, and nothing is moved under them.
            var gone = before is not null;
            if (!gone && await BringUpAsync(task, since, queued, kill) is { } behind)
                return Stop($"{behind} · #{number} is not taken");
            if (await CommandAsync(after: false, task, since, queued, policy.CommandTime, kill) is { } unready)
                return Stop($"{unready} · #{number} is not taken");

            begun = true;
            var start = Start(task, Briefing.Task(task, NewTasks.Of(plan), again && before?.State != RecordState.Paused, lost: before is { State: not RecordState.WrappedUp }, skill: plan.Skill));
            DateTimeOffset? starting = null;
            try
            {
                await MoveAsync(number, BoardStatus.InProgress, kill);
                starting = clock.Now;
                session = await assistant.StartAsync(start, kill);
            }
            catch (AssistantException)
            {
                // Nobody works on the task, so it does not stay as one that is worked on.
                await Quietly(() => MoveAsync(number, BoardStatus.Todo, kill, uncut: true));
                throw;
            }
            catch (OperationCanceledException) when (kill.IsCancellationRequested)
            {
                await UnstartAsync(number, starting is { } asked ? (start, asked) : null, kill);
                throw;
            }

            var fresh = plan.Skill is null ? "a new session" : $"a new session with the skill {plan.Skill}";
            how = gone ? $"{fresh}: the conversation of the one before is gone"
                : again ? $"{fresh}; the task was interrupted before"
                : fresh;
        }

        // The session works from here on, so it is written down before the tracker is told anything more: a
        // tracker that refuses, or a kill, stops the session as it stops the run, and the next run goes on with it.
        string? unassigned = null;
        try
        {
            state.Save(record = new TaskRecord(number, session.Trace));
            if (goesOn)
                await MoveAsync(number, BoardStatus.InProgress, kill);

            // The one who runs toobusy is the one whose task it is now, with a board and without one. A task that
            // nobody is named on is still worked on: a tracker that refuses this is said, and the run goes on.
            try
            {
                await AssignAsync(number, kill);
            }
            catch (TrackerException refused)
            {
                unassigned = refused.Message;
            }

            if (again)
                await UnlabelAsync(number, plan.Rules.Labels.Interrupted, kill);
        }
        catch (TrackerException refused)
        {
            await Quietly(() => session.StopAsync(CancellationToken.None));
            return Stop($"{refused.Message} · the session of #{number} is stopped, and the next run goes on with it · {session.Open}");
        }
        catch (OperationCanceledException) when (kill.IsCancellationRequested)
        {
            await Quietly(() => session.StopAsync(CancellationToken.None));
            left = $"#{number} was being taken, and the next run goes on with its session · {session.Open}";
            Say(RunMark.Interrupted, $"Killed the session of #{number} while the task was taken: the next run goes on with its session · {session.Open}");
            throw;
        }
        catch (Exception failed)
        {
            if (await BreakOffAsync(number, session, failed) is { } broken)
                return broken;
            throw;
        }

        Say(RunMark.Started, $"#{number} {task.Title} — {(before is null ? "started" : "taken again")} · {how} · {session.Open}");
        string?[] kept = [plan.Rules.Board ? $"{plan.Rules.Statuses.InProgress} on the board" : null, unassigned is null ? "assigned to the current user" : null];
        if (kept.Any(part => part is not null))
            Say(RunMark.Note, $"#{number} is {string.Join(" and ", kept.Where(part => part is not null))}; the session is told that toobusy keeps its task");
        if (unassigned is not null)
            Say(RunMark.Attention, $"{unassigned} · the run goes on");

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
                    left = $"#{number} stays {plan.Rules.Statuses.InProgress}, and the next run goes on with its session · {session.Open}";
                    Say(RunMark.Interrupted, $"Killed the session of #{number}: the task stays {plan.Rules.Statuses.InProgress}, and the next run goes on with its session · {session.Open}");
                }

                throw;
            }
            catch (Exception failed)
            {
                if (await BreakOffAsync(number, session, failed) is { } broken)
                    return broken;
                throw;
            }

            if (watched.Paused)
            {
                mark = RunMark.Paused;
                return Ended(RunEnd.Limited, left);
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

    // A kill while a task is moved to In Progress and its session is started: nobody works on the task, so it goes
    // back to Todo, as a task whose session did not start does. A start that was cut may have reached the assistant
    // all the same, and no record names what it started: such a session is looked for by the name the start gave it
    // and stopped, so that nothing works on with nobody watching. `started` is the start and when it was made, null
    // when the kill came before it. The kill cuts none of this: the owner waits for it.
    async Task UnstartAsync(int number, (SessionStart Start, DateTimeOffset Since)? started, CancellationToken kill)
    {
        string? unmoved = null;
        try
        {
            await MoveAsync(number, BoardStatus.Todo, kill, uncut: true);
        }
        catch (TrackerException refused)
        {
            unmoved = refused.Message;
        }

        IAssistantSession? found = null;
        if (started is { } cut)
        {
            await Quietly(async () =>
            {
                if (await assistant.FindStartedAsync(cut.Start, cut.Since, CancellationToken.None) is { } session)
                {
                    await session.StopAsync(CancellationToken.None);
                    found = session;
                }
            });
        }

        var todo = plan.Rules.Statuses.Todo;
        left = $"#{number} was being started: {(!plan.Rules.Board ? "it is left as it was" : unmoved is null ? $"it is back in {todo}" : $"it could not be moved back to {todo}")}"
            + (found is not null ? $", and the session that started for it all the same is stopped · {found.Open}"
                : started is not null ? ", and no session was found for it"
                : "");
        Say(RunMark.Interrupted, $"Killed while {left}");
        if (unmoved is not null)
            Say(RunMark.Attention, $"{unmoved} · set it right by hand before the next run");
    }

    // A failure that is neither the tracker's nor the kill's while a session works, as a file of the run that cannot
    // be written: the session is stopped first, unless a pause stopped it, so that nothing works on with nobody
    // watching. A file of the run stops the run here, with its words; anything else is said, and the one who catches
    // it throws it on to where the run ends over what nobody foresaw: null then.
    async Task<RunResult?> BreakOffAsync(int number, IAssistantSession session, Exception failed)
    {
        if (!paused)
            await Quietly(() => session.StopAsync(CancellationToken.None));
        if (failed is LocalStateException unwritten)
            return Stop($"{unwritten.Message} · the session of #{number} is stopped · {session.Open}");

        Say(RunMark.Interrupted, $"Stopped the session of #{number}: the run cannot go on · {session.Open}");
        return null;
    }

    // Whether the look shows a session that ended with a line for toobusy: it is not watched, only settled.
    static bool Over(SessionLook look) => look.Limit is null && look.Phase is SessionPhase.Ended or SessionPhase.Lost && OutcomeOf(look) is not null;

    // How the task went, as the session says: null unless it has reported, its reply ending with a line for toobusy
    // and nothing done after it. A line with a question after it says nothing: it may be one the session wrote before
    // it went on, and a session lost after such a reply has not said how the task went.
    static Outcome? OutcomeOf(SessionLook look) => look.Reported ? Outcome.Read(look.Reply) : null;

    // Settles a task that was closed while nobody watched and whose session ended with a line for toobusy: null when
    // the run goes on to the next one. Nothing is moved and nobody is assigned before: the task is not taken again,
    // only what its session said is checked and written into it.
    async Task<RunResult?> FinishAsync(QueueTask task, IAssistantSession session, SessionLook look, DateTimeOffset since, int queued, CancellationToken kill)
    {
        var number = task.Number;
        Show(new RunStatus(RunPhase.Preparing, $"Settling #{number}…") { Task = task, Since = since, Queued = queued });
        begun = true;
        Say(RunMark.Started, $"#{number} {task.Title} — taken again · it was closed since, and its session ended while nobody watched · {session.Open}");

        var mark = RunMark.Failed;
        try
        {
            (mark, var result) = await SettleAsync(task, session, look, since, kill, closed: true);
            return result;
        }
        catch (OperationCanceledException) when (kill.IsCancellationRequested)
        {
            mark = RunMark.Interrupted;
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
        var sent = 0;
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
            Step = unseen ? "its session cannot be read" : look.Odd ?? look.Step,
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
                    Say(RunMark.Interrupted, $"Asked #{number} to wrap up after its current step: nothing committed, a report in the task, the {plan.Rules.Labels.Interrupted} label");
                }
            }

            if (aborting && !cut && (waiting || clock.Now - askedAt >= policy.AbortGrace))
            {
                cut = true;
                Say(RunMark.Interrupted, waiting
                    ? $"#{number} waits for the owner: its turn is cut, and it is told to wrap up"
                    : $"#{number} did not wrap up in {Spoken.Time(policy.AbortGrace)}: its turn is cut, and it is asked again");
                waiting = false;
                var former = session.Trace.Id;
                var tell = await session.TellAsync(Briefing.WrapUp(number), kill);
                if (!tell.Went)
                {
                    Say(RunMark.Failed, $"#{number} did not go on with the request to wrap up: {tell.Why}");
                    return new Watched(look with { Phase = SessionPhase.Lost });
                }

                if (tell.AsCopy)
                    NoteCopy(number, former, session);

                look = look with { Phase = SessionPhase.Working };
                next = clock.Now + policy.Poll;
                Show(Now());
            }

            if (clock.Now >= next)
            {
                // The status line of the session tells of the limits as it works, so they are read with every look.
                look = await session.LookAsync(kill);
                Seen(number, session, look);
                limits = assistant.ReadLimits().At(clock.Now);
                next = clock.Now + policy.Poll;
                Remember(number, session);
                if (look.Phase == SessionPhase.Unseen)
                {
                    if (!unseen)
                    {
                        unseen = true;
                        Say(RunMark.Paused, $"The sessions of the assistant cannot be read: #{number} may still be working · {session.Open}");
                    }

                    Show(Now());
                }
                else
                {
                    if (unseen)
                        Say(RunMark.GoesOn, $"The sessions are read again: #{number} is watched");
                    unseen = false;

                    // A turn that ends without saying how the task went waits for the owner as a question does, and
                    // while the run aborts its session gets the request to wrap up as a message, as one that waits
                    // does; a turn that ends so after that message is over. One that the assistant marks as waiting
                    // for an answer and whose reply ends with a line for toobusy does not wait: the session has
                    // reported, and there is nothing for the owner to answer.
                    var silent = look.Phase == SessionPhase.Ended && OutcomeOf(look) is null && !cut;
                    var reported = look is { Phase: SessionPhase.Asking, Reported: true };
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
                    else if (reported && (sent > 0 || aborting))
                    {
                        // A session that was told that no answer will come, and one that was asked to wrap up, has
                        // nothing more to do: such a reply ends the task, whatever the assistant marks it as.
                        return new Watched(look);
                    }
                    else if (look.Phase == SessionPhase.Working)
                    {
                        (waiting, sendAt) = (false, null);
                    }
                    else if (reported)
                    {
                        // It is told so at once, with no countdown: nothing is asked of the owner.
                        sent++;
                        steps = look.Steps;
                        (waiting, sendAt) = (false, null);
                        var message = Briefing.NoAnswer(number, plan.Rules.Labels.Manual);
                        Say(RunMark.Attention, $"#{number} ended its turn with a line for toobusy, but Claude Code marks it as waiting for an answer · it is told that none will come · {session.Open}");
                        Quote(look.Reply, session);
                        Say(RunMark.Note, $"“{message}”");
                        var former = session.Trace.Id;
                        var tell = await session.TellAsync(message, kill);
                        if (!tell.Went)
                        {
                            Say(RunMark.Failed, $"#{number} did not go on with the message: {tell.Why}");
                            return new Watched(look with { Phase = SessionPhase.Lost });
                        }

                        if (tell.AsCopy)
                            NoteCopy(number, former, session);

                        look = look with { Phase = SessionPhase.Working };
                        next = clock.Now + policy.Poll;
                    }
                    else if (!waiting)
                    {
                        // A step of its own since it was told last makes this a new stop, not the old one again. While
                        // the run aborts, a session that did not get the request to wrap up as a message yet is not
                        // left to the owner for the messages before: it gets that one first.
                        waiting = true;
                        idle = sent > 0 && look.Steps == steps ? idle + 1 : 0;
                        var why = aborting && !cut ? null
                            : idle >= policy.IdleMessages ? $"{policy.IdleMessages} messages in a row brought no step of its own"
                            : sent >= policy.Messages ? $"it was told to go on alone {policy.Messages} times"
                            : null;
                        sendAt = why is null && !aborting ? clock.Now + policy.OwnerWait : null;
                        Say(RunMark.Paused, (look.Phase == SessionPhase.Asking ? $"#{number} waits for the owner: {look.Asks ?? "an answer is needed"}" : $"#{number} ended its turn without saying how the task went")
                            + (sendAt is not null ? $" · it is told to go on alone in {Spoken.Time(policy.OwnerWait)}: /nudge does it now, /hold never"
                                : why is not null ? $" · it is left to the owner: {why}"
                                : "")
                            + $" · {session.Open}");
                        Quote(look.Reply, session);
                        if (!aborting)
                            machine.Notify($"#{number} waits for an answer");
                        if (look.Phase == SessionPhase.Ended && why is not null)
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
                sent++;
                steps = look.Steps;
                (waiting, sendAt) = (false, null);
                var message = Briefing.Alone(number, plan.Rules.Labels.Manual);
                Say(RunMark.GoesOn, $"#{number} is told to go on without the owner: its turn is cut, and this is the next message of its conversation · {session.Open}");
                Say(RunMark.Note, $"“{message}”");
                var former = session.Trace.Id;
                var tell = await session.TellAsync(message, kill);
                if (!tell.Went)
                {
                    Say(RunMark.Failed, $"#{number} did not go on with the message: {tell.Why}");
                    return new Watched(look with { Phase = SessionPhase.Lost });
                }

                if (tell.AsCopy)
                    NoteCopy(number, former, session);

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

        // A kill while the session is stopped for the wait pauses the task, as one during the wait does.
        bool waited;
        try
        {
            await Quietly(() => session.StopAsync(kill));
            kill.ThrowIfCancellationRequested();

            var known = limits.Near is { ResetsAt: not null } near && near.Used >= policy.Spent ? limits.Near : null;
            var until = known is { ResetsAt: { } reset } ? reset + policy.ResetMargin : clock.Now + policy.UnknownLimitWait;
            Say(RunMark.Paused, $"#{number} waits {Spoken.Time(until - clock.Now)} for {(known is null ? "the limit" : Limit(known))} to reset, then goes on · /stop pauses it for the next run");
            Show(new RunStatus(RunPhase.WaitingForLimit, "") { Task = task, Since = since, Until = until, Open = session.Open, Plan = status.Plan });
            waited = await WaitAsync(until, kill);
        }
        catch (OperationCanceledException) when (kill.IsCancellationRequested)
        {
            await PauseAsync(task, session, "toobusy was stopped while it waited for the limit", kill);
            throw;
        }

        if (!waited)
            return await PauseAsync(task, session, "toobusy was stopped while it waited for the limit", kill);
        var former = session.Trace.Id;
        var tell = await session.TellAsync(Briefing.AfterLimit(number), kill);
        if (!tell.Went)
            return await PauseAsync(task, session, $"its session did not go on after the reset: {tell.Why}", kill);

        Say(RunMark.GoesOn, $"#{number} goes on after the reset of the limit{(tell.AsCopy ? $", {AsCopy(former)}" : "")} · {session.Open}");
        return true;
    }

    // Leaves the task for the next run with its changes in the working tree: the session is stopped, the task is
    // marked and goes back among those to do, and what the session is gone on with is remembered. Always false, for
    // the one who asks whether the session goes on. A kill does not cut a pause, whether it came before it or comes
    // in the middle of it: a task that is half paused is neither worked on nor waiting. After the kill nothing of it
    // is made twice, as the owner waits for it.
    async Task<bool> PauseAsync(QueueTask task, IAssistantSession session, string why, CancellationToken kill)
    {
        var number = task.Number;
        paused = true;
        var calls = CancellationToken.None;
        var missing = new List<string>();
        await Quietly(() => session.StopAsync(calls));
        await Try(missing, () => LabelAsync(number, plan.Rules.Labels.Interrupted, kill, uncut: true));
        await Try(missing, () => MoveAsync(number, BoardStatus.Todo, kill, uncut: true));
        await Try(missing, () => tracker.CommentAsync(number, Briefing.Paused(why, session.Open), calls));
        state.Save(record = new TaskRecord(number, session.Trace, RecordState.Paused));
        if (session.Trace.Conversation is null)
            missing.Add("the conversation of its session is not known");

        left = $"#{number} is paused: {why}. Its changes stay in the working tree, and the next run goes on with its session";
        Say(RunMark.Paused, $"{left} · {Went()}");
        if (missing.Count > 0)
            Say(RunMark.Attention, $"The pause of #{number} is not whole: {string.Join(" · ", missing)} · set it right by hand before the next run");
        machine.Notify($"#{number} is paused by a usage limit");
        return false;
    }

    // Settles a task whose session came to its end: the session is stopped, what it says is checked against the
    // working copy, and the tracker is told how the task went. Gives the mark of how it went, and how the run ends with
    // it: null when the run goes on to the next task.
    //
    // Until the record is forgotten a kill leaves the task as it is, and the next run settles it from the record. From
    // then on the kill cuts nothing: a task whose record is gone has nothing but the tracker to keep it, so the tracker
    // is told to the end, each call that is left once, and the run ends when the task is over.
    async Task<(RunMark Mark, RunResult? Ended)> SettleAsync(QueueTask task, IAssistantSession session, SessionLook look, DateTimeOffset since, CancellationToken kill, bool closed = false)
    {
        var number = task.Number;
        WorkspaceState? tree = null;
        try
        {
            await Quietly(() => session.StopAsync(kill));
            kill.ThrowIfCancellationRequested();
            if (OutcomeOf(look) is { Kind: not OutcomeKind.Failed })
                tree = await workspace.ReadAsync(kill);
        }
        catch (OperationCanceledException) when (kill.IsCancellationRequested)
        {
            await Quietly(() => session.StopAsync(CancellationToken.None));
            left = $"#{number} was being settled, and the next run settles it · {session.Open}";
            Say(RunMark.Interrupted, $"Killed while #{number} was being settled: nothing of how it went is told yet, and the next run settles it · {session.Open}");
            throw;
        }

        (RunMark Mark, RunResult? Ended) settled;
        try
        {
            settled = await ConcludeAsync(task, session, look, tree, Spoken.Time(clock.Now - since), closed, kill);
        }
        catch (TrackerException) when (kill.IsCancellationRequested)
        {
            SettledAfterKill(number);
            throw;
        }

        if (kill.IsCancellationRequested)
            SettledAfterKill(number);
        return settled;
    }

    // What a kill that came while the tracker was told how a task went says, when it is told.
    void SettledAfterKill(int number)
    {
        left = $"#{number} was being settled, and the tracker was told how it went all the same";
        Say(RunMark.Interrupted, $"Killed while {left}");
    }

    // Checks what the session says against the working copy, as it was read before, and tells the tracker how the
    // task went; `tree` is null where it was not read or could not be. Every call of the tracker here is uncut.
    //
    // A task may be closed by then, as by a commit of its session with `Closes #12` in its message, and it is settled
    // all the same. `closed` says that it is known to be; otherwise the tracker is asked where it matters: a closed
    // task is not put back among those to do, so it gets the comment and neither a label nor a status.
    // `kill` is only what ends the pauses of its calls and keeps them from being made again.
    async Task<(RunMark Mark, RunResult? Ended)> ConcludeAsync(QueueTask task, IAssistantSession session, SessionLook look, WorkspaceState? tree, string took, bool closed, CancellationToken kill)
    {
        var number = task.Number;
        async Task<bool> Shut() => closed = closed || await ClosedAsync(number, kill);

        // A failure is for the owner: the record is forgotten, the task is left to them, and no run goes on with
        // it by itself.
        if (OutcomeOf(look) is not { } outcome)
        {
            state.Clear();
            Say(RunMark.Failed, $"#{number} {task.Title} — ended after {took} without saying how the task went");
            await LeaveAsync(number, session, "its session ended without saying how the task went", string.Join('\n', Said(look.Reply)), await Shut(), kill);
            return (RunMark.Failed, Stop($"the session of #{number} did not say how the task went · {session.Open}"));
        }

        if (outcome.Kind == OutcomeKind.Failed)
        {
            state.Clear();
            var reason = outcome.Reason ?? "no reason is given";
            Say(RunMark.Failed, $"#{number} {task.Title} — failed after {took}: {reason}");
            Quote(outcome.Report, session);
            await LeaveAsync(number, session, $"its session failed: {reason}", outcome.Report, await Shut(), kill);
            return (RunMark.Failed, Stop($"#{number} failed · {session.Open}"));
        }

        // The task has come to an outcome, and its record is forgotten before the tracker is told, so that nothing
        // is told twice. Only a task that was wrapped up keeps it: the next run goes on with the same conversation,
        // unless the task is closed, which no run goes on with.
        if (outcome.Kind == OutcomeKind.Interrupted && tree is { Changes: 0 } && !await Shut())
            state.Save(record = new TaskRecord(number, session.Trace, RecordState.WrappedUp));
        else
            state.Clear();

        // A check that does not pass leaves the task to the owner as a failure does, with what the session said.
        var wrong = tree is null ? "the working tree cannot be read after"
            : tree.Changes > 0 ? "left changes in the working tree"
            : outcome.Kind is OutcomeKind.Done or OutcomeKind.Partial && tree.Unpushed > 0 ? $"left {(tree.Unpushed == 1 ? "a commit that is" : $"{tree.Unpushed} commits that are")} not pushed"
            : outcome.Kind == OutcomeKind.Partial && !outcome.Names(number) ? "said that a part is done and named no task of what is left"
            : null;
        if (wrong is not null)
        {
            await LeaveAsync(number, session, tree is null ? "the working tree could not be read after its session" : $"its session {wrong}", outcome.Report, await Shut(), kill);
            return (RunMark.Failed, Stop(tree is null ? $"{wrong} #{number} · {session.Open}" : $"#{number} {wrong} · {session.Open}"));
        }

        try
        {
            return await TellAsync(task, session, outcome, took, outcome.Kind is OutcomeKind.Done or OutcomeKind.Partial ? closed : await Shut(), kill);
        }
        catch (TrackerException refused) when (outcome.Kind != OutcomeKind.Interrupted || closed)
        {
            // The record is forgotten, so nothing but the manual label keeps the next run from the task. A
            // task that was wrapped up has its record, and the next run goes on with its session.
            await LeaveAsync(number, session, $"the tracker refused something after its session: {refused.Message}", outcome.Report, await Shut(), kill);
            throw;
        }
    }

    // Whether the task is closed, for a task that is being settled. A tracker that does not say leaves it as one that
    // is open: what is told next is refused then, and says so.
    async Task<bool> ClosedAsync(int number, CancellationToken kill)
    {
        try
        {
            return await ClosedTaskAsync(number, kill, uncut: true) is not null;
        }
        catch (TrackerException)
        {
            return false;
        }
    }

    // Tells the tracker how a task went whose session said so and passed the checks. `closed` is a task that is
    // known to be closed already: it is not closed again, and it is not put back among those to do. A kill cuts none
    // of it, as none of what settles a task.
    async Task<(RunMark Mark, RunResult? Ended)> TellAsync(QueueTask task, IAssistantSession session, Outcome outcome, string took, bool closed, CancellationToken kill)
    {
        var number = task.Number;
        var rules = plan.Rules;
        var shut = $"#{number} was closed before its session ended, and is left closed";
        var commented = closed ? $"#{number} was closed already, and has the report of the session as its comment" : $"#{number} is closed, with the report of the session as its comment";
        switch (outcome.Kind)
        {
            case OutcomeKind.Done:
                await tracker.CommentAsync(number, Briefing.Comment(OutcomeKind.Done, outcome.Report, session.Open), CancellationToken.None);
                await CloseAsync(number, closed, kill);
                done++;
                Say(RunMark.Done, $"#{number} {task.Title} — done in {took}");
                Say(RunMark.Note, commented);
                return (RunMark.Done, null);

            case OutcomeKind.Partial:
                // What is left is a task that the session made and that its report names: the run makes nothing.
                await tracker.CommentAsync(number, Briefing.Comment(OutcomeKind.Partial, outcome.Report, session.Open), CancellationToken.None);
                await CloseAsync(number, closed, kill);
                partial++;
                Say(RunMark.Partial, $"#{number} {task.Title} — done in part in {took}");
                Say(RunMark.Note, $"{commented}; what is left is a task that the session made");
                return (RunMark.Partial, null);

            case OutcomeKind.Owner when closed:
                await tracker.CommentAsync(number, Briefing.Comment(OutcomeKind.Owner, outcome.Report, session.Open, closed: true), CancellationToken.None);
                Say(RunMark.Owner, $"#{number} {task.Title} — waits for the owner after {took}");
                Say(RunMark.Note, $"{shut}, with a comment that says what is needed; no run takes it until it is opened again");
                return (RunMark.Owner, null);

            case OutcomeKind.Owner:
                await LabelAsync(number, rules.Labels.Manual, kill, uncut: true);
                await tracker.CommentAsync(number, Briefing.Comment(OutcomeKind.Owner, outcome.Report, session.Open, label: rules.Labels.Manual), CancellationToken.None);
                await MoveAsync(number, BoardStatus.Todo, kill, uncut: true);
                Say(RunMark.Owner, $"#{number} {task.Title} — waits for the owner after {took}");
                Say(RunMark.Note, $"#{number} has the {rules.Labels.Manual} label and a comment that says what is needed; no run takes it while it has the label");
                return (RunMark.Owner, null);

            case OutcomeKind.Interrupted when closed:
                await tracker.CommentAsync(number, Briefing.Comment(OutcomeKind.Interrupted, outcome.Report, session.Open, closed: true), CancellationToken.None);
                Say(RunMark.Interrupted, $"#{number} {task.Title} — interrupted after {took}: the report is in the task · {shut}");
                Quote(outcome.Report, session);
                return (RunMark.Interrupted, aborting ? null : Stop($"#{number} was interrupted, and nobody asked for it · {session.Open}"));

            default:
                await LabelAsync(number, rules.Labels.Interrupted, kill, uncut: true);
                await tracker.CommentAsync(number, Briefing.Comment(OutcomeKind.Interrupted, outcome.Report, session.Open), CancellationToken.None);
                await MoveAsync(number, BoardStatus.Todo, kill, uncut: true);
                Say(RunMark.Interrupted, $"#{number} {task.Title} — interrupted after {took}: the report is in the task, and the next run takes it first");
                Quote(outcome.Report, session);
                return (RunMark.Interrupted, aborting ? null : Stop($"#{number} was interrupted, and nobody asked for it · {session.Open}"));
        }
    }

    // Leaves a task that the run stops over to the owner: it gets the manual label and a comment that says
    // what went wrong, and goes back among those to do, where the label holds it in a project with a board as in
    // one without. A tracker that refuses any of it is said, and changes nothing of why the run stops. A task that is
    // closed is left closed: it gets the comment only, and no run takes it until its owner opens it again. A kill cuts
    // none of it.
    async Task LeaveAsync(int number, IAssistantSession session, string why, string report, bool closed, CancellationToken kill)
    {
        var manual = plan.Rules.Labels.Manual;
        var missing = new List<string>();
        if (closed)
        {
            if (await Try(missing, () => tracker.CommentAsync(number, Briefing.Stopped(why, report, session.Open, manual, closed: true), CancellationToken.None)))
                Say(RunMark.Note, $"#{number} was closed before its session ended, and is left closed, with a comment that says what went wrong; no run takes it until it is opened again");
            else
                Say(RunMark.Attention, $"#{number} is closed and did not get the comment that says what went wrong: {missing[0]}");
            return;
        }

        // Without the label the status is all that holds the task, so it is moved only when the label is put.
        var labelled = await Try(missing, () => LabelAsync(number, manual, kill, uncut: true));
        await Try(missing, () => tracker.CommentAsync(number, Briefing.Stopped(why, report, session.Open, manual), CancellationToken.None));
        if (labelled)
        {
            await Try(missing, () => MoveAsync(number, BoardStatus.Todo, kill, uncut: true));
            Say(RunMark.Note, $"#{number} has the {manual} label{(missing.Count == 0 ? " and a comment that says what went wrong" : "")}; no run takes it while it has the label");
        }

        if (missing.Count > 0)
            Say(RunMark.Attention, $"#{number} is not left to the owner as it should be: {string.Join(" · ", missing)} · set it right by hand before the next run");
    }

    // A task that is done is closed, unless it is known to be closed already. Moving it on the board follows, and a
    // board that cannot take it there does not undo what is done: it is said, and the run goes on.
    async Task CloseAsync(int number, bool closed, CancellationToken kill)
    {
        if (!closed)
            await CloseTaskAsync(number, kill, uncut: true);
        try
        {
            await MoveAsync(number, BoardStatus.Done, kill, uncut: true);
        }
        catch (TrackerException refused)
        {
            Say(RunMark.Attention, refused.Message);
        }
    }

    // The calls of the tracker that are safe to make twice, as a run makes them: each is made again when GitHub did not
    // answer it, and the log says so in the words of what the call does. A comment is not among them: one whose answer
    // was lost may be written already, and a second would stand twice. An `uncut` call is one that a kill does not
    // cut, as what is done for good measure, what is told of a task that is settled or paused, or what is done after
    // the kill; its pauses are cut all the same.
    Task<IReadOnlyList<QueueTask>> OpenTasksAsync(CancellationToken kill) =>
        AgainAsync("the open tasks are read", calls => tracker.ReadOpenAsync(plan.Milestone, calls), kill);

    Task<QueueTask?> ClosedTaskAsync(int number, CancellationToken kill, bool uncut = false) =>
        AgainAsync($"#{number} is read", calls => tracker.ReadClosedAsync(number, calls), kill, uncut);

    Task MoveAsync(int number, BoardStatus to, CancellationToken kill, bool uncut = false) =>
        AgainAsync($"#{number} is moved to {plan.Rules.Statuses.NameOf(to)}", calls => tracker.SetStatusAsync(number, to, calls), kill, uncut);

    Task AssignAsync(int number, CancellationToken kill) =>
        AgainAsync($"#{number} is assigned to the current user", calls => tracker.AssignAsync(number, calls), kill);

    Task LabelAsync(int number, string label, CancellationToken kill, bool uncut = false) =>
        AgainAsync($"the label {label} is put on #{number}", calls => tracker.AddLabelAsync(number, label, calls), kill, uncut);

    Task UnlabelAsync(int number, string label, CancellationToken kill) =>
        AgainAsync($"the label {label} is taken off #{number}", calls => tracker.RemoveLabelAsync(number, label, calls), kill);

    Task CloseTaskAsync(int number, CancellationToken kill, bool uncut = false) =>
        AgainAsync($"#{number} is closed", calls => tracker.CloseAsync(number, calls), kill, uncut);

    // Makes a call that is safe to make twice, and makes it again when it failed in a way that may pass, after each
    // pause of the policy: the failure of the last try is the one that stands. `what` says what the call does, and
    // `call` makes it with the token it is cut by: none for one that is `uncut`. A kill ends a pause at once, and after
    // a kill nothing is made again: the owner waits for what is done then. An uncut call whose pause the kill ends
    // fails with the failure it had.
    async Task<T> AgainAsync<T>(string what, Func<CancellationToken, Task<T>> call, CancellationToken kill, bool uncut = false)
    {
        for (var tried = 0; ; tried++)
        {
            TrackerException failed;
            try
            {
                return await call(uncut ? CancellationToken.None : kill);
            }
            catch (TrackerException passing) when (passing.MayPass && tried < policy.RetryPauses.Count && !kill.IsCancellationRequested)
            {
                failed = passing;
            }

            var pause = policy.RetryPauses[tried];
            Say(RunMark.Attention, $"GitHub did not answer: {what} again in {Spoken.Time(pause)}");
            try
            {
                await clock.DelayAsync(pause, kill);
            }
            catch (OperationCanceledException) when (uncut && kill.IsCancellationRequested)
            {
                throw failed;
            }
        }
    }

    async Task AgainAsync(string what, Func<CancellationToken, Task> call, CancellationToken kill, bool uncut = false) =>
        await AgainAsync(what, async calls =>
        {
            await call(calls);
            return true;
        }, kill, uncut);

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

    // What a session said, line by line, without the empty lines and without the line that is for toobusy.
    static List<string> Said(string? reply) => [.. Lines(reply ?? "").Where(line => !line.Trim().StartsWith(Outcome.Mark, StringComparison.OrdinalIgnoreCase))];

    // The lines of a text that are not empty, without what ends them.
    static List<string> Lines(string text) => [.. text.Split('\n').Select(line => line.TrimEnd()).Where(line => line.Length > 0)];

    // What a session said, in the log: its first lines, and where the rest is.
    void Quote(string? reply, IAssistantSession session)
    {
        var lines = Said(reply);
        foreach (var line in lines.Take(Quoted))
            Say(RunMark.Note, $"│ {line}");
        if (lines.Count > Quoted)
            Say(RunMark.Note, $"│ … {session.Open} shows the whole session");
    }

    // `detail` is what the log on disk keeps of the problem beyond its line: the whole of an error nobody foresaw.
    RunResult Stop(string problem, string? detail = null)
    {
        view.Say(new RunLine(RunMark.Failed, $"Stopped: {problem} · the next task did not start · {Went()}") { Detail = detail });
        machine.Notify($"Stopped: {problem}");
        return Ended(RunEnd.Problem, problem);
    }

    // What nobody foresaw, in a line: its kind and its words, for a report of the bug.
    static string Unforeseen(Exception failed) => $"an error toobusy did not foresee: {failed.GetType().Name}: {string.Join(' ', Lines(failed.Message))}";

    // How the session of the task was seen last, in the words of the log on disk; null before its first look.
    string? seen;

    // Writes into the log on disk how the session of a task is seen, when that changed since its last look: what it
    // is doing, and what it waits for, the limit it ran into or what the assistant cannot make of it. The screen does
    // not show it.
    void Seen(int number, IAssistantSession session, SessionLook look)
    {
        var now = look.Phase.ToString().ToLowerInvariant() + ((look.Asks ?? look.Limit ?? look.Odd) is { } why ? $" ({string.Join(' ', Lines(why))})" : "");
        if (now == seen)
            return;

        var trace = session.Trace;
        view.Trace($"#{number} session {trace.Id ?? trace.Conversation}: {(seen is null ? now : $"{seen} → {now}")}");
        seen = now;
    }

    // How a session that went on as a copy is said to go on: the copy is named after the session it was of.
    static string AsCopy(string? former) => former is null ? "as a copy of its session" : $"as a copy of {former}";

    // A session that went on as a copy in the middle of its task is noted: it is opened by another name from now on.
    void NoteCopy(int number, string? former, IAssistantSession session) =>
        Say(RunMark.Note, $"#{number} goes on {AsCopy(former)}: Claude Code started a new session with the whole conversation · {session.Open}");

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

    static string Limit(UsageWindow window) => $"the {window.Name} limit";

    static string Reset(UsageWindow window) =>
        window.ResetsAt is { } reset ? $", it resets {Spoken.Moment(reset)}" : "";

    // A change of the tracker that a refusal does not stop: what was refused is added to `missing`, and the change
    // counts as not made. True when it was made.
    static async Task<bool> Try(List<string> missing, Func<Task> change)
    {
        try
        {
            await change();
            return true;
        }
        catch (TrackerException refused)
        {
            missing.Add(refused.Message);
            return false;
        }
    }

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
