using System.Globalization;
using System.Text;

namespace TooBusy.Cli.Terminal;

// The foot of a tape: the lines under what the tape has said for good, drawn again and again. Like a frame of a
// screen it may have a cursor at a caret and a chosen line, which blink. Its mark is where something goes on: the
// line and the column of a sign that turns: a full cell of dots with a gap that runs around it.
public sealed record Strip(IReadOnlyList<Line> Lines, Caret? Mark = null, Caret? Caret = null, int? Chosen = null)
{
    public static Strip Empty { get; } = new([]);
}

// A tape: the terminal's own screen, written from the top down under a bar that is printed once. What is settled is
// written for good and scrolls up into the history of the terminal, the bar with it, so that the terminal scrolls
// back to it. Under the settled lines stands the foot, which is drawn over itself: the cursor goes up to its first
// line, every line is written again, and what is left of a longer foot is erased. Nothing is erased above it while
// the window keeps its size.
//
// No line is let wrap: a line the terminal broke would make the foot taller than it is counted, and every drawing
// would leave a line of it behind. Lines are cut a column short of the window, and the terminal is told not to wrap
// for as long as the tape is unrolled. A foot taller than the window loses its first lines.
//
// A window that changes its size is not drawn over: a terminal folds or cuts what is on it as it likes, and the
// foot is not found again. The tape remembers what it has settled instead, not as lines but as what lays them out
// to a width. It then erases the terminal, its history too, and writes all of it anew to the new width: the bar,
// every settled line and the foot. What the terminal had in its history before the tape is gone with it.
//
// What blinks is written a beat at a time where it stands, counted from the line the cursor rests on, and the cursor
// comes back there. Without colours nothing blinks: the mark is as it is drawn, and the terminal's own cursor stands
// at the caret, or on the pointer of the chosen line.
public sealed class Tape
{
    // What a program that is stopped while a tape is unrolled must still write: lines that wrap again, the cursor
    // as it was, and a line of its own for what the terminal writes next.
    public const string Rescue = "\u001b[?7h" + Screen.Shown + "\r\n";

    // The sign of the mark as the gap goes around it, a beat at a time.
    static readonly string[] Signs = ["⣾", "⣷", "⣯", "⣟", "⡿", "⢿", "⣻", "⣽"];

    readonly TextWriter output;
    readonly Palette palette;
    readonly Func<(int Width, int Height)> size;
    readonly Lock drawing;
    readonly string title;
    readonly string status;

    // What is settled, in its order: each piece gives its lines for a width.
    readonly List<Func<int, IReadOnlyList<Line>>> said = [];

    Strip foot = Strip.Empty;
    (int Width, int Height) drawn;

    // How many lines the cursor rests under the first line of the foot.
    int above;
    double glow = 1;
    int step;

    internal Tape(TextWriter output, Palette palette, Func<(int Width, int Height)> size, Lock drawing, string before, string title, string status)
    {
        (this.output, this.palette, this.size, this.drawing, this.title, this.status) = (output, palette, size, drawing, title, status);
        drawn = size();
        output.Write($"{Screen.HideCursor}{before}\u001b[?7l\u001b[1 q{Bar(Math.Max(20, drawn.Width))}");
        output.Flush();
    }

    // The bar: three lines of its ground and an empty one, as on a screen. The ground is not written cell by cell:
    // what the terminal erases of a line it fills with it.
    string Bar(int width)
    {
        var fill = palette.Erase(Ground.Bar);
        var bar = Screen.Row(palette, Screen.Bar(title, status, width), width - 1, Ground.Bar, fill: false);
        return $"{fill}\r\n{bar}{fill}\r\n{fill}\r\n\r\n";
    }

    public bool Closed { get; private set; }

    // How many lines a foot may have in the window as it is.
    public int Room => Math.Max(1, size().Height - 1);

    public int Width => Math.Max(20, size().Width);

    // Writes the lines that are settled, for good, and the foot under them in the place of the one before.
    public void Draw(IReadOnlyList<Line> settled, Strip foot) => DrawFitted(settled.Count == 0 ? [] : [_ => settled], foot);

    // The same for what is settled as pieces that lay their lines out to a width: a window that changes its size
    // gets them laid out anew.
    public void DrawFitted(IReadOnlyList<Func<int, IReadOnlyList<Line>>> settled, Strip foot)
    {
        lock (drawing)
        {
            if (Closed)
                return;

            // In a window of another size all is written anew, on a terminal that is erased with its history.
            var anew = size() != drawn;
            drawn = size();
            var width = Math.Max(20, drawn.Width);

            // A foot that does not fit loses its first lines, and what points into it moves with them.
            var cut = Math.Max(0, foot.Lines.Count - Math.Max(1, drawn.Height - 1));
            Caret? Moved(Caret? at) => at is var (line, column) && line >= cut ? new Caret(line - cut, column) : null;
            this.foot = cut == 0 ? foot : new Strip([.. foot.Lines.Skip(cut)], Moved(foot.Mark), Moved(foot.Caret), foot.Chosen >= cut ? foot.Chosen - cut : null);

            var text = new StringBuilder(Screen.HideCursor);
            text.Append(anew ? "\u001b[H\u001b[2J\u001b[3J" + Bar(width) : "\r" + Up(above));
            foreach (var line in (anew ? said.Concat(settled) : settled).SelectMany(piece => piece(width)))
                text.Append(Row(line, width)).Append("\u001b[K\r\n");
            said.AddRange(settled);
            for (var row = 0; row < this.foot.Lines.Count; row++)
                text.Append(Row(this.foot.Lines[row], width)).Append(row < this.foot.Lines.Count - 1 ? "\u001b[K\r\n" : "");
            text.Append("\u001b[J");
            above = Math.Max(0, this.foot.Lines.Count - 1);

            text.Append(Pulsing());
            output.Write(text);
            output.Flush();
        }
    }

    // Erases the foot and gives the terminal its lines that wrap back. What is written next stands under the last
    // settled line.
    public void Close()
    {
        lock (drawing)
        {
            if (Closed)
                return;

            Closed = true;
            output.Write($"\r{Up(above)}\u001b[J\u001b[?7h");
            output.Flush();
            (above, foot) = (0, Strip.Empty);
        }
    }

    // Draws the tape again: the size of the window has changed.
    internal void Redraw() => DrawFitted([], foot);

    // Draws what blinks in the shade of the beat, and the mark as the step has it. A window that has changed its
    // size meanwhile is drawn anew.
    internal void Pulse(double glow, int step)
    {
        (this.glow, this.step) = (glow, step);
        if (size() != drawn)
        {
            Redraw();
            return;
        }

        var text = Pulsing();
        if (text.Length == 0)
            return;

        output.Write(text);
        output.Flush();
    }

    // The sign of the mark at a step: the gap is a dot further around the cell with every one.
    public static string Sign(int step) => Signs[((step % Signs.Length) + Signs.Length) % Signs.Length];

    // What blinks as the blink has it at the moment. Without colours it is the terminal's cursor, which stays where
    // it is put: the cursor then rests on that line, and not on the last one.
    string Pulsing()
    {
        if (!palette.DrawsCursor)
        {
            var at = foot.Caret ?? (foot.Chosen is { } pointed ? new Caret(pointed, 0) : null);
            if (at is not var (line, column) || line >= foot.Lines.Count)
                return "";

            var up = above - line;
            above = line;
            return $"\r{Up(up)}{Column(column)}{Screen.ShowCursor}";
        }

        var text = new StringBuilder();
        void Put(int line, int column, string painted)
        {
            if (line < foot.Lines.Count && column + 2 < Math.Max(20, drawn.Width))
                text.Append('\r').Append(Up(above - line)).Append(Column(column)).Append(painted).Append('\r').Append(Down(above - line));
        }

        if (foot.Mark is var (marked, sign))
            Put(marked, sign, palette.Accent(Sign(step)));

        // Of the chosen line only its first piece blinks, the pointer with the name, as on a screen.
        if (foot.Chosen is { } chosen && chosen < foot.Lines.Count && foot.Lines[chosen].Parts is [var name, ..])
        {
            var room = Math.Max(20, drawn.Width) - 2;
            Put(chosen, 0, palette.Glow(name.Text.Length <= room ? name.Text : name.Text[..(room - 1)] + "…", glow));
        }

        if (foot.Caret is var (typed, where) && typed < foot.Lines.Count)
        {
            var under = string.Concat(foot.Lines[typed].Parts.Select(part => part.Text));
            Put(typed, where, palette.Cursor(where < under.Length ? under[where].ToString() : " ", glow));
        }

        return text.ToString();
    }

    // A line of the tape: a column in from the edge, and a column short of the window.
    string Row(Line line, int width) => Screen.Row(palette, new Line([new Part(" "), .. line.Parts]), width - 1, Ground.None, fill: false);

    // The cursor so many lines up or down; nothing for none, which a terminal would read as one.
    static string Up(int lines) => lines > 0 ? string.Create(CultureInfo.InvariantCulture, $"\u001b[{lines}A") : "";

    static string Down(int lines) => lines > 0 ? string.Create(CultureInfo.InvariantCulture, $"\u001b[{lines}B") : "";

    // The cursor at a column of a line of the tape, counted from zero after the edge.
    static string Column(int column) => string.Create(CultureInfo.InvariantCulture, $"\u001b[{column + 2}G");
}
