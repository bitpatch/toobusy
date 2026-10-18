namespace TooBusy.Cli.Terminal;

// A choice of a list: what it is called, what explains it, and what is to be noticed about it. A choice that is off
// cannot be chosen: it is muted, and Enter does nothing on it. One that waits has the dots of a wait where what is
// to be noticed about it will stand. The note is in the colour of success unless it has one of its own. Current is
// the choice of now, which `current` marks right after its name.
public sealed record Choice(string Name, string Detail = "", string Note = "", bool Off = false, bool Waits = false, Tone? NoteTone = null, bool Current = false);

// The lists of every screen: a pointer on the chosen row, the name of a row with what explains it after it, and
// marks above and below a list that does not fit. One look for the setup, the milestones and the menu.
public static class Picker
{
    // A list longer than this scrolls.
    public const int Rows = 8;

    // The last row of every list that there is somewhere to go back from, and the key that does what the row does:
    // the row names it at its right, so the keys of the page do not name it again. The `Exit` of the menu, which
    // Escape does the same as, names it in the same way.
    public const string Back = "Back";
    public const string BackKey = "esc";

    // What marks the choice of now, as the settings or the choices of the user have it, among the others: it stands
    // right after the name, in the colour of success, in a column of its own, which the other rows of its list keep
    // empty before what explains them, so that that stands in one column. `Back` names its key in that column.
    public const string Current = "current";

    const int LabelWidth = 16;

    // Whether a list on the page is to end with `Back`: it is, unless Escape leaves the page from here.
    public static bool GoesBack(Page page) => !page.EscapeLeaves;

    // Asks to choose one of the choices, one under another, starting at the given one. Gives the index of the
    // chosen one, or null when Escape is pressed. `shortcut` lets a key choose at once. Wherever Escape goes back
    // and does not leave the page, the list ends with `Back`, which does what Escape does. A list longer than `rows`
    // scrolls.
    public static int? Pick(Page page, IReadOnlyList<Line> question, IReadOnlyList<Choice> choices, string keys = "", int at = 0, Func<ConsoleKey, int?>? shortcut = null, int rows = Rows)
    {
        var list = new Listed(page, question, keys, at, rows);
        while (true)
        {
            list.Draw(choices);
            if (list.Press(page.Read(), shortcut) is (true, var chosen))
                return chosen;
        }
    }

    // The same list on a page where something goes on meanwhile: the choices are asked for again, and the list is
    // drawn anew, whenever what `changed` gives is done. `off` is told when Enter is pressed on a choice that is off.
    // `settled` is asked whenever something has changed, and what it gives is chosen without a key: what went on
    // has chosen it. `back` says whether the list ends with `Back` where the page would say otherwise: the menu
    // ends with `Exit`, also while Escape does not leave it.
    public static async Task<int?> PickAsync(Page page, IReadOnlyList<Line> question, Func<IReadOnlyList<Choice>> choices, Func<Task> changed, Action<int>? off = null, Func<int?>? settled = null, int at = 0, bool? back = null)
    {
        var list = new Listed(page, question, "", at, Rows, back);
        while (true)
        {
            // Whether anything still goes on is looked at before the choices are asked for: what is done between the
            // two is seen by the wait for a key, and the list is drawn again.
            var wake = changed();
            var still = wake.IsCompleted;
            if (settled?.Invoke() is { } by)
                return by;

            list.Draw(choices());

            // Once nothing goes on any more the page waits for a key as any list does.
            if ((still ? (ConsoleKeyInfo?)page.Read() : await page.ReadAsync(wake)) is not { } key)
                continue;
            if (list.Press(key, null) is (true, var chosen))
                return chosen;
            if (key.Key == ConsoleKey.Enter)
                off?.Invoke(list.At);
        }
    }

    // Asks to choose any of the options, one under another, with those that are proposed chosen at first: Space
    // chooses the option under the pointer or leaves it, and Enter gives the indexes of those that are chosen.
    // Escape gives null. Where there is somewhere to go back to the options end with `Back`, and Enter on it goes
    // back; a list without options says so, and Enter there gives nothing chosen. `keeps` makes going back give
    // what is chosen too, as Enter does: whoever asked looks whether that is a change. An option that is off is
    // muted, and Space does nothing on it; the mark of one that is current, chosen or not, is in the colour of
    // success, as the one of now.
    public static IReadOnlyList<int>? PickMany(Page page, IReadOnlyList<Line> question, IReadOnlyList<Choice> options, IReadOnlyList<int> proposed, bool keeps = false)
    {
        var chosen = new SortedSet<int>(proposed);
        var at = chosen.FirstOrDefault();
        var top = 0;
        var widest = options.Where(option => option.Detail.Length > 0).Select(option => option.Name.Length).DefaultIfEmpty().Max();
        var back = options.Count > 0 && GoesBack(page);
        var rows = options.Count + (back ? 1 : 0);
        while (true)
        {
            var lines = new List<Line>();
            if (options.Count == 0)
                lines.Add(Line.Of("There is nothing to choose from.", Tone.Muted));
            top = Window(lines, rows, at, top, Rows, out var pointer, index => index == options.Count
                ? BackRow(index == at)
                : Marked(index == at, options[index], chosen.Contains(index), widest));

            // An option that is off does not blink under the pointer: it does not wait to be chosen.
            var waits = options.Count > 0 && !(at < options.Count && options[at].Off);
            page.Draw(question, lines, options.Count == 0 ? "enter confirm" : "↑↓ move · space select · enter confirm", chosen: waits ? pointer : null, back: back);

            var key = page.Read();
            if (key.Key == ConsoleKey.Enter)
                return at < options.Count || options.Count == 0 || keeps ? [.. chosen] : null;
            if (key.Key == ConsoleKey.Escape)
                return keeps ? [.. chosen] : null;
            if (options.Count == 0)
                continue;

            if (key.Key is ConsoleKey.UpArrow or ConsoleKey.K)
                at = (at + rows - 1) % rows;
            else if (key.Key is ConsoleKey.DownArrow or ConsoleKey.J)
                at = (at + 1) % rows;
            else if (key.Key == ConsoleKey.Spacebar && at < options.Count && !options[at].Off && !chosen.Remove(at))
                chosen.Add(at);
        }
    }

    // A row of a multiple choice: the pointer, the mark of whether the option is chosen, its name and what explains
    // it. The mark of the one of now stays in the colour of success where the pointer and the name blink.
    static Line Marked(bool pointed, Choice option, bool on, int widest)
    {
        var tone = option.Off ? Tone.Muted : pointed ? Tone.Accent : Tone.Plain;
        return new Line(
            new Part(pointed ? "❯ " : "  ", tone),
            new Part(on ? "◉ " : "◯ ", option.Current ? Tone.Success : tone, Steady: option.Current),
            new Part(option.Detail.Length == 0 ? option.Name : option.Name.PadRight(widest), tone),
            new Part(option.Detail.Length == 0 ? "" : "  " + option.Detail, Tone.Muted))
        {
            Blinking = 3,
        };
    }

    // A row of a list: the pointer when it is on the row, the name, and after it what explains the name and what is
    // to be noticed about it.
    // `current` is null in a list without a choice of now, and otherwise whether this row is that choice.
    public static Line Row(bool pointed, string name, string detail = "", string note = "", bool? current = null) =>
        Row(pointed, new Choice(name, detail, note, Current: current == true), name, current is not null);

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

    // The row of a choice, its name as wide as the list wants it. What is off is muted all through. `marks` keeps the
    // column of the mark of the choice of now, in a list that has one.
    static Line Row(bool pointed, Choice choice, string name, bool marks = false) => new(
        new Part((pointed ? "❯ " : "  ") + name, choice.Off ? Tone.Muted : pointed ? Tone.Accent : Tone.Plain),
        new Part(choice.Current ? "  " + Current : marks && Followed(choice) ? new string(' ', Current.Length + 2) : "", Tone.Success),
        new Part(choice.Detail.Length == 0 ? "" : "  " + choice.Detail, Tone.Muted),
        new Part(choice.Note.Length == 0 ? "" : "  " + choice.Note, choice.Off ? Tone.Muted : choice.NoteTone ?? Tone.Success),
        new Part(choice.Waits ? "  " + new string(Screen.Dot, Screen.Dots) : "", Tone.Muted));

    // Whether something stands after the name of the choice, but for the mark of the choice of now.
    static bool Followed(Choice choice) => choice.Detail.Length + choice.Note.Length > 0 || choice.Waits;

    // A list on a page: where its pointer is and what of it is in the window, from one drawing to the next.
    sealed class Listed(Page page, IReadOnlyList<Line> question, string keys, int at, int rows, bool? ends = null)
    {
        IReadOnlyList<Choice> choices = [];
        bool back;
        int top;

        public int At { get; private set; } = at;

        public void Draw(IReadOnlyList<Choice> offered)
        {
            back = ends ?? GoesBack(page);
            choices = back ? [.. offered, new Choice(Back, BackKey)] : offered;
            At = Math.Clamp(At, 0, choices.Count - 1);

            // Names that something follows are as wide as the widest of them, so that what follows stands in a column.
            // In a list with a choice of now its mark has a column of its own, where `Back` names its key.
            var marks = choices.Any(choice => choice.Current);
            bool Padded(Choice choice) => Followed(choice) || choice.Current;
            var widest = choices.Where(Padded).Select(choice => choice.Name.Length).DefaultIfEmpty().Max();
            var lines = new List<Line>();
            int? waits = null;
            top = Window(lines, choices.Count, At, top, rows, out var pointer, index =>
            {
                if (choices[index].Waits)
                    waits = lines.Count;
                var name = Padded(choices[index]) ? choices[index].Name.PadRight(widest) : choices[index].Name;
                return Row(index == At, choices[index], name, marks && !(back && index == choices.Count - 1));
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
