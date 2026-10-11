namespace TooBusy.Core.Doctor;

public enum Platform
{
    MacOS,
    Linux,
    Windows,
}

// The programs a run starts.
public enum Tool
{
    Git,
    GitHubCli,
    ClaudeCode,
}

public enum BoardAccess
{
    Readable,

    // The login has no right to work with projects.
    LacksScope,

    // The board does not exist, or the user has no access to it.
    Unreadable,

    // The tracker did not answer.
    NoAnswer,
}

// What the checks ask of the machine and, through its tools, of the tracker and the assistant. Nothing here changes
// anything, and nothing throws but a cancellation: what cannot be asked is told by the answer.
public interface ICheckupMachine
{
    Platform Platform { get; }

    // Whether the tool is on the path and runs.
    Task<bool> HasAsync(Tool tool, CancellationToken cancellationToken);

    // Whether the package manager a fix would name on this platform is on the path; false where there is none.
    Task<bool> HasManagerAsync(CancellationToken cancellationToken);

    Task<bool> TrackerLoggedInAsync(CancellationToken cancellationToken);

    Task<bool> AssistantLoggedInAsync(CancellationToken cancellationToken);

    Task<bool> CanReadRepositoryAsync(string repository, CancellationToken cancellationToken);

    Task<BoardAccess> CheckBoardAsync(string board, CancellationToken cancellationToken);

    // The labels of the repository; null when they cannot be read.
    Task<IReadOnlyList<string>?> ReadLabelsAsync(string repository, CancellationToken cancellationToken);
}

// Where the checks tell how they go: a check that starts, and every check as it ends. A check that is skipped
// never starts.
public interface ICheckupView
{
    void Start(string name);

    void Done(CheckResult result);
}

public enum CheckState
{
    Passed,
    Failed,
    Skipped,
}

// How a check ended. Text is what a passed check found, what is wrong for a failed one, and what a skipped one
// waits for; Details are more lines of what is wrong, and Fix is the command, or the address, that puts it right.
public sealed record CheckResult(string Name, CheckState State, string Text = "", string? Fix = null)
{
    public IReadOnlyList<string> Details { get; init; } = [];
}
