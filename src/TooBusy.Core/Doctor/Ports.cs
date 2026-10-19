using TooBusy.Core.Assistant;

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

// What is found of a tool: Runs when it is on the path and runs. Unstarted is why one that is on the path could not be
// started; null for one that runs, and for one that is not there.
public sealed record ToolLook(bool Runs, string? Unstarted = null)
{
    // The version of the tool toobusy was written against, for one that runs and is newer than it: what the tool
    // prints may have changed. Null for any other.
    public string? WrittenAgainst { get; init; }
}

// What is found of the login of the tracker's command-line tool.
public enum TrackerLoginState
{
    LoggedIn,
    LoggedOut,

    // The tracker did not answer in time: whether the tool is logged in is not known.
    NoAnswer,
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

    // Whether the tool is on the path and runs, and when it is there and does not, why.
    Task<ToolLook> FindAsync(Tool tool, CancellationToken cancellationToken);

    // Whether the package manager a fix would name on this platform is on the path; false where there is none.
    Task<bool> HasManagerAsync(CancellationToken cancellationToken);

    Task<TrackerLoginState> CheckTrackerLoginAsync(CancellationToken cancellationToken);

    Task<bool> AssistantLoggedInAsync(CancellationToken cancellationToken);

    Task<bool> CanReadRepositoryAsync(string repository, CancellationToken cancellationToken);

    Task<BoardAccess> CheckBoardAsync(string board, CancellationToken cancellationToken);

    // What the board calls its statuses; none for a board that has none, and null when they cannot be read.
    Task<IReadOnlyList<string>?> ReadStatusesAsync(string board, CancellationToken cancellationToken);

    // The labels of the repository; null when they cannot be read.
    Task<IReadOnlyList<string>?> ReadLabelsAsync(string repository, CancellationToken cancellationToken);

    // Whether the assistant has the skill of this name to start a session with, in this project or of the user.
    SkillLook FindSkill(string name);
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

    // The check passed, and something is to be known all the same: a warning fails nothing.
    Warned,
    Failed,
    Skipped,
}

// How a check ended. Text is what a passed check found, what a warned one warns of, what is wrong for a failed one, and
// what a skipped one waits for; Details are more lines of what is wrong, and Fix is the command, or the address, that
// puts it right.
public sealed record CheckResult(string Name, CheckState State, string Text = "", string? Fix = null)
{
    public IReadOnlyList<string> Details { get; init; } = [];
}
