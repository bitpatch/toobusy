namespace TooBusy.Cli.Terminal;

// A choice of a list: what it is called, what explains it, and what is to be noticed about it. A choice that is off
// cannot be chosen: it is muted, and Enter does nothing on it. One that waits has the dots of a wait where what is
// to be noticed about it will stand.
public sealed record Choice(string Name, string Detail = "", string Note = "", bool Off = false, bool Waits = false);

// The lists of every screen: a pointer on the chosen row, the name of a row with what explains it after it, and
// marks above and below a list that does not fit. One look for the setup, the milestones and the menu.
public static class Picker
{
    // A list longer than this scrolls.
    public const int Rows = 8;

    // The last row of every list that there is somewhere to go back from, and the key that does what the row does:
    // the row names it at its right, so the keys of the page do not name it again.
    public const string Back = "Back";
    public const string BackKey = "esc";

    const int LabelWidth = 16;

    // Whether a list on the page is to end with `Back`: it is, unless Escape leaves the page from here.
    public static bool GoesBack(Page page) => !page.EscapeLeaves;

    // Asks to choose one of the choices, one under another, starting at the given one. Gives the index of the
    // chosen one, or null when Escape is pressed. `shortcut` lets a key choose at once. Wherever Escape goes back
    // and does not leave the page, the list ends with `Back`, which does what Escape does.
    public static int? Pick(Page page, IReadOnlyList<Line> question, IReadOnlyList<Choice> choices, string keys = "", int at = 0, Func<ConsoleKey, int?>? shortcut = null)
    {
        var list = new Listed(page, question, keys, at);
        while (true)
        {
            list.Draw(choices);
            if (list.Press(page.Read(), shortcut) is (true, var chosen))
                return chosen;
        }
    }

    // The same list on a page where something goes on meanwhile: the choices are asked for again, and the list is
    // drawn anew, whenever what `changed` gives is done. `off` is told when Enter is pressed on a choice that is off.
    public static async Task<int?> PickAsync(Page page, IReadOnlyList<Line> question, Func<IReadOnlyList<Choice>> choices, Func<Task> changed, Action<int>? off = null)
    {
        var list = new Listed(page, question, "", 0);
        while (true)
        {
            // Whether anything still goes on is looked at before the choices are asked for: what is done between the
            // two is seen by the wait for a key, and the list is drawn again.
            var wake = changed();
            var settled = wake.IsCompleted;
            list.Draw(choices());

            // Once nothing goes on any more the page waits for a key as any list does.
            if ((settled ? (ConsoleKeyInfo?)page.Read() : await page.ReadAsync(wake)) is not { } key)
                continue;
            if (list.Press(key, null) is (true, var chosen))
                return chosen;
            if (key.Key == ConsoleKey.Enter)
                off?.Invoke(list.At);
        }
    }

    // A row of a list: the pointer when it is on the row, the name, and after it what explains the name and what is
    // to be noticed about it.
    public static Line Row(bool pointed, string name, string detail = "", string note = "") => Row(pointed, new Choice(name, detail, note), name);

    // The row that goes back, with its key at its right.
    public static Line BackRow(bool pointed) => Row(pointed, Back, BackKey);

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

    // The row of a choice, its name as wide as the list wants it. What is off is muted all through.
    static Line Row(bool pointed, Choice choice, string name) => new(
        new Part((pointed ? "❯ " : "  ") + name, choice.Off ? Tone.Muted : pointed ? Tone.Accent : Tone.Plain),
        new Part(choice.Detail.Length == 0 ? "" : "  " + choice.Detail, Tone.Muted),
        new Part(choice.Note.Length == 0 ? "" : "  " + choice.Note, choice.Off ? Tone.Muted : Tone.Success),
        new Part(choice.Waits ? "  " + new string(Screen.Dot, Screen.Dots) : "", Tone.Muted));

    // Whether something stands after the name of the choice.
    static bool Followed(Choice choice) => choice.Detail.Length + choice.Note.Length > 0 || choice.Waits;

    // A list on a page: where its pointer is and what of it is in the window, from one drawing to the next.
    sealed class Listed(Page page, IReadOnlyList<Line> question, string keys, int at)
    {
        IReadOnlyList<Choice> choices = [];
        bool back;
        int top;

        public int At { get; private set; } = at;

        public void Draw(IReadOnlyList<Choice> offered)
        {
            back = GoesBack(page);
            choices = back ? [.. offered, new Choice(Back, BackKey)] : offered;
            At = Math.Clamp(At, 0, choices.Count - 1);

            // Names that something follows are as wide as the widest of them, so that what follows stands in a column.
            var widest = choices.Where(Followed).Select(choice => choice.Name.Length).DefaultIfEmpty().Max();
            var lines = new List<Line>();
            int? waits = null;
            top = Window(lines, choices.Count, At, top, Rows, out var pointer, index =>
            {
                if (choices[index].Waits)
                    waits = lines.Count;
                return Row(index == At, choices[index], Followed(choices[index]) ? choices[index].Name.PadRight(widest) : choices[index].Name);
            });

            // A choice that is off does not blink under the pointer: it does not wait to be chosen.
            page.Draw(question, lines, keys.Length == 0 ? "↑↓ move · enter choose" : $"↑↓ move · enter choose · {keys}", chosen: choices[At].Off ? null : pointer, waiting: waits, back: back);
        }

        // What the key does to the list: `Done` when the list is over, with the index of what was chosen, null for
        // going back.
        public (bool Done, int? Chosen) Press(ConsoleKeyInfo key, Func<ConsoleKey, int?>? shortcut)
        {
            if (key.Key is ConsoleKey.UpArrow or ConsoleKey.K)
                At = (At + choices.Count - 1) % choices.Count;
            else if (key.Key is ConsoleKey.DownArrow or ConsoleKey.J or ConsoleKey.Tab)
                At = (At + 1) % choices.Count;
            else if (key.Key == ConsoleKey.Enter && !choices[At].Off)
                return (true, back && At == choices.Count - 1 ? null : At);
            else if (key.Key == ConsoleKey.Escape)
                return (true, null);
            else if (shortcut?.Invoke(key.Key) is { } chosen)
                return (true, chosen);
            return (false, null);
        }
    }
}
