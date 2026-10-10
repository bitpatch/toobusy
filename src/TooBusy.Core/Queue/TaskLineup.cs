using TooBusy.Core.Settings;

namespace TooBusy.Core.Queue;

// The rules a task is taken by: the labels of the settings, and whether the project has a board, on which a task
// must stand as one to do.
public sealed record QueueRules(IReadOnlyList<string> Blocking, IReadOnlyList<string> Take, string Owner, string Interrupted, bool Board)
{
    public static QueueRules Of(ProjectSettings settings) => new(
        settings.Queue.Labels.Blocking, settings.Queue.Labels.Take, settings.Queue.Labels.Owner, settings.Queue.Labels.Interrupted, settings.Tracker.Board is not null);
}

// A task that opens once the tasks before it are closed.
public sealed record LaterTask(QueueTask Task, IReadOnlyList<int> After);

// A task that no run takes as things are, and why.
public sealed record HeldTask(QueueTask Task, string Reason);

// The open tasks as a run sees them. Ready can be taken now, in this order: those that were interrupted first, then
// by their numbers. Later open as the tasks before them are closed, in the order they will. Held are the rest.
public sealed record TaskLineup(IReadOnlyList<QueueTask> Ready, IReadOnlyList<LaterTask> Later, IReadOnlyList<HeldTask> Held)
{
    public static TaskLineup Arrange(IReadOnlyList<QueueTask> open, QueueRules rules)
    {
        var refused = open.Select(task => (Task: task, Reason: Refusal(task, rules))).Where(pair => pair.Reason is not null).ToDictionary(pair => pair.Task.Number, pair => pair.Reason!);
        var taken = open.Where(task => !refused.ContainsKey(task.Number))
            .OrderByDescending(task => task.Has(rules.Interrupted)).ThenBy(task => task.Number).ToList();

        // The tasks are closed one after another as a run would close them, and what waits for them opens.
        var done = new HashSet<int>();
        var later = new List<LaterTask>();
        var waiting = taken.Where(task => Before(task).Count > 0).ToList();
        done.UnionWith(taken.Where(task => Before(task).Count == 0).Select(task => task.Number));
        while (waiting.Find(task => Before(task).All(done.Contains)) is { } opened)
        {
            later.Add(new LaterTask(opened, Before(opened)));
            done.Add(opened.Number);
            waiting.Remove(opened);
        }

        var numbers = open.Select(task => task.Number).ToHashSet();
        var held = open.Where(task => refused.ContainsKey(task.Number)).Select(task => new HeldTask(task, refused[task.Number]))
            .Concat(waiting.Select(task =>
            {
                var blocker = Before(task).First(number => !done.Contains(number));
                var why = refused.TryGetValue(blocker, out var reason) ? $" ({reason})" : numbers.Contains(blocker) ? "" : " (not among these tasks)";
                return new HeldTask(task, $"it waits for #{blocker}{why}");
            }))
            .OrderBy(task => task.Task.Number);

        return new TaskLineup([.. taken.Where(task => Before(task).Count == 0)], later, [.. held]);
    }

    static List<int> Before(QueueTask task) => [.. task.BlockedBy.Concat(task.Parts).Distinct().Order()];

    // Why the task is not taken whatever closes before it; null when nothing speaks against it.
    static string? Refusal(QueueTask task, QueueRules rules)
    {
        if (rules.Blocking.FirstOrDefault(task.Has) is { } blocking)
            return $"it has the {blocking} label";
        if (task.Has(rules.Owner))
            return $"it waits for the owner: it has the {rules.Owner} label";
        if (rules.Take.Count > 0 && !rules.Take.Any(task.Has))
            return $"it has none of the labels to take: {string.Join(", ", rules.Take)}";
        if (!rules.Board || task.Status == BoardStatus.Todo)
            return null;
        return task.Status == BoardStatus.Missing ? "it is not on the board" : $"its status is {task.StatusName}";
    }
}
