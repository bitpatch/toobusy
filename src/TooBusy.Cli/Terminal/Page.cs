namespace TooBusy.Cli.Terminal;

// What every screen of the tool shares: it takes the terminal and gives it back, it draws the body and the foot it
// is given around the question of the moment, and it reads the keys. The screen itself is entered with the first
// thing that is drawn; a tape can be unrolled over the terminal's own screen instead. Ctrl+C leaves the screen, but only when it
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

    // How often a page that waits for something looks whether a key was pressed.
    static readonly TimeSpan Glance = TimeSpan.FromMilliseconds(30);

    // The key that was pressed to leave and waits to be pressed again; null when none was.
    string? leaving;

    public Page(TextWriter output, Palette palette, TerminalDevice device, string title)
    {
        Coloured = palette.DrawsCursor;
        this.device = device;
        this.title = title;
        screen = new Screen(output, palette, device.Size);
        device.TakeControlC(true);
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

    // What leaving the page with Ctrl+C does, as the keys say it: more than leaving, while something is going on.
    public string Leaving { get; set; } = "exit";

    public int Width => device.Size().Width;

    // Whether more keys are typed already, as in a paste: nothing is drawn between them.
    public bool KeyWaiting => device.KeyWaiting();

    // What the page asks before it is left: a key that leaves was pressed once and waits to be pressed again. Null
    // when nothing is asked.
    public string? Asking => leaving is null ? null : $"press {leaving} again to {(leaving == "ctrl+c" ? Leaving : "exit")}";

    // What gives the terminal back from where the page has it now.
    public string Held => screen.Held;

    // Leaves the screen for a tape on the terminal's own screen, under the bar of the page. The next thing the
    // page draws takes the terminal back.
    public Tape Unroll() => screen.Unroll(title, Status);

    // Whether the page has colours: without them what a colour would tell has to be told by a mark.
    public bool Coloured { get; }

    // Draws the page with what is asked, the lines where the user acts, and the keys that act there. The caret is
    // where a text is typed among those lines, and `chosen` the line the pointer is on.
    // `waiting` is the line of dots that run while the page waits for something, and `running` such a line of the
    // question, for a page that shows something going on. `back` tells that the lines end with `Back`, which names
    // the key that goes back: the keys of the page are left out then. While more keys are waiting, as in a paste,
    // it does not draw.
    public void Draw(IReadOnlyList<Line> question, IReadOnlyList<Line> choice, string keys, Caret? caret = null, int? chosen = null, int? waiting = null, int? running = null, bool back = false)
    {
        if (device.KeyWaiting())
            return;

        var all = Asking is { } asking
            ? Line.Of(asking, Tone.Warning)
            : Hints(string.Join(" · ", ((string[])[keys, back ? "" : Keys, $"ctrl+c {Leaving}"]).Where(part => part.Length > 0)));
        screen.Draw(new Frame(title, Status, Body, question, choice, all, Foot, caret, chosen, waiting, running));
    }

    // A key, when one is pressed; null once what the page waits for is done. The page looks for a key again and
    // again, so that it can draw what changes meanwhile.
    public async Task<ConsoleKeyInfo?> ReadAsync(Task wake)
    {
        while (!device.KeyWaiting())
        {
            if (wake.IsCompleted)
                return null;

            await Task.WhenAny(wake, Task.Delay(Glance, CancellationToken.None));
        }

        return Read();
    }

    // Says what the page waits for, with the dots that run under it, for as long as the work takes, and gives what
    // the work gives. The user does not have to wait: where there is somewhere to go back to, `Back` stands under
    // the dots from the first moment, and Enter or Escape goes back; where Escape leaves the page it does so here
    // too, asked twice, and so does Ctrl+C. `Done` is false when the user did not wait: the work is told to stop
    // then, and nobody waits for it.
    public async Task<(bool Done, T? Value)> WaitAsync<T>(string text, Func<CancellationToken, Task<T>> work, CancellationToken cancellationToken)
    {
        using var stopping = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var back = Picker.GoesBack(this);
        void Show() => Draw(
            [],
            [Line.Of(text, Tone.Muted), Line.Of(new string(Screen.Dot, Screen.Dots), Tone.Muted), .. back ? [Picker.BackRow(true)] : (Line[])[]],
            back ? "enter choose" : "",
            chosen: back ? 2 : null,
            waiting: 1,
            back: back);

        Show();
        var working = work(stopping.Token);
        try
        {
            while (!working.IsCompleted && await ReadAsync(working) is { } key)
            {
                if (key.Key == ConsoleKey.Escape || (back && key.Key == ConsoleKey.Enter))
                    return (false, default);

                Show();
            }

            return (true, await working);
        }
        finally
        {
            // Work that was not waited for may never end: it is told to stop and left to it, and what it fails with
            // is nobody's any more.
            if (!working.IsCompleted)
            {
                await stopping.CancelAsync();
                _ = working.ContinueWith(static left => _ = left.Exception, CancellationToken.None, TaskContinuationOptions.OnlyOnFaulted, TaskScheduler.Default);
            }
        }
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
