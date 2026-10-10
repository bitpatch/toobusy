namespace TooBusy.Core.Assistant;

// What Claude Code, the only assistant so far, lets the user choose from.
public static class ClaudeCodeOptions
{
    // The names of the models that are offered; any other name can be typed.
    public static IReadOnlyList<string> Models { get; } = ["fable", "opus", "sonnet", "haiku"];

    // The levels of effort, from the least to the most.
    public static IReadOnlyList<string> Efforts { get; } = ["low", "medium", "high", "xhigh", "max"];

    // The level that is proposed to a user who has not chosen.
    public const string ProposedEffort = "high";

    // The level as the list writes it, whatever the case of the letters of the text; null when it is not a level.
    public static string? FindEffort(string text) =>
        Efforts.FirstOrDefault(effort => effort.Equals(text.Trim(), StringComparison.OrdinalIgnoreCase));
}
