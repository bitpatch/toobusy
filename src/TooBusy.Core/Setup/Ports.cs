using TooBusy.Core.Queue;

namespace TooBusy.Core.Setup;

// How the setup asks its questions and says what it has to say. The terminal implements it with arrow keys;
// tests answer from a script.
public interface ISetupDialog
{
    // One of the options; gives its index.
    int Choose(string label, IReadOnlyList<SetupOption> options, int proposed);

    // Any number of the options; gives their indexes in the order of the options.
    IReadOnlyList<int> ChooseMany(string label, IReadOnlyList<string> options, IReadOnlyList<int> proposed, string whenNone);

    // A typed answer. It is asked again while `refuse` gives a reason against it.
    string Ask(string label, string hint, string proposed, string whenEmpty, Func<string, string?> refuse);

    bool Confirm(string question);

    // A step that has one answer only: it is shown, not asked.
    void Answered(string label, string value);

    void Say(SetupTone tone, string text);
}

public sealed record SetupOption(string Name, string Detail);

public enum SetupTone
{
    Plain,
    Muted,
    Warning,
    Failure,
    Added,
    Removed,
}

// What the setup needs to know of the machine before it asks anything.
public interface ISetupEnvironment
{
    Task<SetupEnvironment> InspectAsync(CancellationToken cancellationToken);
}

// TrackerReachable is false when the tracker's command-line tool is missing or not logged in;
// OriginRepository is the `owner/name` of the `origin` remote when it is a GitHub one.
public sealed record SetupEnvironment(IReadOnlyList<SetupProblem> Problems, bool TrackerReachable, string? OriginRepository);

public sealed record SetupProblem(string Text, string Fix);

// What the setup reads from the tracker.
public interface ISetupTracker
{
    // Null when the repository can be read; otherwise why it cannot.
    Task<string?> RefuseRepositoryAsync(string repository, CancellationToken cancellationToken);

    // Null when the board can be read with the scope it needs; otherwise why it cannot.
    Task<string?> RefuseBoardAsync(string board, CancellationToken cancellationToken);

    Task<IReadOnlyList<string>> ReadLabelsAsync(string repository, CancellationToken cancellationToken);

    Task<IReadOnlyList<Milestone>> ReadOpenMilestonesAsync(string repository, CancellationToken cancellationToken);
}
