namespace TooBusy.Core.Queue;

// What the board of a project calls the three statuses a run knows: the one it takes a task from, and the two it
// moves a task to. The settings name them; a name they do not give is the one GitHub gives the status of a new
// board. A name is the same as one of the board whatever the case of its letters and whatever stands between its
// letters and digits.
public sealed record BoardStatuses(string Todo, string InProgress, string Done)
{
    // The names GitHub gives the statuses of a new board.
    public static BoardStatuses Default { get; } = new("Todo", "In Progress", "Done");

    // The three names, in the order a task goes through them.
    public IReadOnlyList<string> Names => [Todo, InProgress, Done];

    // A status by what a board calls it; an empty name is that of a task that has none.
    public BoardStatus Of(string name) =>
        name.Length == 0 ? BoardStatus.Missing
        : Same(name, Todo) ? BoardStatus.Todo
        : Same(name, InProgress) ? BoardStatus.InProgress
        : Same(name, Done) ? BoardStatus.Done
        : BoardStatus.Other;

    public string NameOf(BoardStatus status) => status switch
    {
        BoardStatus.Todo => Todo,
        BoardStatus.InProgress => InProgress,
        BoardStatus.Done => Done,
        _ => status.ToString(),
    };

    // The names of the three that are not among the statuses of a board.
    public IReadOnlyList<string> Missing(IReadOnlyList<string> has) => [.. Names.Where(name => !has.Any(one => Same(one, name)))];

    // What is wrong with a board that has these statuses, with what it has instead; null for a board that has
    // the three.
    public string? Lack(IReadOnlyList<string> has) => Missing(has) switch
    {
        [] => null,
        var missing => $"The board has no {(missing.Count == 1 ? "status" : "statuses")} {string.Join(", ", missing.Select(name => $"“{name}”"))}: "
            + (has.Count == 0 ? "it has no statuses." : $"it has {string.Join(", ", has)}."),
    };

    // Whether two names are of one status.
    public static bool Same(string one, string other) => Key(one) == Key(other);

    // A name without what does not tell it from another: its letters and digits, in lower case. A name that has
    // neither is told by all of it.
    static string Key(string name) => new string([.. name.Where(char.IsLetterOrDigit).Select(char.ToLowerInvariant)]) is { Length: > 0 } key ? key : name.Trim();
}
