using System.Text;

namespace TooBusy.Cli.Terminal;

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

    public string Accent(string text) => Paint(AccentShade, text);

    public string Error(string text) => Paint(ErrorShade, text);

    public string Success(string text) => Paint(SuccessShade, text);

    public string Warning(string text) => Paint(WarningShade, text);

    public string Muted(string text) => Paint(MutedShade, text);

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

    string Paint(Shade shade, string text) => code is null || text.Length == 0 ? text : $"\u001b[{code(shade)}m{text}{Reset}";

    sealed record Shade(string Dark, string Light, string Basic);
}
