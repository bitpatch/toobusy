using TooBusy.Core.Settings;

namespace TooBusy.Cli.Imitation;

// The made-up project of every demo, the repository of a small online shop: the same wherever a demo is run, as a
// demo reads nothing of the folder it is run in, neither its `origin` nor its settings nor the sign of a run.
public static class DemoProject
{
    // The folder of the project, as the bar names it.
    public const string Folder = "~/Projects/demo-project";

    public const string Repository = "example/demo-project";

    // The branch a demo of several sessions lands its made-up work on.
    public const string Branch = "develop";

    // The organisation the repository belongs to, and the user who runs the demo.
    public const string Organisation = "example";

    public const string User = "demo-user";

    // The board of the settings, which is linked to the repository.
    public const string Board = "https://github.com/orgs/example/projects/1";

    public const string BoardTitle = "Demo project";

    // The labels of the repository: those the settings name, and no other.
    public static IReadOnlyList<string> Labels { get; } = ["feature", "bug", "chore", "docs", "draft", "design", "manual", "interrupted"];

    // The settings of a demo that shows a project that is set up: the board, the labels that let a task in and those
    // that hold one, the labels of the owner and of an interrupted task, and the skill a session starts with.
    public static ProjectSettings Settings { get; } = new(
        new TrackerSettings(SettingsKeys.GitHubTracker, Board),
        new QueueSettings(new LabelSettings(["draft", "design"], ["feature", "bug", "chore", "docs"], "manual", "interrupted")),
        new AssistantSettings(SettingsKeys.ClaudeCodeAssistant))
    {
        Session = new SessionSettings("task-auto"),
    };

    // The address of an issue of the repository.
    public static string Issue(int number) => $"https://github.com/{Repository}/issues/{number}";
}
