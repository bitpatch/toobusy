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

    // The permission modes a session can be started in: those in which it goes on with nobody at the keyboard and
    // what it does is still checked. In `auto` a model of Claude Code looks at every action; in `dontAsk` whatever
    // the settings of Claude Code do not allow is refused.
    public static IReadOnlyList<string> PermissionModes { get; } = ["auto", "dontAsk"];

    // The mode a session is started in when the settings name none.
    public const string DefaultPermissionMode = "auto";

    // What a mode a session can be started in checks, in a few words: for the question of the setup, and after a
    // `where` in what `toobusy about` says, so that the two say it alike.
    public static string Checks(string mode) => mode == "dontAsk"
        ? "only what the settings of Claude Code in the project allow is done"
        : "a model of Claude Code looks at every action before it is done";

    // The modes of Claude Code that ask before what they do not allow, or wait for a plan to be approved: a session
    // in one of them waits for an answer that never comes.
    static readonly string[] Waiting = ["default", "manual", "acceptEdits", "plan"];

    // The mode of Claude Code that checks nothing a session does.
    const string Unchecked = "bypassPermissions";

    // The level as the list writes it, whatever the case of the letters of the text; null when it is not a level.
    public static string? FindEffort(string text) =>
        Efforts.FirstOrDefault(effort => effort.Equals(text.Trim(), StringComparison.OrdinalIgnoreCase));

    // The mode as Claude Code writes it, whatever the case of the letters of the text; null when a session cannot
    // be started in it.
    public static string? FindPermissionMode(string text) =>
        PermissionModes.FirstOrDefault(mode => mode.Equals(text.Trim(), StringComparison.OrdinalIgnoreCase));

    // Null when a session can be started in the mode, as Claude Code writes it; otherwise why it cannot.
    public static string? RefusePermissionMode(string mode)
    {
        if (PermissionModes.Contains(mode))
            return null;

        var modes = string.Join(" or ", PermissionModes.Select(known => $"`{known}`"));
        if (Waiting.Contains(mode, StringComparer.OrdinalIgnoreCase))
            return $"must be {modes}: in `{mode}` a session waits for an answer, and nobody answers a session of a run";
        if (mode.Equals(Unchecked, StringComparison.OrdinalIgnoreCase))
            return $"must be {modes}: `{mode}` checks nothing a session does, and toobusy starts no session in it";
        return $"must be {modes}, a permission mode of Claude Code in which a session goes on with nobody to answer it";
    }
}
