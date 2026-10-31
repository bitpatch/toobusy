using System.Globalization;
using System.Text;
using Rgb = (int Red, int Green, int Blue);

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
    const string Bold = "1";

    // Truecolor for a dark and for a light terminal, and the nearest of the sixteen colours for a terminal without truecolor.
    static readonly Shade AccentShade = new((212, 80, 63), (176, 58, 46), "31");
    static readonly Shade ErrorShade = new((240, 100, 126), (194, 37, 92), "95");
    static readonly Shade SuccessShade = new((78, 201, 122), (26, 127, 55), "32");
    static readonly Shade WarningShade = new((229, 192, 123), (154, 103, 0), "33");
    static readonly Shade MutedShade = new((128, 128, 128), (118, 118, 118), "90");
    static readonly Shade QuietShade = new((176, 176, 176), (88, 88, 88), "37");

    // The bar is a grey a little off the background. A terminal of sixteen colours has none: its grey is the muted text.
    static readonly Shade BarShade = new((48, 48, 48), (228, 228, 228), "");

    // What a blink fades from or to that is no tone of the tool: the colour of any text and the ground of the
    // terminal, as terminals have them, and the character in the cursor at the brightest of the blink.
    static readonly Shade TextShade = new((226, 226, 226), (30, 30, 30), "");
    static readonly Shade GroundShade = new((28, 28, 28), (246, 246, 246), "");
    static readonly Shade InCursorShade = new((20, 20, 20), (255, 255, 255), "");

    static readonly Rgb White = (255, 255, 255);

    readonly Depth depth;

    Palette(Depth depth) => this.depth = depth;

    public static Palette None { get; } = new(Depth.None);

    public static Palette Dark { get; } = new(Depth.Dark);

    public static Palette Light { get; } = new(Depth.Light);

    public static Palette Basic { get; } = new(Depth.Basic);

    // No colour when `NO_COLOR` is set, the stream is not a terminal, or the terminal does not say what it is or says
    // it is a dumb one.
    public static Palette Detect(bool isTerminal, Func<string, string?> variable)
    {
        if (!isTerminal || !string.IsNullOrEmpty(variable("NO_COLOR")) || variable("TERM") is null or "" or "dumb")
            return None;
        if (variable("COLORTERM") is not ("truecolor" or "24bit"))
            return Basic;

        // Terminals that tell their colours do it as `foreground;background`; 7 and 15 are the light backgrounds.
        var background = variable("COLORFGBG")?.Split(';')[^1];
        return background is "7" or "15" ? Light : Dark;
    }

    // Whether the palette has colours to draw a cursor of its own with.
    public bool DrawsCursor => depth != Depth.None;

    // The cell of a screen's own cursor at a moment of its blink: `glow` goes from 1, a block in the accent with what
    // stands in the cell drawn over it, down to 0, where the cell is as if there were no cursor, and back. Between
    // them the block fades toward the ground of the terminal and the character toward the colour of any text.
    // A terminal of sixteen colours reverses the cell for the brighter half of the blink.
    public string Cursor(string text, double glow)
    {
        if (depth == Depth.None || glow <= Faint)
            return text;
        if (depth == Depth.Basic)
            return glow >= 0.5 ? $"\u001b[7m{text}{Reset}" : text;

        return $"\u001b[38;2;{Mix(TextShade, InCursorShade, glow)};48;2;{Mix(GroundShade, AccentShade, glow)}m{text}{Reset}";
    }

    // The chosen line of a screen in the same blink: its text is in the accent at 1 and in the colour of any text
    // at 0, so that the line is read as well at every moment.
    public string Glow(string text, double glow)
    {
        if (depth == Depth.None || text.Length == 0 || glow <= Faint)
            return text;
        if (depth == Depth.Basic)
            return glow >= 0.5 ? Accent(text) : text;

        return $"\u001b[38;2;{Mix(TextShade, AccentShade, glow)}m{text}{Reset}";
    }

    // A dot of a wait as the light that runs along the dots has it: in the accent at 1 and in the muted colour of
    // the rest at 0. A terminal of sixteen colours has the accent for the brighter half.
    public string Spark(string text, double glow)
    {
        if (depth == Depth.None)
            return text;
        if (glow <= Faint || depth == Depth.Basic)
            return glow >= 0.5 ? Accent(text) : Muted(text);

        return $"\u001b[38;2;{Mix(MutedShade, AccentShade, glow)}m{text}{Reset}";
    }

    // Below this the blink is off: what blinks is drawn as it is, in the colours the terminal itself has.
    const double Faint = 0.02;

    // The truecolor between two shades, of the terminal of the palette, at a moment of a blink.
    string Mix(Shade from, Shade to, double glow)
    {
        var (low, high) = (Of(from), Of(to));
        int Between(int start, int end) => (int)Math.Round(start + ((end - start) * Math.Clamp(glow, 0, 1)));
        return Told((Between(low.Red, high.Red), Between(low.Green, high.Green), Between(low.Blue, high.Blue)));
    }

    // The truecolor of a shade for the terminal of the palette.
    Rgb Of(Shade shade) => depth == Depth.Light ? shade.Light : shade.Dark;

    // The code that gives a text the colour of the shade, or that gives the shade to the ground under a text.
    string Code(Shade shade, bool ground = false) => depth == Depth.Basic ? shade.Basic : $"{(ground ? 48 : 38)};2;{Told(Of(shade))}";

    // A truecolor as a code of the terminal has it.
    static string Told(Rgb colour) => string.Create(CultureInfo.InvariantCulture, $"{colour.Red};{colour.Green};{colour.Blue}");

    public string Accent(string text) => Paint(Tone.Accent, text);

    public string Error(string text) => Paint(Tone.Error, text);

    public string Success(string text) => Paint(Tone.Success, text);

    public string Warning(string text) => Paint(Tone.Warning, text);

    public string Muted(string text) => Paint(Tone.Muted, text);

    // A text in the colour of its tone on the given ground. The ground is painted under spaces too.
    public string Paint(Tone tone, string text, Ground ground = Ground.None)
    {
        if (depth == Depth.None || text.Length == 0)
            return text;

        // The chosen tab is white on the deeper accent, the light one, in a dark terminal and in a light one; a
        // terminal of sixteen colours shows it reversed.
        if (ground == Ground.Chosen)
            return $"\u001b[{(depth == Depth.Basic ? "7" : $"{Bold};38;2;{Told(White)};48;2;{Told(AccentShade.Light)}")}m{text}{Reset}";

        var colour = tone switch
        {
            Tone.Accent => Code(AccentShade),
            Tone.Error => Code(ErrorShade),
            Tone.Success => Code(SuccessShade),
            Tone.Warning => Code(WarningShade),
            Tone.Muted => Code(MutedShade),
            Tone.Quiet => Code(QuietShade),
            Tone.Strong => Bold,
            _ => "",
        };
        var codes = string.Join(';', ((string[])[colour, ground == Ground.Bar ? Code(BarShade, ground: true) : ""]).Where(part => part.Length > 0));
        return codes.Length == 0 ? text : $"\u001b[{codes}m{text}{Reset}";
    }

    // What erases the rest of a line of the terminal to a ground: the terminal fills what it erases with the colour
    // of the moment.
    public string Erase(Ground ground)
    {
        var colour = depth == Depth.None || ground != Ground.Bar ? "" : Code(BarShade, ground: true);
        return colour.Length == 0 ? "\u001b[K" : $"\u001b[{colour}m\u001b[K{Reset}";
    }

    // A message of the tool: `toobusy:` at its start is muted and whatever stands between backticks, a command as a rule,
    // gets the accent. The text itself is the same with and without colour.
    public string Message(string text)
    {
        if (depth == Depth.None)
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

    // Which colours a palette has: none, the sixteen of any terminal, or truecolor for a dark or a light one.
    enum Depth
    {
        None,
        Basic,
        Dark,
        Light,
    }

    // A colour for a dark terminal and one for a light terminal, and the code of the nearest of the sixteen colours.
    sealed record Shade(Rgb Dark, Rgb Light, string Basic);
}
