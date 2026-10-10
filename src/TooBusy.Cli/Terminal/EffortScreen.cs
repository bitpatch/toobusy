using TooBusy.Core.Assistant;

namespace TooBusy.Cli.Terminal;

// The question about the effort the tasks are done with by default: the levels of the assistant, from the least to
// the most.
public static class EffortScreen
{
    // `chosen` is null when the user has not chosen yet: the pointer then starts on the proposed level. Gives the
    // level, or null when Escape is pressed or `Back` is chosen.
    public static string? Ask(Page page, string? chosen)
    {
        var levels = ClaudeCodeOptions.Efforts.ToList();
        var picked = Picker.Pick(
            page,
            Picker.Head("Effort", "How hard the assistant thinks over a task by default."),
            [.. levels.Select(level => new Choice(level))],
            at: Math.Max(0, levels.IndexOf(chosen ?? ClaudeCodeOptions.ProposedEffort)));
        return picked is { } index ? levels[index] : null;
    }
}
