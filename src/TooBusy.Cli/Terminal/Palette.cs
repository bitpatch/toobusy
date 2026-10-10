using System.Text;

namespace TooBusy.Cli.Terminal;

// What a text is: its colour follows from it.
public enum Tone
{
    Plain,
    Accent,
    Error,
    Success,
    Warning,
    Muted,

    // Between a plain text and a muted one: a tab that is not chosen, the name of a key among the hints.
    Quiet,

    // What stands out without a colour of its own: a heading, the choice under the pointer.
    Strong,
}

// What a text of a screen stands on.
public enum Ground
{
    None,

    // The band at the top of a screen that names the tool, the one at its bottom, and a tab that is not chosen.
    Bar,

    // The tab that is chosen: the accent itself is its ground.
    Chosen,
}

// The colours of the tool, all of them in this one place. They are provisional: the brand colours are still to be chosen.
// A palette without colours gives every text back as it is.
public sealed class Palette
{
    const string Reset = "\u001b[0m";

    // Truecolor for a dark and for a light terminal, and the nearest of the sixteen colours for a terminal without truecolor.
    static readonly Shade AccentShade = new("38;2;212;80;63", "38;2;176;58;46", "31");
    static readonly Shade ErrorShade = new("38;2;240;100;126", "38;2;194;37;92", "95");
    static readonly Shade SuccessShade = new("38;2;78;201;122", "38;2;26;127;55", "32");
    static readonly Shade WarningShade = new("38;2;229;192;123", "38;2;154;103;0", "33");
    static readonly Shade MutedShade = new("38;2;128;128;128", "38;2;118;118;118", "90");
    static readonly Shade QuietShade = new("38;2;176;176;176", "38;2;88;88;88", "37");
    static readonly Shade StrongShade = new("1", "1", "1");

    // The bar is a grey a little off the background. A terminal of sixteen colours has none: its grey is the muted text.
    static readonly Shade BarShade = new("48;2;48;48;48", "48;2;228;228;228", "");

    // The chosen tab is white on the deeper accent, in a dark terminal and in a light one; a terminal of sixteen
    // colours shows it reversed.
    static readonly Shade ChosenShade = new("1;38;2;255;255;255;48;2;176;58;46", "1;38;2;255;255;255;48;2;176;58;46", "7");

    readonly Func<Shade, string>? code;

    Palette(Func<Shade, string>? code) => this.code = code;

    public static Palette None { get; } = new(null);

    public static Palette Dark { get; } = new(shade => shade.Dark);

    public static Palette Light { get; } = new(shade => shade.Light);

    public static Palette Basic { get; } = new(shade => shade.Basic);

    // No colour when `NO_COLOR` is set or the stream is not a terminal.
    public static Palette Detect(bool isTerminal, Func<string, string?> variable)
    {
        if (!isTerminal || !string.IsNullOrEmpty(variable("NO_COLOR")))
            return None;
        if (variable("COLORTERM") is not ("truecolor" or "24bit"))
            return Basic;

        // Terminals that tell their colours do it as `foreground;background`; 7 and 15 are the light backgrounds.
        var background = variable("COLORFGBG")?.Split(';')[^1];
        return background is "7" or "15" ? Light : Dark;
    }

    // Whether the palette has colours to draw a cursor of its own with.
    public bool DrawsCursor => code is not null;

    // The cell of a screen's own cursor at a moment of its blink: `glow` goes from 1, a block in the accent with what
    // stands in the cell drawn over it, down to 0, where the cell is as if there were no cursor, and back. Between
    // them the block fades toward the ground of the terminal and the character toward the colour of any text.
    // A terminal of sixteen colours reverses the cell for the brighter half of the blink.
    public string Cursor(string text, double glow)
    {
        if (code is null || glow <= Faint)
            return text;
        if (ReferenceEquals(this, Basic))
            return glow >= 0.5 ? $"\u001b[7m{text}{Reset}" : text;

        return ReferenceEquals(this, Light)
            ? $"\u001b[38;2;{Mix((30, 30, 30), (255, 255, 255), glow)};48;2;{Mix((246, 246, 246), (176, 58, 46), glow)}m{text}{Reset}"
            : $"\u001b[38;2;{Mix((226, 226, 226), (20, 20, 20), glow)};48;2;{Mix((28, 28, 28), (212, 80, 63), glow)}m{text}{Reset}";
    }

    // The chosen line of a screen in the same blink: its text is in the accent at 1 and in the colour of any text
    // at 0, so that the line is read as well at every moment.
    public string Glow(string text, double glow)
    {
        if (code is null || text.Length == 0 || glow <= Faint)
            return text;
        if (ReferenceEquals(this, Basic))
            return glow >= 0.5 ? Accent(text) : text;

        return ReferenceEquals(this, Light)
            ? $"\u001b[38;2;{Mix((30, 30, 30), (176, 58, 46), glow)}m{text}{Reset}"
            : $"\u001b[38;2;{Mix((226, 226, 226), (212, 80, 63), glow)}m{text}{Reset}";
    }

    // Below this the blink is off: what blinks is drawn as it is, in the colours the terminal itself has.
    const double Faint = 0.02;

    static string Mix((int Red, int Green, int Blue) from, (int Red, int Green, int Blue) to, double glow)
    {
        int Between(int low, int high) => (int)Math.Round(low + ((high - low) * Math.Clamp(glow, 0, 1)));
        return string.Create(System.Globalization.CultureInfo.InvariantCulture, $"{Between(from.Red, to.Red)};{Between(from.Green, to.Green)};{Between(from.Blue, to.Blue)}");
    }

    public string Accent(string text) => Paint(Tone.Accent, text);

    public string Error(string text) => Paint(Tone.Error, text);

    public string Success(string text) => Paint(Tone.Success, text);

    public string Warning(string text) => Paint(Tone.Warning, text);

    public string Muted(string text) => Paint(Tone.Muted, text);

    // A text in the colour of its tone on the given ground. The ground is painted under spaces too.
    public string Paint(Tone tone, string text, Ground ground = Ground.None)
    {
        if (code is null || text.Length == 0)
            return text;
        if (ground == Ground.Chosen)
            return $"\u001b[{code(ChosenShade)}m{text}{Reset}";

        var colour = tone switch
        {
            Tone.Accent => code(AccentShade),
            Tone.Error => code(ErrorShade),
            Tone.Success => code(SuccessShade),
            Tone.Warning => code(WarningShade),
            Tone.Muted => code(MutedShade),
            Tone.Quiet => code(QuietShade),
            Tone.Strong => code(StrongShade),
            _ => "",
        };
        var codes = string.Join(';', ((string[])[colour, ground == Ground.Bar ? code(BarShade) : ""]).Where(part => part.Length > 0));
        return codes.Length == 0 ? text : $"\u001b[{codes}m{text}{Reset}";
    }

    // A message of the tool: `toobusy:` at its start is muted and whatever stands between backticks, a command as a rule,
    // gets the accent. The text itself is the same with and without colour.
    public string Message(string text)
    {
        if (code is null)
            return text;

        const string prefix = "toobusy:";
        var start = text.StartsWith(prefix, StringComparison.Ordinal) ? prefix.Length : 0;
        var message = new StringBuilder(start > 0 ? Muted(prefix) : "");
        var rest = text[start..];
        while (true)
        {
            var open = rest.IndexOf('`', StringComparison.Ordinal);
            var close = open < 0 ? -1 : rest.IndexOf('`', open + 1);
            if (close < 0)
                return message.Append(rest).ToString();
            message.Append(rest[..open]).Append(Accent(rest[open..(close + 1)]));
            rest = rest[(close + 1)..];
        }
    }

    sealed record Shade(string Dark, string Light, string Basic);
}
