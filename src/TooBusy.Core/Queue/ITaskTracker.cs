namespace TooBusy.Core.Queue;

// The tasks of a project in its tracker: reading them for the queue and changing them as a run goes. It is a run
// that changes the tracker, never the assistant. Everything throws TrackerException when the tracker refuses or
// does not answer; the message says why, in words for the user.
public interface ITaskTracker
{
    // The open tasks of the milestone, or of the whole project when the milestone is null.
    Task<IReadOnlyList<QueueTask>> ReadOpenAsync(string? milestone, CancellationToken cancellationToken);

    Task<TaskText> ReadTextAsync(int number, CancellationToken cancellationToken);

    // Moves the task on the board, putting it there when it is not; does nothing in a project without a board.
    Task SetStatusAsync(int number, BoardStatus status, CancellationToken cancellationToken);

    Task AddLabelAsync(int number, string label, CancellationToken cancellationToken);

    Task RemoveLabelAsync(int number, string label, CancellationToken cancellationToken);

    Task CommentAsync(int number, string text, CancellationToken cancellationToken);

    Task CloseAsync(int number, CancellationToken cancellationToken);

    // Makes a task and gives its number; it stands on the board as one to do.
    Task<int> CreateAsync(NewTask task, CancellationToken cancellationToken);
}

// A task as the queue reads it. Status is where it stands on the board, and StatusName what the board calls that.
// BlockedBy and Parts are the open tasks it waits for: those that block it and its own sub-tasks.
public sealed record QueueTask(
    int Number,
    string Title,
    string Url,
    IReadOnlyList<string> Labels,
    BoardStatus Status,
    string StatusName,
    IReadOnlyList<int> BlockedBy,
    IReadOnlyList<int> Parts)
{
    public bool Has(string label) => Labels.Contains(label, StringComparer.OrdinalIgnoreCase);
}

public enum BoardStatus
{
    // The task is not on the board, or the project has none.
    Missing,
    Todo,
    InProgress,
    Done,
    Other,
}

public sealed record TaskText(string Description, IReadOnlyList<TaskComment> Comments);

public sealed record TaskComment(string Author, string Text);

public sealed record NewTask(string Title, string Description, IReadOnlyList<string> Labels, string? Milestone);
