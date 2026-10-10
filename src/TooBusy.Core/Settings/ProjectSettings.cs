namespace TooBusy.Core.Settings;

// The settings of a project as `.toobusy/settings.toml` holds them. An instance is always valid:
// it comes out of SettingsValidator or is put together by the setup.
public sealed record ProjectSettings(TrackerSettings Tracker, QueueSettings Queue, AssistantSettings Assistant)
{
    // The version of the settings schema that this build reads and writes.
    public const int SchemaVersion = 1;
}

public sealed record TrackerSettings(string Type, string? Board);

public sealed record QueueSettings(LabelSettings Labels);

// Blocking and Take say which tasks a run takes. Owner and Interrupted are the labels a run puts on a task itself:
// on one that waits for the owner, which is not taken while it has the label, and on one it had to stop, which is
// taken first.
public sealed record LabelSettings(IReadOnlyList<string> Blocking, IReadOnlyList<string> Take, string Owner, string Interrupted);

public sealed record AssistantSettings(string Type);
