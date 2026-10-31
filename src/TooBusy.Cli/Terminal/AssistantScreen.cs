namespace TooBusy.Cli.Terminal;

public enum AssistantAction
{
    Model,
    Effort,
    Sessions,
}

// What `Assistant` of the menu opens: the model and the effort the tasks are done with by default and how many sessions
// work at once, each with what is chosen after its name.
public static class AssistantScreen
{
    // `model`, `effort` and `sessions` are what is chosen, in a few words; `at` is the row the pointer starts on.
    // Gives null when Escape is pressed or `Back` is chosen.
    public static AssistantAction? Ask(Page page, string model, string effort, int at = 0, string sessions = "1")
    {
        page.EscapeLeaves = false;
        page.Keys = "esc back";
        Choice[] choices = [new("Model", Note: model), new("Effort", Note: effort), new("Sessions", Note: sessions)];
        return Picker.Pick(page, [Line.Of("Assistant", Tone.Strong)], choices, at: at) is { } picked ? (AssistantAction)picked : null;
    }
}
