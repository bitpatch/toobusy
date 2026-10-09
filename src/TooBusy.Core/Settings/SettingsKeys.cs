namespace TooBusy.Core.Settings;

// The keys of the settings file and the values that have names.
public static class SettingsKeys
{
    public const string Version = "version";
    public const string TrackerType = "tracker.type";
    public const string TrackerRepository = "tracker.repository";
    public const string TrackerBoard = "tracker.board";
    public const string MilestoneRule = "queue.milestone.rule";
    public const string MilestoneTitle = "queue.milestone.title";
    public const string BlockingLabels = "queue.labels.blocking";
    public const string TakeLabels = "queue.labels.take";
    public const string AssistantType = "assistant.type";

    public const string GitHubTracker = "github";
    public const string ClaudeCodeAssistant = "claude-code";

    public static IReadOnlyList<string> All { get; } =
    [
        Version, TrackerType, TrackerRepository, TrackerBoard, MilestoneRule, MilestoneTitle, BlockingLabels, TakeLabels,
        AssistantType,
    ];

    public static string NameOf(MilestoneRule rule) => rule switch
    {
        Settings.MilestoneRule.LowestVersion => "lowest-version",
        Settings.MilestoneRule.EarliestDue => "earliest-due",
        Settings.MilestoneRule.Fixed => "fixed",
        Settings.MilestoneRule.None => "none",
        _ => throw new ArgumentOutOfRangeException(nameof(rule)),
    };

    public static MilestoneRule? RuleNamed(string name) => name switch
    {
        "lowest-version" => Settings.MilestoneRule.LowestVersion,
        "earliest-due" => Settings.MilestoneRule.EarliestDue,
        "fixed" => Settings.MilestoneRule.Fixed,
        "none" => Settings.MilestoneRule.None,
        _ => null,
    };
}
