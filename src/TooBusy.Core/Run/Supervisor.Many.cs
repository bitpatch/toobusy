using TooBusy.Core.Queue;

namespace TooBusy.Core.Run;

// A run of several sessions: as many tasks as the plan says go on side by side, each on a flow of its own in a git
// worktree of its own, and the queue is read anew whenever a flow is over. The branch of the tasks stays where it is:
// a worktree stands on a branch of its own that follows it on the remote, and its session pushes there. The working copy
// the run was started in is left alone.
//
// What a run of one session stops over stops this one too, in that it takes no other task: the sessions that work
// beside it go on to their end in their own worktrees, as no task of theirs is built on what went wrong, and the run
// ends when they are over, with the first problem as what ended it. Nor is a task started while one waits for a usage
// limit, which is that of the account. A kill goes to all of them, and `/stop` and `/abort` too.
public sealed partial class Supervisor
{
    // The flows of the tasks that are worked on, in the order they were taken.
    readonly List<Flow> going = [];

    // How many tasks follow those that are worked on, as the queue was read last.
    int ahead;

    // Whether something stopped the run while tasks go on beside it.
    bool halted;

    // Goes on with what earlier runs left in worktrees and, in a run of several sessions, takes the tasks of the queue.
    // Null in a run of one session, when the worktrees are done with or there were none: it goes on in the working copy.
    async Task<RunResult?> ManyAsync(CancellationToken kill)
    {
        var left = await copies!.FindAsync(kill);
        if (plan.Sessions <= 1 && left.Count == 0)
            return null;

        Show(Main, new RunStatus(RunPhase.Preparing, "Checking the working copies…"));
        var tree = plan.Sessions > 1 ? await workspace.ReadAsync(kill) : null;
        Upstream? onto = null;
        if (plan.Sessions > 1)
        {
            if (tree is null)
                return Stop("the working tree cannot be read");
            if ((plan.Branch ?? tree.Branch) is not { } branch)
                return Stop("a run of several sessions starts from a branch, and the working copy is on none");
            if (await copies.FollowedAsync(branch, kill) is not { } followed)
                return Stop($"the sessions of a run of several push to the remote, and {branch} has no upstream");

            onto = followed;
        }

        // The working copy has a record too where a run of one session left one. A record keeps its worktree, and the
        // others are what tasks that are over left.
        List<(WorkingCopy Copy, TaskRecord Record)> recorded = [];
        foreach (var copy in (WorkingCopy[])[.. plan.Sessions > 1 ? [home] : (WorkingCopy[])[], .. left])
        {
            if (copy.State.Load() is { } record)
                recorded.Add((copy, record));
            else
                await TidyAsync(copy);
        }

        if (recorded.Any(entry => entry.Copy == home))
        {
            if (plan.Branch is { } branch && tree!.Branch != branch)
                return Stop($"the working copy is on {tree.Branch ?? "no branch"}, and the tasks are done on {branch}");
        }

        var gone = new HashSet<int>();
        var lefts = new List<string>();
        RunResult? ended = null;
        while (true)
        {
            Drain();
            foreach (var flow in going.Where(flow => flow.Doing.IsCompleted).ToList())
            {
                var result = await flow.Doing;
                if (flow.Left is { } said)
                    lefts.Add(said);

                lock (gate)
                {
                    going.Remove(flow);
                    if (result is null)
                        taken++;
                    else if (ended is null || (result.End == RunEnd.Problem && ended.End != RunEnd.Problem))
                        (ended, halted) = (result, true);
                    Publish();
                }
            }

            if (ended is null && !kill.IsCancellationRequested && !stopping && Asked() is null)
                ended = await GuardedAsync(() => TakeMoreAsync(recorded, gone, onto, kill), Main, kill);

            if (going.Count == 0)
                break;

            await Task.WhenAny(going.Select(flow => flow.Doing));
        }

        Main.Left = lefts.Count > 0 ? string.Join(" · ", lefts.Distinct()) : Main.Left;
        if (ended is not null)
            return ended with { Done = done, Partial = partial, Why = ended.End == RunEnd.Problem ? ended.Why : Main.Left ?? ended.Why };
        return onto is null && !kill.IsCancellationRequested ? null : Wanted(kill);
    }

    // How many more tasks may be started now.
    int Room()
    {
        var room = Math.Max(1, plan.Sessions) - going.Count;
        if (plan.Only is not null)
            room = Math.Min(room, 1 - taken - going.Count);
        if (plan.Most is { } most)
            room = Math.Min(room, most - taken - going.Count);
        return going.Any(flow => flow.Status.Phase == RunPhase.WaitingForLimit) ? 0 : room;
    }

    // Reads the queue and starts as many tasks as there is room for, the tasks of the records first. `onto` is what a
    // task that is taken anew lands on, null in a run that only goes on with its records. What it gives ends the run:
    // that no task is left, once nothing works any more, or that one could not be taken.
    async Task<RunResult?> TakeMoreAsync(List<(WorkingCopy Copy, TaskRecord Record)> recorded, HashSet<int> gone, Upstream? onto, CancellationToken kill)
    {
        var room = Room();
        if (room <= 0 || (onto is null && recorded.Count == 0))
            return null;

        if (going.Count == 0)
            Show(Main, new RunStatus(RunPhase.Preparing, "Reading the queue…"));
        var open = await Calls.OpenTasksAsync(kill);
        var lineup = TaskLineup.Arrange(open, plan.Rules);

        // A task of a record that is not gone on with is settled or forgotten as in a run of one session, and the
        // queue is read anew after it.
        foreach (var (copy, record) in recorded)
        {
            var (kept, why) = TaskLineup.Recorded(open, plan.Rules, record.Number);
            if (kept is not null)
                continue;

            recorded.Remove((copy, record));
            if (await copy.Workspace.ReadAsync(kill) is not { } tree)
                return Stop($"the working tree of #{record.Number} cannot be read");
            if (await UnkeptAsync(copy, record, why!, open, lineup, tree, kill) is { } over)
                return over;

            await TidyAsync(copy);
            return await TakeMoreAsync(recorded, gone, onto, kill);
        }

        foreach (var (_, record) in Enumerable.Reverse(recorded))
            lineup = lineup.Taking(TaskLineup.Recorded(open, plan.Rules, record.Number).Task!);

        if (plan.Only is { } only)
        {
            if (recorded.Find(entry => entry.Record.Number != only) is { Record: { } other })
                return Refuse(only, $"an earlier run left #{other.Number} with a session, and a run goes on with that task before any other");
            if (lineup.Ready.FirstOrDefault(ready => ready.Number == only) is not { } one)
                return Refuse(only, lineup.Holds(only) ?? (plan.Milestone is null ? "it is not among the open tasks" : $"it is not among the open tasks of the milestone {plan.Milestone}"));

            lineup = TaskLineup.Of(one);
        }

        if (first)
            Head(lineup);

        var working = going.Select(flow => flow.Number).ToHashSet();
        List<QueueTask> next = [.. lineup.Ready.Where(task => !working.Contains(task.Number) && (onto is not null || recorded.Exists(entry => entry.Record.Number == task.Number)))];
        if (next.Count == 0)
        {
            if (going.Count > 0 || onto is null)
                return null;
            if (!first && lineup.Held.Count > 0)
                Say(RunMark.Note, Held(lineup));
            return NoneLeft();
        }

        first = false;
        ReadLimits();
        var queued = lineup.Ready.Count + lineup.Later.Count - working.Count;
        foreach (var task in next.Take(room))
        {
            if (!gone.Add(task.Number))
                return Stop($"#{task.Number} came up again after its session");

            queued--;
            var (copy, record) = recorded.Find(entry => entry.Record.Number == task.Number);
            recorded.RemoveAll(entry => entry.Record.Number == task.Number);
            Begin(task, copy, record, onto, plan.Most is { } most ? Math.Min(queued, most - taken - going.Count - 1) : queued, kill);
        }

        ahead = plan.Most is { } few ? Math.Min(queued, few - taken - going.Count) : queued;
        return null;
    }

    // Starts a task on a flow of its own: in the working copy of its record, or in a new worktree.
    void Begin(QueueTask task, WorkingCopy? copy, TaskRecord? before, Upstream? onto, int queued, CancellationToken kill)
    {
        var flow = new Flow(this) { Number = task.Number, Status = new RunStatus(RunPhase.Preparing, $"Starting #{task.Number}…") { Task = task, Since = clock.Now, Queued = queued } };
        lock (gate)
        {
            going.Add(flow);
            Publish();
        }

        flow.Doing = FlowAsync(flow, task, copy, before, onto, queued, kill);
    }

    // What one task of the run comes to; null when the run goes on. Its worktree is seen to whatever became of it.
    async Task<RunResult?> FlowAsync(Flow flow, QueueTask task, WorkingCopy? copy, TaskRecord? before, Upstream? onto, int queued, CancellationToken kill)
    {
        var ended = await GuardedAsync(
            async () =>
            {
                if (copy is null)
                {
                    flow.Show(flow.Status with { Step = "making its worktree" });
                    copy = await copies!.MakeAsync(task.Number, onto!, kill);
                }

                return await NewTask(flow, copy, task, queued).TakeAsync(before, kill);
            },
            flow,
            kill);
        if (copy is not null)
            await TidyAsync(copy);
        return ended;
    }

    // Sees to a worktree that nothing goes on in: one with a record stays for the next run, one whose work is all on the
    // remote is taken away, and one with work of a session that is not is left to the owner. The working copy itself is
    // never touched.
    async Task TidyAsync(WorkingCopy copy)
    {
        if (copy.Landing is null)
            return;

        if (copy.State.Load() is { } record)
        {
            Say(RunMark.Note, $"#{record.Number} stays in its worktree for the next run: {copy.Folder}");
            return;
        }

        if (await copy.Workspace.ReadAsync(CancellationToken.None) is not { Changes: 0, Unpushed: 0 })
        {
            Say(RunMark.Attention, $"A worktree is left with work that is not pushed: {copy.Folder} · look at it, and take it away with `git worktree remove --force`");
            return;
        }

        try
        {
            await copies!.RemoveAsync(copy, CancellationToken.None);
        }
        catch (LocalStateException kept)
        {
            Say(RunMark.Attention, kept.Message);
        }
    }
}
