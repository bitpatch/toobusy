using TooBusy.Core.Queue;

namespace TooBusy.Core.Run;

// The end of a task for a run: what its session said is checked against the working copy, and the tracker is told how
// the task went.
sealed class TaskSettlement(QueueTask task, IAssistantSession session, DateTimeOffset since, ITaskHost host, TaskTools tools)
{
    readonly int number = task.Number;
    readonly RunTracker calls = tools.Calls;
    readonly IWorkspace workspace = tools.Workspace;
    readonly IRunState state = tools.State;
    readonly IClock clock = tools.Clock;
    readonly RunPlan plan = tools.Plan;

    // Settles a task whose session came to its end: the session is stopped, what it says is checked against the
    // working copy, and the tracker is told how the task went. Gives the mark of how it went, and how the run ends with
    // it: null when the run goes on to the next task.
    //
    // Until the record is forgotten a kill leaves the task as it is, and the next run settles it from the record. From
    // then on the kill cuts nothing: a task whose record is gone has nothing but the tracker to keep it, so the tracker
    // is told to the end, each call that is left once, and the run ends when the task is over.
    public async Task<(RunMark Mark, RunResult? Ended)> SettleAsync(SessionLook look, CancellationToken kill, bool closed = false)
    {
        WorkspaceState? tree = null;
        try
        {
            await host.StopAsync(number, session, kill);
            kill.ThrowIfCancellationRequested();
            if (Outcome.Of(look) is { Kind: not OutcomeKind.Failed })
                tree = await workspace.ReadAsync(kill);
        }
        catch (OperationCanceledException) when (kill.IsCancellationRequested)
        {
            await host.StopAsync(number, session, CancellationToken.None);
            host.Left = $"#{number} was being settled, and the next run settles it · {session.Open}";
            Say(RunMark.Interrupted, $"Killed while #{number} was being settled: nothing of how it went is told yet, and the next run settles it · {session.Open}");
            throw;
        }

        (RunMark Mark, RunResult? Ended) settled;
        try
        {
            settled = await ConcludeAsync(look, tree, Spoken.Time(clock.Now - since), closed, kill);
        }
        catch (TrackerException) when (kill.IsCancellationRequested)
        {
            SettledAfterKill();
            throw;
        }

        if (kill.IsCancellationRequested)
            SettledAfterKill();
        return settled;
    }

    // What a kill that came while the tracker was told how a task went says, when it is told.
    void SettledAfterKill()
    {
        host.Left = $"#{number} was being settled, and the tracker was told how it went all the same";
        Say(RunMark.Interrupted, $"Killed while {host.Left}");
    }

    // Checks what the session says against the working copy, as it was read before, and tells the tracker how the
    // task went; `tree` is null where it was not read or could not be. Every call of the tracker here is uncut.
    //
    // A task may be closed by then, as by a commit of its session with `Closes #12` in its message, and it is settled
    // all the same. `closed` says that it is known to be; otherwise the tracker is asked where it matters: a closed
    // task is not put back among those to do, so it gets the comment and neither a label nor a status.
    // `kill` is only what ends the pauses of its calls and keeps them from being made again.
    async Task<(RunMark Mark, RunResult? Ended)> ConcludeAsync(SessionLook look, WorkspaceState? tree, string took, bool closed, CancellationToken kill)
    {
        async Task<bool> Shut() => closed = closed || await ClosedAsync(kill);

        // A failure is for the owner: the record is forgotten, the task is left to them, and no run goes on with
        // it by itself.
        if (Outcome.Of(look) is not { } outcome)
        {
            state.Clear();
            Say(RunMark.Failed, $"#{number} {task.Title} — ended after {took} without saying how the task went");
            await LeaveAsync("its session ended without saying how the task went", string.Join('\n', SessionWords.Said(look.Reply)), await Shut(), kill);
            return (RunMark.Failed, host.Stop($"the session of #{number} did not say how the task went · {session.Open}"));
        }

        if (outcome.Kind == OutcomeKind.Failed)
        {
            state.Clear();
            var reason = outcome.Reason ?? "no reason is given";
            Say(RunMark.Failed, $"#{number} {task.Title} — failed after {took}: {reason}");
            SessionWords.Quote(host, outcome.Report, session);
            await LeaveAsync($"its session failed: {reason}", outcome.Report, await Shut(), kill);
            return (RunMark.Failed, host.Stop($"#{number} failed · {session.Open}"));
        }

        // The task has come to an outcome, and its record is forgotten before the tracker is told, so that nothing
        // is told twice. Only a task that was wrapped up keeps it: the next run goes on with the same conversation,
        // unless the task is closed, which no run goes on with.
        if (outcome.Kind == OutcomeKind.Interrupted && tree is { Changes: 0 } && !await Shut())
            state.Save(new TaskRecord(number, session.Trace, RecordState.WrappedUp));
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
            await LeaveAsync(tree is null ? "the working tree could not be read after its session" : $"its session {wrong}", outcome.Report, await Shut(), kill);
            return (RunMark.Failed, host.Stop(tree is null ? $"{wrong} #{number} · {session.Open}" : $"#{number} {wrong} · {session.Open}"));
        }

        try
        {
            return await TellAsync(outcome, took, outcome.Kind is OutcomeKind.Done or OutcomeKind.Partial ? closed : await Shut(), kill);
        }
        catch (TrackerException refused) when (outcome.Kind != OutcomeKind.Interrupted || closed)
        {
            // The record is forgotten, so nothing but the manual label keeps the next run from the task. A
            // task that was wrapped up has its record, and the next run goes on with its session.
            await LeaveAsync($"the tracker refused something after its session: {refused.Message}", outcome.Report, await Shut(), kill);
            throw;
        }
    }

    // Whether the task is closed, for a task that is being settled. A tracker that does not say leaves it as one that
    // is open: what is told next is refused then, and says so.
    async Task<bool> ClosedAsync(CancellationToken kill)
    {
        try
        {
            return await calls.ClosedTaskAsync(number, kill, uncut: true) is not null;
        }
        catch (TrackerException)
        {
            return false;
        }
    }

    // Tells the tracker how a task went whose session said so and passed the checks. `closed` is a task that is
    // known to be closed already: it is not closed again, and it is not put back among those to do. A kill cuts none
    // of it, as none of what settles a task.
    async Task<(RunMark Mark, RunResult? Ended)> TellAsync(Outcome outcome, string took, bool closed, CancellationToken kill)
    {
        var rules = plan.Rules;
        var shut = $"#{number} was closed before its session ended, and is left closed";
        var commented = closed ? $"#{number} was closed already, and has the report of the session as its comment" : $"#{number} is closed, with the report of the session as its comment";
        switch (outcome.Kind)
        {
            case OutcomeKind.Done:
                await calls.CommentAsync(number, Briefing.Comment(OutcomeKind.Done, outcome.Report, session.Open));
                await CloseAsync(closed, kill);
                host.Count(OutcomeKind.Done);
                Say(RunMark.Done, $"#{number} {task.Title} — done in {took}");
                Say(RunMark.Note, commented);
                return (RunMark.Done, null);

            case OutcomeKind.Partial:
                // What is left is a task that the session made and that its report names: the run makes nothing.
                await calls.CommentAsync(number, Briefing.Comment(OutcomeKind.Partial, outcome.Report, session.Open));
                await CloseAsync(closed, kill);
                host.Count(OutcomeKind.Partial);
                Say(RunMark.Partial, $"#{number} {task.Title} — done in part in {took}");
                Say(RunMark.Note, $"{commented}; what is left is a task that the session made");
                return (RunMark.Partial, null);

            case OutcomeKind.Owner when closed:
                await calls.CommentAsync(number, Briefing.Comment(OutcomeKind.Owner, outcome.Report, session.Open, closed: true));
                Say(RunMark.Owner, $"#{number} {task.Title} — waits for the owner after {took}");
                Say(RunMark.Note, $"{shut}, with a comment that says what is needed; no run takes it until it is opened again");
                return (RunMark.Owner, null);

            case OutcomeKind.Owner:
                await calls.LabelAsync(number, rules.Labels.Manual, kill, uncut: true);
                await calls.CommentAsync(number, Briefing.Comment(OutcomeKind.Owner, outcome.Report, session.Open, label: rules.Labels.Manual));
                await calls.MoveAsync(number, BoardStatus.Todo, kill, uncut: true);
                Say(RunMark.Owner, $"#{number} {task.Title} — waits for the owner after {took}");
                Say(RunMark.Note, $"#{number} has the {rules.Labels.Manual} label and a comment that says what is needed; no run takes it while it has the label");
                return (RunMark.Owner, null);

            case OutcomeKind.Interrupted when closed:
                await calls.CommentAsync(number, Briefing.Comment(OutcomeKind.Interrupted, outcome.Report, session.Open, closed: true));
                Say(RunMark.Interrupted, $"#{number} {task.Title} — interrupted after {took}: the report is in the task · {shut}");
                SessionWords.Quote(host, outcome.Report, session);
                return (RunMark.Interrupted, host.Aborting ? null : host.Stop($"#{number} was interrupted, and nobody asked for it · {session.Open}"));

            default:
                await calls.LabelAsync(number, rules.Labels.Interrupted, kill, uncut: true);
                await calls.CommentAsync(number, Briefing.Comment(OutcomeKind.Interrupted, outcome.Report, session.Open));
                await calls.MoveAsync(number, BoardStatus.Todo, kill, uncut: true);
                Say(RunMark.Interrupted, $"#{number} {task.Title} — interrupted after {took}: the report is in the task, and the next run takes it first");
                SessionWords.Quote(host, outcome.Report, session);
                return (RunMark.Interrupted, host.Aborting ? null : host.Stop($"#{number} was interrupted, and nobody asked for it · {session.Open}"));
        }
    }

    // Leaves a task that the run stops over to the owner: it gets the manual label and a comment that says
    // what went wrong, and goes back among those to do, where the label holds it in a project with a board as in
    // one without. A tracker that refuses any of it is said, and changes nothing of why the run stops. A task that is
    // closed is left closed: it gets the comment only, and no run takes it until its owner opens it again. A kill cuts
    // none of it.
    async Task LeaveAsync(string why, string report, bool closed, CancellationToken kill)
    {
        var manual = plan.Rules.Labels.Manual;
        var missing = new List<string>();
        if (closed)
        {
            if (await RunTracker.Try(missing, () => calls.CommentAsync(number, Briefing.Stopped(why, report, session.Open, manual, closed: true))))
                Say(RunMark.Note, $"#{number} was closed before its session ended, and is left closed, with a comment that says what went wrong; no run takes it until it is opened again");
            else
                Say(RunMark.Attention, $"#{number} is closed and did not get the comment that says what went wrong: {missing[0]}");
            return;
        }

        // Without the label the status is all that holds the task, so it is moved only when the label is put.
        var labelled = await RunTracker.Try(missing, () => calls.LabelAsync(number, manual, kill, uncut: true));
        await RunTracker.Try(missing, () => calls.CommentAsync(number, Briefing.Stopped(why, report, session.Open, manual)));
        if (labelled)
        {
            await RunTracker.Try(missing, () => calls.MoveAsync(number, BoardStatus.Todo, kill, uncut: true));
            Say(RunMark.Note, $"#{number} has the {manual} label{(missing.Count == 0 ? " and a comment that says what went wrong" : "")}; no run takes it while it has the label");
        }

        if (missing.Count > 0)
            Say(RunMark.Attention, $"#{number} is not left to the owner as it should be: {string.Join(" · ", missing)} · set it right by hand before the next run");
    }

    // A task that is done is closed, unless it is known to be closed already. Moving it on the board follows, and a
    // board that cannot take it there does not undo what is done: it is said, and the run goes on.
    async Task CloseAsync(bool closed, CancellationToken kill)
    {
        if (!closed)
            await calls.CloseTaskAsync(number, kill, uncut: true);
        try
        {
            await calls.MoveAsync(number, BoardStatus.Done, kill, uncut: true);
        }
        catch (TrackerException refused)
        {
            Say(RunMark.Attention, refused.Message);
        }
    }

    void Say(RunMark mark, string text) => host.Say(mark, text);
}
