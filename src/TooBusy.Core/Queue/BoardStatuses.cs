namespace TooBusy.Core.Queue;

// The statuses of a board as a run knows them: by the names GitHub gives the statuses of a new board, Todo,
// In Progress and Done, whatever the case of the letters and whatever stands between them.
public static class BoardStatuses
{
    // The statuses a run takes a task from and moves it to, in the order a task goes through them.
    static readonly BoardStatus[] Known = [BoardStatus.Todo, BoardStatus.InProgress, BoardStatus.Done];

    // A status by what a board calls it; an empty name is that of a task that has none.
    public static BoardStatus Of(string name) => new string([.. name.Where(char.IsLetter).Select(char.ToLowerInvariant)]) switch
    {
        "" => BoardStatus.Missing,
        "todo" => BoardStatus.Todo,
        "inprogress" => BoardStatus.InProgress,
        "done" => BoardStatus.Done,
        _ => BoardStatus.Other,
    };

    public static string NameOf(BoardStatus status) => status switch
    {
        BoardStatus.Todo => "Todo",
        BoardStatus.InProgress => "In Progress",
        BoardStatus.Done => "Done",
        _ => status.ToString(),
    };

    // The statuses a run knows that are not among those of a board, by their names.
    public static IReadOnlyList<string> Missing(IReadOnlyList<string> has) =>
        [.. Known.Where(status => has.All(name => Of(name) != status)).Select(NameOf)];

    // What is wrong with a board that has these statuses, with what it has instead; null for a board that has
    // the three.
    public static string? Lack(IReadOnlyList<string> has) => Missing(has) switch
    {
        [] => null,
        var missing => $"The board has no {(missing.Count == 1 ? "status" : "statuses")} {string.Join(", ", missing.Select(name => $"“{name}”"))}: "
            + (has.Count == 0 ? "it has no statuses." : $"it has {string.Join(", ", has)}."),
    };
}
