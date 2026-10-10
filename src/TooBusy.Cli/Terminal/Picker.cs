namespace TooBusy.Cli.Terminal;

// A choice of a list: what it is called, what explains it, and what is to be noticed about it.
public sealed record Choice(string Name, string Detail = "", string Note = "");

// The lists of every screen: a pointer on the chosen row, the name of a row with what explains it after it, and
// marks above and below a list that does not fit. One look for the setup, the milestones and the menu.
public static class Picker
{
    // A list longer than this scrolls.
    public const int Rows = 8;

    const int LabelWidth = 16;

    // Asks to choose one of the choices, one under another, starting at the given one. Gives the index of the
    // chosen one, or null when Escape is pressed. `shortcut` lets a key choose at once.
    public static int? Pick(Page page, IReadOnlyList<Line> question, IReadOnlyList<Choice> choices, string keys = "", int at = 0, Func<ConsoleKey, int?>? shortcut = null)
    {
        at = Math.Clamp(at, 0, choices.Count - 1);
        var top = 0;
        // Names that something follows are as wide as the widest of them, so that what follows stands in a column.
        var widest = choices.Where(choice => choice.Detail.Length + choice.Note.Length > 0).Select(choice => choice.Name.Length).DefaultIfEmpty().Max();
        while (true)
        {
            var lines = new List<Line>();
            top = Window(lines, choices.Count, at, top, Rows, out var pointer, index =>
                Row(index == at, choices[index].Detail.Length + choices[index].Note.Length > 0 ? choices[index].Name.PadRight(widest) : choices[index].Name, choices[index].Detail, choices[index].Note));
            page.Draw(question, lines, keys.Length == 0 ? "↑↓ move · enter choose" : $"↑↓ move · enter choose · {keys}", chosen: pointer);

            var key = page.Read();
            if (key.Key is ConsoleKey.UpArrow or ConsoleKey.K)
                at = (at + choices.Count - 1) % choices.Count;
            else if (key.Key is ConsoleKey.DownArrow or ConsoleKey.J or ConsoleKey.Tab)
                at = (at + 1) % choices.Count;
            else if (key.Key == ConsoleKey.Enter)
                return at;
            else if (key.Key == ConsoleKey.Escape)
                return null;
            else if (shortcut?.Invoke(key.Key) is { } chosen)
                return chosen;
        }
    }

    // A row of a list: the pointer when it is on the row, the name, and after it what explains the name and what is
    // to be noticed about it.
    public static Line Row(bool pointed, string name, string detail = "", string note = "") => new(
        new Part((pointed ? "❯ " : "  ") + name, pointed ? Tone.Accent : Tone.Plain),
        new Part(detail.Length == 0 ? "" : "  " + detail, Tone.Muted),
        new Part(note.Length == 0 ? "" : "  " + note, Tone.Success));

    // Something that is settled, as every screen lists it: a mark, its name and its value.
    public static Line Answer(string label, string value) =>
        new(new Part("✔ ", Tone.Success), new Part(label.PadRight(LabelWidth) + " ", Tone.Muted), new Part(value));

    // The name of a question and, under it, what to do, or why the answer was refused.
    public static List<Line> Head(string label, string hint, string? reason = null) =>
        [Line.Of(label, Tone.Strong), .. reason is not null ? [Line.Of(reason, Tone.Error)] : hint.Length > 0 ? [Line.Of(hint, Tone.Muted)] : (Line[])[]];

    // The rows of a list around the one the pointer is on, as many as fit; gives the row the window starts at, and
    // in `marked` the line the pointer is on. A list that does not fit says above and below how many rows there are
    // beyond the window.
    public static int Window(List<Line> lines, int count, int at, int top, int rows, out int marked, Func<int, Line> row)
    {
        top = Math.Clamp(top, Math.Max(0, at - rows + 1), Math.Max(0, at));
        var below = count - top - rows;
        if (count > rows)
            lines.Add(top > 0 ? Line.Of($"  ↑ {top} more", Tone.Muted) : Line.Empty);
        marked = lines.Count + Math.Max(0, at - top);
        for (var index = top; index < Math.Min(count, top + rows); index++)
            lines.Add(row(index));
        if (count > rows)
            lines.Add(below > 0 ? Line.Of($"  ↓ {below} more", Tone.Muted) : Line.Empty);
        return top;
    }
}
