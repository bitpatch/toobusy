using System.Text.RegularExpressions;
using TooBusy.Cli.Terminal;

namespace TooBusy.Cli.Tests;

// A terminal for a test: scripted keys, a size, and what was written to it. It reads a page back as the lines of
// the last frame that was drawn, without the escape sequences and the spaces that fill a line, and a tape as the
// rows the terminal's own screen has after it.
public sealed partial class TestTerminal : IDisposable
{
    public TestTerminal(int width = 80, int height = 20)
    {
        Tape = new Scroll(width, height);
        Output = new Recorded(Tape);
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

    public StringWriter Output { get; }

    // The terminal's own screen: what a tape has written there, and what was printed around it.
    public Scroll Tape { get; }

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

    // Whether some frame had the text, read as `AssertSaw` reads it, or the terminal's own screen had it at some
    // moment.
    public bool Saw(string text) =>
        Output.ToString().Split("\u001b[H").Skip(1).Any(frame => Told(Lines(frame)).Contains(text, StringComparison.Ordinal))
        || ((Recorded)Output).Seen.Any(seen => seen.Contains(text, StringComparison.Ordinal));

    // Fails unless the terminal's own screen had the text at some moment.
    public void AssertTaped(string text) =>
        Assert.True(((Recorded)Output).Seen.Any(seen => seen.Contains(text, StringComparison.Ordinal)), $"The tape never had:\n{text}\n\nIt has:\n{Tape.Text}");

    // The line and the column the cursor was put at, counted from one; null when it is hidden.
    public (int Line, int Column)? Caret =>
        Position().Match(Output.ToString().Split("\u001b[H")[^1]) is { Success: true } match ? (int.Parse(match.Groups[1].Value), int.Parse(match.Groups[2].Value)) : null;

    public Page Open(string title = "toobusy · ~/rocket", Palette? palette = null) => new(Output, palette ?? Palette.None, Device, title);

    public void Dispose() => Output.Dispose();

    static string Told(string[] lines) => string.Join('\n', lines.Where(line => line.Trim(' ', '─').Length > 0));

    static string[] Lines(string frame) => [.. Escape().Replace(frame, "").Split("\r\n").Select(line => line.TrimEnd())];

    [GeneratedRegex(@"\u001b\[[0-9;?]* ?[A-Za-z]")]
    private static partial Regex Escape();

    // What is written, kept as text and read by the terminal's own screen as it comes. Every text that screen has
    // had is remembered, so that a test can ask for what stood there a moment ago.
    sealed class Recorded(Scroll scroll) : StringWriter
    {
        int read;

        // How deep the writing is: a text may be written piece by piece, and is read when it is whole.
        int writing;

        public List<string> Seen { get; } = [];

        public override void Write(char value) => Whole(() => base.Write(value));

        public override void Write(char[] buffer, int index, int count) => Whole(() => base.Write(buffer, index, count));

        public override void Write(string? value) => Whole(() => base.Write(value));

        public override void Write(System.Text.StringBuilder? value) => Whole(() => base.Write(value));

        public override void Write(ReadOnlySpan<char> buffer)
        {
            writing++;
            base.Write(buffer);
            writing--;
            Read();
        }

        void Whole(Action write)
        {
            writing++;
            write();
            writing--;
            Read();
        }

        void Read()
        {
            var written = GetStringBuilder();
            if (writing > 0 || written.Length == read)
                return;

            scroll.Write(written.ToString(read, written.Length - read));
            read = written.Length;
            if (!scroll.Alternate && scroll.Text is var text && (Seen.Count == 0 || Seen[^1] != text))
                Seen.Add(text);
        }
    }

    [GeneratedRegex(@"\u001b\[(\d+);(\d+)H")]
    private static partial Regex Position();
}
