using System.Text.RegularExpressions;
using TooBusy.Cli.Terminal;

namespace TooBusy.Cli.Tests;

// A terminal for a test: scripted keys, a size, and what was written to it. It reads a page back as the lines of
// the last frame that was drawn, without the escape sequences and the spaces that fill a line.
public sealed partial class TestTerminal : IDisposable
{
    public TestTerminal(int width = 80, int height = 20)
    {
        Device = new TerminalDevice(Keys.Read, () => (width, height))
        {
            TakeControlC = taken => ControlCTaken = taken,
            WatchSize = changed =>
            {
                Resize = changed;
                return null;
            },
            Every = (time, tick) =>
            {
                (Beat, Tick) = (time, tick);
                return null;
            },
        };
    }

    public StringWriter Output { get; } = new();

    public Keys Keys { get; } = new();

    public TerminalDevice Device { get; set; }

    public bool ControlCTaken { get; private set; }

    // What a page asked to be called when the size of the window changes.
    public Action? Resize { get; private set; }

    // What a page asked to be called again and again, and how often.
    public Action? Tick { get; private set; }

    public TimeSpan Beat { get; private set; }

    // How many frames were drawn.
    public int Frames => Output.ToString().Split("\u001b[H").Length - 1;

    public string[] Frame => Lines(Output.ToString().Split("\u001b[H")[^1]);

    // The frame without its empty lines and its rules, as one text.
    public string Text => Told(Frame);

    // Fails unless some frame, the last one or one before it, had the text; a frame is read without its empty lines
    // and its rules.
    public void AssertSaw(string text) => Assert.True(Saw(text), $"No frame had:\n{text}\n\nThe last one was:\n{Text}");

    // Whether some frame had the text, read as `AssertSaw` reads it.
    public bool Saw(string text) =>
        Output.ToString().Split("\u001b[H").Skip(1).Any(frame => Told(Lines(frame)).Contains(text, StringComparison.Ordinal));

    // The line and the column the cursor was put at, counted from one; null when it is hidden.
    public (int Line, int Column)? Caret =>
        Position().Match(Output.ToString().Split("\u001b[H")[^1]) is { Success: true } match ? (int.Parse(match.Groups[1].Value), int.Parse(match.Groups[2].Value)) : null;

    public Page Open(string title = "toobusy · ~/rocket", Palette? palette = null) => new(Output, palette ?? Palette.None, Device, title);

    public void Dispose() => Output.Dispose();

    static string Told(string[] lines) => string.Join('\n', lines.Where(line => line.Trim(' ', '─').Length > 0));

    static string[] Lines(string frame) => [.. Escape().Replace(frame, "").Split("\r\n").Select(line => line.TrimEnd())];

    [GeneratedRegex(@"\u001b\[[0-9;?]* ?[A-Za-z]")]
    private static partial Regex Escape();

    [GeneratedRegex(@"\u001b\[(\d+);(\d+)H")]
    private static partial Regex Position();
}
