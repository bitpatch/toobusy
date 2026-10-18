using TooBusy.Core.Queue;

namespace TooBusy.Core.Settings;

// The settings of a project as `.toobusy/settings.toml` holds them. An instance is always valid:
// it comes out of SettingsValidator or is put together by the setup.
public sealed record ProjectSettings(TrackerSettings Tracker, QueueSettings Queue, AssistantSettings Assistant)
{
    // The version of the settings schema that this build reads and writes.
    public const int SchemaVersion = 1;

    // The skill of the project a session starts with, by its name; null when the project names none.
    public string? Skill { get; init; }

    // The commands of the project a run runs around a task.
    public TaskCommands Commands { get; init; } = TaskCommands.None;

    // The branch the tasks are done on: a run checks that the working copy is on it and brings it up to date with
    // its upstream before a task. Null when the project names none, and a run does the tasks on whatever is checked
    // out.
    public string? Branch { get; init; }
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

// Blocking and Take say which tasks a run takes. Owner and Interrupted are the labels a run puts on a task itself:
// on one that waits for the owner, which is not taken while it has the label, and on one it had to stop, which is
// taken first.
public sealed record LabelSettings(IReadOnlyList<string> Blocking, IReadOnlyList<string> Take, string Owner, string Interrupted);

public sealed record AssistantSettings(string Type);
