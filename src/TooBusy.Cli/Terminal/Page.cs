namespace TooBusy.Cli.Terminal;

// What every screen of the tool shares: it is opened over the terminal and closed again, it draws the body and the
// foot it is given around the question of the moment, and it reads the keys. Ctrl+C leaves the screen, but only when it
// is pressed twice in a row: the first one asks, any other key takes the question back. Leaving this way throws
// OperationCanceledException, which the command that opened the page catches. Escape is asked about in the same way
// where the page is told that it leaves: the first one is read as a key that means nothing.
public sealed class Page : IDisposable
{
    readonly Screen screen;
    readonly TerminalDevice device;
    readonly string title;
    readonly IDisposable? watching;
    readonly IDisposable? pulsing;

    // A key that means nothing to any question: what the first Escape that would leave the page is read as.
    static readonly ConsoleKeyInfo Nothing = new('\0', ConsoleKey.NoName, shift: false, alt: false, control: false);

    // The key that was pressed to leave and waits to be pressed again; null when none was.
    string? leaving;

    public Page(TextWriter output, Palette palette, TerminalDevice device, string title)
    {
        Coloured = palette.DrawsCursor;
        this.device = device;
        this.title = title;
        screen = new Screen(output, palette, device.Size);
        device.TakeControlC(true);
        screen.Open();
        watching = device.WatchSize(screen.Redraw);
        pulsing = device.Every(Screen.Beat, screen.Pulse);
    }

    public IReadOnlyList<Line> Body { get; set; } = [];

    // What the page is doing, at the right of the bar.
    public string Status { get; set; } = "";

    // The keys that work everywhere on the page; they follow the keys of the question, and `ctrl+c exit` follows them.
    public string Keys { get; set; } = "";

    // Whether Escape leaves the page from where it is now, and so is asked about like Ctrl+C.
    public bool EscapeLeaves { get; set; }

    public Line Foot { get; set; } = Line.Empty;

    public int Width => device.Size().Width;

    // Whether the page has colours: without them what a colour would tell has to be told by a mark.
    public bool Coloured { get; }

    // Draws the page with what is asked, the lines where the user acts, and the keys that act there. The caret is
    // where a text is typed among those lines, and `chosen` the line the pointer is on.
    // While more keys are waiting, as in a paste, it does not draw.
    public void Draw(IReadOnlyList<Line> question, IReadOnlyList<Line> choice, string keys, Caret? caret = null, int? chosen = null)
    {
        if (device.KeyWaiting())
            return;

        var all = leaving is not null
            ? Line.Of($"press {leaving} again to exit", Tone.Warning)
            : Hints(string.Join(" · ", ((string[])[keys, Keys, "ctrl+c exit"]).Where(part => part.Length > 0)));
        screen.Draw(new Frame(title, Status, Body, question, choice, all, Foot, caret, chosen));
    }

    // Hints as `key what it does · key what it does`: the name of each key is a little lighter than the rest,
    // so that the eye finds it.
    public static Line Hints(string hints) => new([.. hints.Split(" · ").SelectMany((hint, index) =>
    {
        var name = hint.Split(' ')[0];
        return (Part[])[new Part(index == 0 ? "" : " · ", Tone.Muted), new Part(name, Tone.Quiet), new Part(hint[name.Length..], Tone.Muted)];
    })]);

    public ConsoleKeyInfo Read()
    {
        var key = device.ReadKey();
        var asked = key.Key == ConsoleKey.C && key.Modifiers.HasFlag(ConsoleModifiers.Control) ? "ctrl+c"
            : key.Key == ConsoleKey.Escape && EscapeLeaves ? "esc"
            : null;
        if (asked is not null && asked == leaving)
        {
            // The second Ctrl+C leaves from here. The second Escape is given to the question, which goes back out of the page.
            leaving = null;
            return asked == "esc" ? key : throw new OperationCanceledException("The screen was left with Ctrl+C.");
        }

        leaving = asked;
        return asked == "esc" ? Nothing : key;
    }

    public void Dispose()
    {
        pulsing?.Dispose();
        watching?.Dispose();
        screen.Close();
        device.TakeControlC(false);
    }
}
