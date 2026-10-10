namespace TooBusy.Core.Setup;

// How the setup asks its questions. The terminal implements it as a screen of its own; tests answer from a script.
// Every question gives null when the user goes back from it instead of answering.
public interface ISetupDialog
{
    // What stands around the question from now on: the notes, the answers given so far and the place among the steps.
    void Show(SetupProgress progress);

    // Something takes a moment; the text says what. Gives what the work gives, or null when the user goes back
    // instead of waiting for it: the work is told to stop then, and is not waited for.
    Task<T?> WaitAsync<T>(string text, Func<CancellationToken, Task<T>> work, CancellationToken cancellationToken)
        where T : class;

    // One of the options; gives its index. The hint of every question says what it is about and what to do.
    int? Choose(string label, string hint, IReadOnlyList<SetupOption> options, int proposed);

    // Any number of the options; gives their indexes in the order of the options.
    IReadOnlyList<int>? ChooseMany(string label, string hint, IReadOnlyList<string> options, IReadOnlyList<int> proposed);

    // A typed answer. It is asked again while `refuse` gives a reason against it.
    string? Ask(string label, string hint, string proposed, Func<string, string?> refuse);

    // The board of the tasks: one of the known ones, one by its address, a new one, or none.
    BoardAnswer? AskBoard(BoardQuestion question);

    // Yes or no.
    bool? Confirm(string question);
}

public sealed record SetupOption(string Name, string Detail);

// Steps are the names of the steps of the setup in their order; Step is the index of the one that is asked now,
// and their number when they are all done and only the confirmation is left.
public sealed record SetupProgress(IReadOnlyList<SetupNote> Notes, IReadOnlyList<SetupAnswer> Answers, IReadOnlyList<string> Steps, int Step);

public sealed record SetupNote(SetupTone Tone, string Text);

public sealed record SetupAnswer(string Label, string Value);

public enum SetupTone
{
    Plain,
    Muted,
    Warning,
    Failure,

    // A setting that will change: what it was and what it will be.
    Change,
}

// Linked tells that the board is linked to the repository of the project; the title is empty when it is not known.
public sealed record SetupBoard(string Address, string Title, bool Linked);

// Who a new board can belong to: the user or one of their organisations.
public sealed record SetupOwner(string Login, bool Organisation);

public sealed record SetupBoards(IReadOnlyList<SetupBoard> Boards, IReadOnlyList<SetupOwner> Owners)
{
    public static SetupBoards None { get; } = new([], []);
}

// Current is the board the project has now, when it has one: the user keeps it or changes it.
// A new board can be made only when there are owners to make it for.
public sealed record BoardQuestion(
    string Label,
    string Hint,
    SetupBoard? Current,
    IReadOnlyList<SetupBoard> Known,
    IReadOnlyList<SetupOwner> Owners,
    Func<string, string?> RefuseAddress,
    Func<string, string?> RefuseTitle);

public abstract record BoardAnswer
{
    public sealed record Existing(string Address) : BoardAnswer;

    public sealed record Created(SetupOwner Owner, string Title) : BoardAnswer;

    public sealed record None : BoardAnswer;
}

// What the setup needs to know of the machine before it asks anything.
public interface ISetupEnvironment
{
    Task<SetupEnvironment> InspectAsync(CancellationToken cancellationToken);
}

// TrackerReachable is false when the tracker's command-line tool is missing or not logged in;
// OriginRepository is the `owner/name` of the `origin` remote when it is a GitHub one. That repository is where
// the tasks are: the settings do not name it.
public sealed record SetupEnvironment(IReadOnlyList<SetupProblem> Problems, bool TrackerReachable, string? OriginRepository);

public sealed record SetupProblem(string Text, string Fix);

// The boards the user can reach, those linked to the repository first, and who a new one can belong to.
// It never fails: when they cannot be read there are none, and the question goes without them.
public interface ISetupBoards
{
    Task<SetupBoards> ReadAsync(string? repository, CancellationToken cancellationToken);
}

// What the setup reads from the tracker, and the two things it changes there when the user confirms them.
// Making and linking throw TrackerException when the tracker refuses.
public interface ISetupTracker
{
    // Null when the board can be read with the scope it needs; otherwise why it cannot.
    Task<string?> RefuseBoardAsync(string board, CancellationToken cancellationToken);

    Task<IReadOnlyList<string>> ReadLabelsAsync(string repository, CancellationToken cancellationToken);

    // Makes a board and gives its address.
    Task<string> CreateBoardAsync(SetupOwner owner, string title, CancellationToken cancellationToken);

    Task LinkBoardAsync(string board, string repository, CancellationToken cancellationToken);
}

// The tracker refused to do what it was asked; the message says why, in words for the user.
public sealed class TrackerException(string message) : Exception(message);
