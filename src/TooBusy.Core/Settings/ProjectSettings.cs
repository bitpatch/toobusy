namespace TooBusy.Core.Settings;

// The settings of a project as `.toobusy/settings.toml` holds them. An instance is always valid:
// it comes out of SettingsValidator or is put together by the setup.
public sealed record ProjectSettings(TrackerSettings Tracker, QueueSettings Queue, AssistantSettings Assistant)
{
    // The version of the settings schema that this build reads and writes.
    public const int SchemaVersion = 1;
}

public sealed record TrackerSettings(string Type, string Repository, string? Board);

public sealed record QueueSettings(MilestoneSettings Milestone, LabelSettings Labels);

public sealed record MilestoneSettings(MilestoneRule Rule, string? Title);

public sealed record LabelSettings(IReadOnlyList<string> Blocking, IReadOnlyList<string> Take);

public sealed record AssistantSettings(string Type);

public enum MilestoneRule
{
    LowestVersion,
    EarliestDue,
    Fixed,
    None,
}
