using TooBusy.Core.Setup;

namespace TooBusy.Cli.Terminal;

// What `Settings` of the menu opens: the settings of the project one under another, all of them at once, each with
// its value after its name. A value is in the colour of success, as everything that is chosen; the blocking labels
// are in the colour of an error, as they keep tasks out of a run; and a value that says nothing is set is muted.
public static class SettingsScreen
{
    public const string Label = "Settings";
    public const string Hint = "Choose a setting to change it: a change is saved when you say so.";

    // `at` is the row the pointer starts on. Gives the setting that is chosen, or null when Escape is pressed or
    // `Back` is chosen.
    public static SetupSetting? Ask(Page page, IReadOnlyList<SetupValue> values, int at = 0)
    {
        page.EscapeLeaves = false;
        page.Keys = "esc back";
        Choice[] choices =
        [
            .. values.Select(value => new Choice(value.Label, Note: value.Value, NoteTone: value.Look switch
            {
                SetupLook.Unset => Tone.Muted,
                SetupLook.KeepsOut => Tone.Error,
                _ => null,
            })),
        ];
        return Picker.Pick(page, Picker.Head(Label, Hint), choices, at: at, rows: choices.Length + 1) is { } picked ? values[picked].Setting : null;
    }
}
