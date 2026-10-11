namespace TooBusy.Core.Settings;

// The keys of the settings file and the values that have names.
public static class SettingsKeys
{
    public const string Version = "version";
    public const string TrackerType = "tracker.type";
    public const string TrackerBoard = "tracker.board";
    public const string TodoStatus = "tracker.statuses.todo";
    public const string InProgressStatus = "tracker.statuses.in-progress";
    public const string DoneStatus = "tracker.statuses.done";
    public const string BlockingLabels = "queue.labels.blocking";
    public const string TakeLabels = "queue.labels.take";
    public const string OwnerLabel = "queue.labels.owner";
    public const string InterruptedLabel = "queue.labels.interrupted";
    public const string AssistantType = "assistant.type";
    public const string SessionSkill = "session.skill";
    public const string TaskBefore = "task.before";
    public const string TaskAfter = "task.after";

    public const string GitHubTracker = "github";
    public const string ClaudeCodeAssistant = "claude-code";

    public static IReadOnlyList<string> All { get; } =
    [
        Version, TrackerType, TrackerBoard, TodoStatus, InProgressStatus, DoneStatus, BlockingLabels, TakeLabels, OwnerLabel, InterruptedLabel,
        AssistantType, SessionSkill, TaskBefore, TaskAfter,
    ];
}
