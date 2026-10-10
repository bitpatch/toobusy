namespace TooBusy.Cli.Terminal;

public enum HomeAction
{
    Run,
    Milestone,
    Settings,
    Exit,
}

// The menu toobusy opens with: what can be done in a project that is set up and has its milestone chosen.
public static class HomeScreen
{
    // `milestone` is the chosen milestone in a few words. Escape, asked twice, is the same as `Exit`.
    public static HomeAction Ask(Page page, string milestone)
    {
        page.EscapeLeaves = true;
        page.Keys = "esc exit";
        Choice[] choices =
        [
            new("Run", "take the tasks one after another"),
            new("Change milestone", Note: milestone),
            new("Settings"),
            new("Exit"),
        ];
        return Picker.Pick(page, [Line.Of("What to do", Tone.Strong)], choices) is { } picked ? (HomeAction)picked : HomeAction.Exit;
    }
}
