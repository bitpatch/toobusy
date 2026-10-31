using TooBusy.Core.Assistant;

namespace TooBusy.Core.Setup;

// How the setup asks its questions. The terminal implements it as a screen of its own; tests answer from a script.
// Every question gives null when the user goes back from it instead of answering.
public interface ISetupDialog
{
    // What stands around the question from now on: the notes, the answers given so far and the place among the steps.
    void Show(SetupProgress progress);

    // Something takes a moment; the text says what, under the label and the hint of the question it is read for.
    // Gives what the work gives, or null when the user goes back instead of waiting for it: the work is told to stop
    // then, and is not waited for.
    Task<T?> WaitAsync<T>(string label, string hint, string text, Func<CancellationToken, Task<T>> work, CancellationToken cancellationToken)
        where T : class;

    // One of the options; gives its index. The hint of every question says what it is about and what to do.
    int? Choose(string label, string hint, IReadOnlyList<SetupOption> options, int proposed);

    // Any number of the options; gives their indexes in the order of the options.
    IReadOnlyList<int>? ChooseMany(string label, string hint, IReadOnlyList<SetupOption> options, IReadOnlyList<int> proposed);

    // A typed answer. It is asked again while `refuse` gives a reason against it.
    string? Ask(string label, string hint, string proposed, Func<string, string?> refuse);

    // The board of the tasks: one of the known ones, one by its address, a new one, or none.
    BoardAnswer? AskBoard(BoardQuestion question);

    // The last question, when everything is answered: true saves what was answered and false leaves it unsaved.
    bool? Confirm(string question);
}

// Current is an option the settings have now, which the screen marks among the others. One that is off cannot be
// chosen, as a label that another setting holds: what explains it says which.
public sealed record SetupOption(string Name, string Detail, bool Current = false, bool Off = false);

// Steps are the names of the steps of the setup in their order; Step is the index of the one that is asked now,
// and their number when they are all done and only the confirmation is left.
public sealed record SetupProgress(IReadOnlyList<SetupNote> Notes, IReadOnlyList<SetupAnswer> Answers, IReadOnlyList<string> Steps, int Step)
{
    // A question of the step that is not its first one: going back from it comes to the question before it, so it
    // never leaves the setup, in the first step either.
    public bool Later { get; init; }

    // A question of one setting that the menu of the settings opened. There are no steps around it; going back from
    // its first question gives what is chosen or typed there, as an answer, for the menu to look whether that
    // changes the setting; and the last question saves the change and comes back to the menu.
    public bool Menu { get; init; }
}

// The settings as the menu of the settings lists them, in its order.
public enum SetupSetting
{
    Project,
    Statuses,
    Blocking,
    Take,
    Manual,
    Interrupted,
    Skill,
    Before,
    After,
    Branch,
    PermissionMode,
}

// How a value looks in the menu of the settings: one that is set; one that says nothing is, as `none`; and one that
// keeps tasks out of a run, as the blocking labels do.
public enum SetupLook
{
    Set,
    Unset,
    KeepsOut,
}

// A setting as the menu of the settings lists it: its name and its value as the settings have it.
public sealed record SetupValue(SetupSetting Setting, string Label, string Value, SetupLook Look);

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

    // GitHub did not answer in time: there are none because nothing is known, and the question says so.
    public static SetupBoards NoAnswer { get; } = new([], []) { Unanswered = true };

    public bool Unanswered { get; init; }
}

// Current is the board the project has now, when it has one: the user keeps it or changes it.
// A new board can be made only when there are owners to make it for. Proposed is an answer that names no board
// there is, a new one or none, when that is what an option said, what was answered before or, for none, what the
// settings that exist say: the question opens with it, and there is no Current then.
public sealed record BoardQuestion(
    string Label,
    string Hint,
    SetupBoard? Current,
    IReadOnlyList<SetupBoard> Known,
    IReadOnlyList<SetupOwner> Owners,
    Func<string, string?> RefuseAddress,
    Func<string, string?> RefuseTitle,
    BoardAnswer? Proposed = null)
{
    // The address of the board the settings have now, which the screen marks among the others; null where they
    // have none, or where there are no settings yet.
    public string? Saved { get; init; }

    // Why the boards the user can reach are not known, which the screen says where it would list them; null when
    // they are, none among them too.
    public string? Unread { get; init; }
}

public abstract record BoardAnswer
{
    public sealed record Existing(string Address) : BoardAnswer;

    public sealed record Created(SetupOwner Owner, string Title) : BoardAnswer;

    public sealed record None : BoardAnswer;
}

// What the options of `toobusy init` say before anything is asked, each the proposed answer of its question; null
// leaves a question with what it proposes by itself. An empty list of labels is an answer: no label; and so is an empty
// name of the skill: no skill. The commands around a task, the branch and the permission mode have no question: null
// leaves them as the settings have them. The three statuses are what the board calls them: each is the proposed answer
// of its question, which a board that is named as GitHub names one is not asked.
public sealed record SetupProposals(
    BoardProposal? Board = null,
    IReadOnlyList<string>? Blocking = null,
    IReadOnlyList<string>? Take = null,
    string? Manual = null,
    string? Interrupted = null,
    string? Skill = null,
    CommandProposal? Before = null,
    CommandProposal? After = null)
{
    public static SetupProposals None { get; } = new();

    public string? TodoStatus { get; init; }

    public string? InProgressStatus { get; init; }

    public string? DoneStatus { get; init; }

    public BranchProposal? Branch { get; init; }

    // The permission mode of the assistant a session is started in, as the option names it.
    public string? PermissionMode { get; init; }
}

// A command around a task as an option names it: the line for the shell, or null for no command.
public sealed record CommandProposal(string? Line);

// The branch the tasks are done on as an option names it, or null for none.
public sealed record BranchProposal(string? Name);

// The board as an option names it: by an address, as a new one with its title and the login of whose it will be,
// which is the first of those it can be made for when it is not given, or none.
public abstract record BoardProposal
{
    public sealed record Existing(string Address) : BoardProposal;

    public sealed record Created(string Title, string? Owner) : BoardProposal;

    public sealed record None : BoardProposal;
}

// What the setup needs to know of the machine before it asks anything.
public interface ISetupEnvironment
{
    Task<SetupEnvironment> InspectAsync(CancellationToken cancellationToken);

    // Whether the assistant has the skill of this name to start a session with, and where it is or is to be written.
    SkillLook FindSkill(string name);
}

// TrackerReachable is false when the tracker's command-line tool is missing or not logged in;
// OriginRepository is the `owner/name` of the `origin` remote when it is a GitHub one. That repository is where
// the tasks are: the settings do not name it.
public sealed record SetupEnvironment(IReadOnlyList<SetupProblem> Problems, bool TrackerReachable, string? OriginRepository);

public sealed record SetupProblem(string Text, string Fix);

// The boards the user can reach, those linked to the repository first, and who a new one can belong to.
// It never fails: when they cannot be read there are none, and the question goes without them. When the tracker did
// not answer in time it says so: the question tells the user, as the user is not to think that there are none.
public interface ISetupBoards
{
    Task<SetupBoards> ReadAsync(string? repository, CancellationToken cancellationToken);
}

// What the setup reads from the tracker, and what it changes there when the user confirms it.
// Making and linking throw TrackerException when the tracker refuses.
public interface ISetupTracker
{
    // Null when the board can be read with the scope it needs; otherwise why it cannot.
    Task<string?> RefuseBoardAsync(string board, CancellationToken cancellationToken);

    // What the board calls its statuses; none for a board that has none, and null when they cannot be read.
    Task<IReadOnlyList<string>?> ReadStatusesAsync(string board, CancellationToken cancellationToken);

    Task<IReadOnlyList<string>> ReadLabelsAsync(string repository, CancellationToken cancellationToken);

    // Makes a board and gives its address.
    Task<string> CreateBoardAsync(SetupOwner owner, string title, CancellationToken cancellationToken);

    Task LinkBoardAsync(string board, string repository, CancellationToken cancellationToken);

    // Makes a label in the repository; a label that is there already is left as it is.
    Task CreateLabelAsync(string repository, string name, CancellationToken cancellationToken);
}
