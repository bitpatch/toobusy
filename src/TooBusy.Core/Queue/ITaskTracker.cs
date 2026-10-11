namespace TooBusy.Core.Queue;

// The tasks of a project in its tracker: reading them for the queue and changing them as a run goes. The task of a
// session is changed by the run, never by the assistant; a new task is made by the assistant, never by the run.
// Everything throws TrackerException when the tracker refuses or does not answer; the message says why, in words
// for the user.
public interface ITaskTracker
{
    // The open tasks of the milestone, or of the whole project when the milestone is null.
    Task<IReadOnlyList<QueueTask>> ReadOpenAsync(string? milestone, CancellationToken cancellationToken);

    // The task with this number when it is closed, which the queue does not read; null when it is open, and when the
    // tracker has no such task. What it waited for is not read.
    Task<QueueTask?> ReadClosedAsync(int number, CancellationToken cancellationToken);

    // Moves the task on the board, putting it there when it is not; does nothing in a project without a board.
    Task SetStatusAsync(int number, BoardStatus status, CancellationToken cancellationToken);

    // Makes the user who runs toobusy an assignee of the task, beside those it has; with a board and without one.
    Task AssignAsync(int number, CancellationToken cancellationToken);

    Task AddLabelAsync(int number, string label, CancellationToken cancellationToken);

    Task RemoveLabelAsync(int number, string label, CancellationToken cancellationToken);

    Task CommentAsync(int number, string text, CancellationToken cancellationToken);

    Task CloseAsync(int number, CancellationToken cancellationToken);
}

// A task as the queue reads it. Status is where it stands on the board, and StatusName what the board calls that.
// BlockedBy and Parts are the open tasks it waits for: those that block it and its own sub-tasks. Milestone is the
// title of its own milestone, null when it has none: in a run without a milestone the tasks differ in it.
public sealed record QueueTask(
    int Number,
    string Title,
    string Url,
    IReadOnlyList<string> Labels,
    BoardStatus Status,
    string StatusName,
    IReadOnlyList<int> BlockedBy,
    IReadOnlyList<int> Parts,
    string? Milestone = null)
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
