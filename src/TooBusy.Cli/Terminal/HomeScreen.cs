namespace TooBusy.Cli.Terminal;

public enum HomeAction
{
    Run,
    Assistant,
    Milestone,
    Settings,
    Exit,
}

// The menu toobusy opens with: what can be done in a project that is set up and has its milestone, its model and
// its effort chosen.
public static class HomeScreen
{
    // `assistant` is the chosen model and effort, and `milestone` the chosen milestone, in a few words. Escape, asked
    // twice, is the same as `Exit`.
    public static HomeAction Ask(Page page, string assistant, string milestone)
    {
        page.EscapeLeaves = true;
        page.Keys = "esc exit";
        Choice[] choices =
        [
            new("Run", "take the tasks one after another"),
            new("Assistant", Note: assistant),
            new("Milestone", Note: milestone),
            new("Settings"),
            new("Exit"),
        ];
        return Picker.Pick(page, [Line.Of("What to do", Tone.Strong)], choices) is { } picked ? (HomeAction)picked : HomeAction.Exit;
    }
}
