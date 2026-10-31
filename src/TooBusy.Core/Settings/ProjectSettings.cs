using TooBusy.Core.Assistant;
using TooBusy.Core.Queue;

namespace TooBusy.Core.Settings;

// The settings of a project as `.toobusy/settings.toml` holds them, a record for each of its sections. An instance is
// always valid: it comes out of SettingsValidator or is put together by the setup.
public sealed record ProjectSettings(TrackerSettings Tracker, QueueSettings Queue, AssistantSettings Assistant)
{
    // The version of the settings schema that this build reads and writes.
    public const int SchemaVersion = 1;

    public SessionSettings Session { get; init; } = SessionSettings.None;

    // The commands of the project a run runs around a task, the section `task`.
    public TaskCommands Commands { get; init; } = TaskCommands.None;

    public WorkspaceSettings Workspace { get; init; } = WorkspaceSettings.None;
}

// What a session of the project starts with: Skill is the skill, by its name; null when the project names none.
public sealed record SessionSettings(string? Skill)
{
    public static SessionSettings None { get; } = new(Skill: null);
}

// The working copy the tasks are done in. Branch is the branch they are done on: a run checks that the working copy is
// on it and brings it up to date with its upstream before a task. Null when the project names none, and a run does the
// tasks on whatever is checked out.
public sealed record WorkspaceSettings(string? Branch)
{
    public static WorkspaceSettings None { get; } = new(Branch: null);
}

// What a run runs through the shell of the machine, at the root of the working copy: Before a task is taken, and
// After its session, however the task went. Null is a command the project does not have.
public sealed record TaskCommands(string? Before, string? After)
{
    public static TaskCommands None { get; } = new(null, null);
}

// Statuses are what the board calls the statuses a run knows; they mean something only where there is a board.
public sealed record TrackerSettings(string Type, string? Board)
{
    public BoardStatuses Statuses { get; init; } = BoardStatuses.Default;
}

public sealed record QueueSettings(LabelSettings Labels);

// Blocking and Take say which tasks a run takes. Manual and Interrupted are the labels a run puts on a task itself:
// on one that waits for the owner, which is not taken while it has the label, and on one it had to stop, which is
// taken first.
public sealed record LabelSettings(IReadOnlyList<string> Blocking, IReadOnlyList<string> Take, string Manual, string Interrupted);

public sealed record AssistantSettings(string Type)
{
    // The permission mode of the assistant a session is started in, as the assistant writes it; null when the
    // project names none.
    public string? PermissionMode { get; init; }

    // The mode a session is started in: the one the settings name, or `auto` where they name none.
    public string SessionMode => PermissionMode ?? ClaudeCodeOptions.DefaultPermissionMode;
}
