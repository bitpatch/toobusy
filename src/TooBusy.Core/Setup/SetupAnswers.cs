using TooBusy.Core.Queue;
using TooBusy.Core.Settings;

namespace TooBusy.Core.Setup;

// What the setup was answered, as the settings will say it: every setting of the file, each the answer of its
// question or, while it is not answered yet, what the question proposes. An answer is a new record, so that the
// answers a page showed can be taken up again whole. The board is null while nothing is proposed for it: the
// question then proposes the board of the settings, or one linked to the repository. Skill is empty for none.
sealed record SetupAnswers(
    BoardAnswer? Board,
    StatusesAnswer Statuses,
    IReadOnlyList<string> Blocking,
    IReadOnlyList<string> Take,
    string Manual,
    string Interrupted,
    string Skill,
    TaskCommands Commands,
    string? Branch,
    string? PermissionMode)
{
    // The answers as the settings have them.
    public static SetupAnswers Of(ProjectSettings settings) => new(
        settings.Tracker.Board is { } address ? new BoardAnswer.Existing(address) : new BoardAnswer.None(),
        new StatusesAnswer(settings.Tracker.Statuses, settings.Tracker.Board, false, []),
        settings.Queue.Labels.Blocking,
        settings.Queue.Labels.Take,
        settings.Queue.Labels.Manual,
        settings.Queue.Labels.Interrupted,
        settings.Session.Skill ?? "",
        settings.Commands,
        settings.Workspace.Branch,
        settings.Assistant.PermissionMode);

    // The settings of the answers, with the address of the board: a new board has one once it is made.
    public ProjectSettings Settings(string? address) => new(
        new TrackerSettings(SettingsKeys.GitHubTracker, address) { Statuses = Board is BoardAnswer.Existing ? Statuses.Called : BoardStatuses.Default },
        new QueueSettings(new LabelSettings(Blocking, Take, Manual, Interrupted)),
        new AssistantSettings(SettingsKeys.ClaudeCodeAssistant) { PermissionMode = PermissionMode })
    {
        Session = new SessionSettings(Skill.Length > 0 ? Skill : null),
        Commands = Commands,
        Workspace = new WorkspaceSettings(Branch),
    };
}

// Called is what the board that was answered calls the statuses a run knows, and Board the board they were named for: a
// board that is answered again proposes them. Asked is whether they were asked for: a board that was asked about is
// asked about again when the user comes back to it. Lacking is what is said of a board whose statuses are taken without
// a question although a run would take no task of it, or cannot be read; empty when nothing is.
sealed record StatusesAnswer(BoardStatuses Called, string? Board, bool Asked, IReadOnlyList<SetupNote> Lacking)
{
    // The names of GitHub, named for no board: those of a new board, and of a project without one.
    public static StatusesAnswer None { get; } = new(BoardStatuses.Default, null, false, []);
}
