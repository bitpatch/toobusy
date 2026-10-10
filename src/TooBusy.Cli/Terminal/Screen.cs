using System.Globalization;
using System.Text;

namespace TooBusy.Cli.Terminal;

// A piece of a line and what it is. A ground of its own goes before the ground of its line.
public readonly record struct Part(string Text, Tone Tone = Tone.Plain, Ground Ground = Ground.None);

// Where a text is typed: a line of the choice and a column in it.
public readonly record struct Caret(int Line, int Column);

// A line of a screen: its pieces from left to right.
public sealed record Line(params Part[] Parts)
{
    public static Line Empty { get; } = new();

    public static Line Of(string text, Tone tone = Tone.Plain) => new(new Part(text, tone));
}

// One picture of a screen, from top to bottom: the bar with the title and, at its right, the status; the body; the
// question, which is what is asked, then under a rule the choice, where the user acts, then under another rule the
// keys; and the foot. Two things show where the screen waits for the user, and both blink: the cursor, a block at the
// caret, where a text is typed, and the chosen line of the choice, the one the pointer is on. A frame may have either,
// both or none. When it is the user who waits, a line of the choice is a row of dots that a light runs along; a
// screen that shows something going on has such a row in its question instead. The foot is as many pieces as it has
// marks: it goes on to a line of its own when the window is too narrow for them all.
public sealed record Frame(
    string Title,
    string Status,
    IReadOnlyList<Line> Body,
    IReadOnlyList<Line> Question,
    IReadOnlyList<Line> Choice,
    Line Keys,
    Line Foot,
    Caret? Caret = null,
    int? Chosen = null,
    int? Waiting = null,
    int? Running = null);

// A screen of its own in the terminal: the alternate screen that programs like editors use. What the terminal showed
// before stays untouched under it and is back when the screen is closed. A frame is drawn whole every time; the body
// gives way when the window is short, its oldest lines first.
//
// The cursor is the screen's own: a block at the caret that glows in the accent and fades away to nothing, and the
// chosen line blinks with it, between the accent and the colour of any text. The blink is smooth and keeps its time
// whatever is drawn: a key does not start it again. The cursor of the terminal is hidden, because whether it blinks is up to the
// terminal. Only a palette without colours leaves the terminal's cursor in its place.
//
// The dots of a wait are lit by a light that runs from the first of them to the last and back: the dot it is on is in
// the accent, and those it has left fade behind it to the colour of the rest. Without colours the dots stand still.
public sealed class Screen(TextWriter output, Palette palette, Func<(int Width, int Height)> size)
{
    // The alternate screen, with a cursor that blinks.
    const string Enter = "\u001b[?1049h\u001b[1 q";
    const string HideCursor = "\u001b[?25l";
    const string ShowCursor = "\u001b[?25h";

    // What a program that is stopped while a screen is open must still write: the cursor and the terminal as they were.
    public const string Leave = "\u001b[0 q" + ShowCursor + "\u001b[?1049l";

    // The blink is counted in beats: so many of them take it from its brightest to nothing and back.
    public static readonly TimeSpan Beat = TimeSpan.FromMilliseconds(80);
    const int Beats = 20;

    // The dots of a wait: how many there are, what a dot is, how far the light goes in a beat, and for how many beats a dot it has
    // left goes on fading.
    public const int Dots = 7;
    public const char Dot = '•';
    const double Pace = 0.5;
    const int Fading = 6;

    readonly Lock drawing = new();
    Frame? last;

    // Where the cursor is drawn and what stands in its cell, and where the chosen line is and what it says;
    // null when there is none.
    (int Row, int Column, string Cell)? cursor;
    (int Row, Line Line, int Width)? chosen;
    (int Row, int Count)? waiting;
    int beat;
    int step;

    public void Open()
    {
        output.Write(Enter);
        output.Flush();
    }

    public void Close()
    {
        lock (drawing)
        {
            (last, cursor, chosen, waiting) = (null, null, null, null);
            output.Write(Leave);
            output.Flush();
        }
    }

    // Draws the last frame again: the size of the window has changed.
    public void Redraw()
    {
        lock (drawing)
        {
            if (last is not null)
                Draw(last);
        }
    }

    // Moves the blink a beat on and draws what blinks in its next shade.
    public void Pulse()
    {
        lock (drawing)
        {
            if ((cursor is null && chosen is null && waiting is null) || !palette.DrawsCursor)
                return;

            beat = (beat + 1) % Beats;
            step++;
            output.Write(Pulsing());
            output.Flush();
        }
    }

    public void Draw(Frame frame)
    {
        lock (drawing)
        {
            last = frame;
            (cursor, chosen, waiting) = (null, null, null);
            var (width, height) = size();
            width = Math.Max(20, width);

            // The bar is three lines and an empty one follows it; the foot, when there is one, is the last lines
            // with an empty one before them.
            var marks = Wrap(frame.Foot, width - 2);
            var foot = marks.Count > 0 ? marks.Count + 1 : 0;
            var rule = Row(Line.Of(" " + new string('─', width - 2), Tone.Muted), width, Ground.None);
            var panel = new List<string>();
            panel.AddRange(frame.Question.Select(line => Inset(line, width, Ground.None)));
            panel.Add(rule);
            var choice = panel.Count;
            panel.AddRange(frame.Choice.Select(line => Inset(line, width, Ground.None)));
            panel.Add(rule);
            panel.Add(Inset(frame.Keys, width, Ground.None));

            // A window too short for the panel loses the top of it; the body goes before that.
            var cut = Math.Max(0, panel.Count - Math.Max(1, height - 4 - foot));
            var room = Math.Max(0, height - 4 - foot - (panel.Count - cut));
            var body = frame.Body.Skip(Math.Max(0, frame.Body.Count - room)).ToList();

            var rows = new List<string> { Row(Line.Empty, width, Ground.Bar), Bar(frame.Title, frame.Status, width), Row(Line.Empty, width, Ground.Bar), "" };
            rows.AddRange(body.Select(line => Inset(line, width, Ground.None)));
            rows.AddRange(Enumerable.Repeat("", room - body.Count));
            var first = rows.Count;
            rows.AddRange(panel.Skip(cut));
            if (foot > 0)
                rows.Add("");
            rows.AddRange(marks.Select(line => Inset(line, width, Ground.Bar)));

            var text = new StringBuilder(HideCursor).Append("\u001b[H");
            for (var row = 0; row < rows.Count; row++)
                text.Append(rows[row]).Append("\u001b[K").Append(row < rows.Count - 1 ? "\r\n" : "");
            text.Append("\u001b[J");

            if (frame.Chosen is { } pointed && pointed < frame.Choice.Count && choice + pointed - cut >= 0)
                chosen = (first + choice + pointed - cut + 1, frame.Choice[pointed], width);
            if (frame.Caret is var (line, column) && line < frame.Choice.Count && choice + line - cut is >= 0 and var shown && column + 2 <= width)
            {
                var under = string.Concat(frame.Choice[line].Parts.Select(part => part.Text));
                cursor = (first + shown + 1, column + 2, column < under.Length ? under[column].ToString() : " ");
            }

            if (frame.Waiting is { } dotted && dotted < frame.Choice.Count && choice + dotted - cut >= 0)
                waiting = (first + choice + dotted - cut + 1, Math.Min(width - 1, frame.Choice[dotted].Parts.Sum(part => part.Text.Length)));
            else if (frame.Running is { } going && going < frame.Question.Count && going - cut >= 0)
                waiting = (first + going - cut + 1, Math.Min(width - 1, frame.Question[going].Parts.Sum(part => part.Text.Length)));

            text.Append(Pulsing());
            output.Write(text);
            output.Flush();
        }
    }

    // How brightly a dot of a wait is lit at a step, from 0, the colour of the rest, to 1, the accent. The light is
    // at its brightest on the dot it is on, and a dot goes on fading for a few beats after the light has left it.
    public static double Spark(int dot, int count, int step)
    {
        // The way of the light is there and back: as far again as the row is long.
        var way = Math.Max(1, 2 * (count - 1));
        var glow = 0.0;
        for (var ago = 0; ago <= Fading; ago++)
        {
            var gone = (((step - ago) * Pace % way) + way) % way;
            var at = gone <= count - 1 ? gone : way - gone;
            glow = Math.Max(glow, (1 - ((double)ago / (Fading + 1))) * Math.Max(0, 1 - Math.Abs(at - dot)));
        }

        return glow;
    }

    // The chosen line and the cursor as the blink has them at the moment. Without colours nothing blinks: the cursor
    // of the terminal stands where the text is typed, or on the pointer of the chosen line.
    string Pulsing()
    {
        static string At(int row, int column) => string.Create(CultureInfo.InvariantCulture, $"\u001b[{row};{column}H");

        if (!palette.DrawsCursor)
            return cursor is var (row, column, _) ? At(row, column) + ShowCursor : chosen is { } on ? At(on.Row, 2) + ShowCursor : "";

        var glow = (1 + Math.Cos(2 * Math.PI * beat / Beats)) / 2;
        var text = new StringBuilder();
        // Of the chosen line only its first piece blinks, the pointer with the name: what explains the name after
        // it stays as the frame drew it.
        if (chosen is { Line.Parts: [var name, ..] } pointed)
        {
            var room = pointed.Width - 1;
            var piece = name.Text.Length <= room ? name.Text : room > 0 ? name.Text[..(room - 1)] + "…" : "";
            text.Append(At(pointed.Row, 2)).Append(palette.Glow(piece, glow));
        }

        if (cursor is { } typed)
            text.Append(At(typed.Row, typed.Column)).Append(palette.Cursor(typed.Cell, glow));

        if (waiting is var (line, count))
        {
            text.Append(At(line, 2));
            for (var dot = 0; dot < count; dot++)
                text.Append(palette.Spark(Dot.ToString(), Spark(dot, count, step)));
        }

        return text.ToString();
    }

    // The title with the status at the right edge; the status gives way to the title when both do not fit.
    string Bar(string title, string status, int width)
    {
        var left = title.Length + 2;
        var right = status.Length + 2;
        return left + 2 + right > width || status.Length == 0
            ? Row(new Line(new Part("  " + title, Tone.Strong)), width, Ground.Bar)
            : Row(new Line(new Part("  " + title, Tone.Strong), new Part(new string(' ', width - left - right)), new Part(status, Tone.Muted)), width, Ground.Bar);
    }

    // The pieces of a line on as many lines as the room asks for; a piece is never broken.
    static List<Line> Wrap(Line line, int room)
    {
        var lines = new List<Line>();
        var now = new List<Part>();
        var taken = 0;
        foreach (var part in line.Parts)
        {
            if (taken > 0 && taken + part.Text.Length > room)
            {
                lines.Add(new Line([.. now]));
                now.Clear();
                taken = 0;
            }

            now.Add(part);
            taken += part.Text.Length;
        }

        if (now.Count > 0)
            lines.Add(new Line([.. now]));
        return lines;
    }

    // A line that starts a column in from the edge of the window.
    string Inset(Line line, int width, Ground ground) => Row(new Line([new Part(" "), .. line.Parts]), width, ground);

    // The line cut to the width, each piece in its colour. On a ground the line is filled to the width.
    string Row(Line line, int width, Ground ground)
    {
        var room = width;
        var row = new StringBuilder();
        foreach (var part in line.Parts)
        {
            if (room == 0)
                break;

            var text = part.Text.Length <= room ? part.Text : part.Text[..(room - 1)] + "…";
            room -= text.Length;
            row.Append(palette.Paint(part.Tone, text, part.Ground == Ground.None ? ground : part.Ground));
        }

        if (ground != Ground.None && room > 0)
            row.Append(palette.Paint(Tone.Plain, new string(' ', room), ground));
        return row.ToString();
    }
}
