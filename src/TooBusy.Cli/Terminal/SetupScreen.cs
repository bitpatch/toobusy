using TooBusy.Core.Setup;

namespace TooBusy.Cli.Terminal;

// The questions of the setup on a page: the notes and the answers so far in its body, the question of the moment
// under them, and the steps of the setup, done and to come, in its foot. A question is its name and what to do, then
// the lines where the user acts, one choice under another. The choice under the pointer is the chosen line of the
// page, and a text is typed at its cursor. Escape goes back from every question; from the first one it leaves the
// page, unless the setup was opened from somewhere to go back to.
public sealed class SetupScreen(Page page, bool leavesPage = true) : ISetupDialog
{
    // The boards are shown fewer at a time than other lists, because other things share their place.
    const int BoardRows = 5;

    // Whether the step that is asked is the first one: going back from it leaves the setup.
    bool first;

    public void Show(SetupProgress progress)
    {
        var body = progress.Notes.Select(note => note.Tone switch
        {
            SetupTone.Muted => Line.Of(note.Text, Tone.Muted),
            SetupTone.Warning => Line.Of("! " + note.Text, Tone.Warning),
            SetupTone.Failure => Line.Of("✘ " + note.Text, Tone.Error),
            SetupTone.Change => new Line(new Part("  → ", Tone.Accent), new Part(note.Text)),
            _ => Line.Of(note.Text),
        }).ToList();

        // The answers come first; what is said about them, and what is about to change, follows.
        page.Body =
        [
            .. progress.Answers.Select(answer => Picker.Answer(answer.Label, answer.Value)),
            .. body.Count > 0 ? (Line[])[Line.Empty] : [],
            .. body,
        ];

        // The steps are marks on a line: those that are done, the one that is asked, those to come.
        page.Foot = new Line([.. progress.Steps.Select((_, step) =>
            step < progress.Step ? new Part(step == 0 ? "●" : "──●", Tone.Success)
            : step == progress.Step ? new Part(step == 0 ? "◉" : "──◉", Tone.Accent)
            : new Part(step == 0 ? "○" : "──○", Tone.Muted))]);
        first = progress.Step == 0 && leavesPage;
        Leaves(first);
    }

    // Says whether Escape leaves the setup from the question that is on the page now.
    void Leaves(bool leaves)
    {
        page.EscapeLeaves = leaves;
        page.Keys = leaves ? "esc exit" : "esc back";
    }

    public void Wait(string text) => page.Draw([], [Line.Of(text, Tone.Muted)], "");

    public int? Choose(string label, string hint, IReadOnlyList<SetupOption> options, int proposed) =>
        Picker.Pick(page, Head(label, hint), [.. options.Select(option => new Choice(option.Name, option.Detail))], at: proposed);

    public IReadOnlyList<int>? ChooseMany(string label, string hint, IReadOnlyList<string> options, IReadOnlyList<int> proposed)
    {
        var chosen = new SortedSet<int>(proposed);
        var at = chosen.FirstOrDefault();
        var top = 0;
        var widest = options.Select(option => option.Length).DefaultIfEmpty().Max();
        while (true)
        {
            var lines = new List<Line>();
            if (options.Count == 0)
                lines.Add(Line.Of("There is nothing to choose from.", Tone.Muted));
            top = Window(lines, options.Count, at, top, Picker.Rows, out var pointer, index => Row(index == at, (chosen.Contains(index) ? "◉ " : "◯ ") + options[index].PadRight(widest)));
            page.Draw(Head(label, hint), lines, options.Count == 0 ? "enter confirm" : "↑↓ move · space select · enter confirm", chosen: options.Count == 0 ? null : pointer);

            var key = page.Read();
            if (key.Key == ConsoleKey.Enter)
                return [.. chosen];
            if (key.Key == ConsoleKey.Escape)
                return null;
            if (options.Count == 0)
                continue;

            if (key.Key is ConsoleKey.UpArrow or ConsoleKey.K)
                at = (at + options.Count - 1) % options.Count;
            else if (key.Key is ConsoleKey.DownArrow or ConsoleKey.J)
                at = (at + 1) % options.Count;
            else if (key.Key == ConsoleKey.Spacebar && !chosen.Remove(at))
                chosen.Add(at);
        }
    }

    public string? Ask(string label, string hint, string proposed, Func<string, string?> refuse)
    {
        var editor = new LineEditor(proposed);
        string? reason = null;
        while (true)
        {
            var (field, column) = Field(editor, "");
            page.Draw(Head(label, hint, reason), [field], "enter confirm", new Caret(0, column));

            var key = page.Read();
            if (key.Key == ConsoleKey.Escape)
                return null;
            if (key.Key != ConsoleKey.Enter)
                editor.Press(key);
            else if ((reason = refuse(editor.Text)) is null)
                return editor.Text.Trim();
        }
    }

    public bool? Confirm(string question) => Picker.Pick(page, [Line.Of(question, Tone.Strong)], [new Choice("Yes"), new Choice("No")], "y yes · n no", shortcut: key => key switch
    {
        ConsoleKey.Y => 0,
        ConsoleKey.N => 1,
        _ => null,
    }) is { } picked ? picked == 0 : null;

    // A project that has a board keeps it with one Enter; `Change` opens the ways to name another one, and going back
    // from them leaves the board as it is.
    public BoardAnswer? AskBoard(BoardQuestion question)
    {
        Leaves(first);
        if (question.Current is not { } current)
            return PickBoard(question);

        // The board that is stands first among the choices, as it stands in the list of the boards: Enter keeps it.
        Choice[] choices =
        [
            new(current.Title.Length == 0 ? current.Address : current.Title, current.Title.Length == 0 ? "" : current.Address, current.Linked ? "linked to this repository" : ""),
            new("Choose another project"),
        ];
        while (true)
        {
            switch (Picker.Pick(page, Head(question.Label, question.Hint), choices))
            {
                case null:
                    return null;
                case 0:
                    return new BoardAnswer.Existing(current.Address);
                default:
                    // From the ways to name a board Escape comes back here, to the board that is.
                    Leaves(false);
                    var picked = PickBoard(question);
                    Leaves(first);
                    if (picked is not null)
                        return picked;
                    break;
            }
        }
    }

    enum Way
    {
        Known,
        Address,
        Created,
        None,
    }

    // The ways to name a board, as tabs that Tab goes through: one of the known boards, an address, a new board, none.
    BoardAnswer? PickBoard(BoardQuestion question)
    {
        (Way Way, string Name)[] ways =
        [
            .. question.Known.Count > 0 ? [(Way.Known, "Your projects")] : ((Way, string)[])[],
            (Way.Address, "By URL"),
            .. question.Owners.Count > 0 ? [(Way.Created, "New project")] : ((Way, string)[])[],
            (Way.None, "No project"),
        ];
        var way = 0;

        var filter = new LineEditor("");
        var at = Math.Max(0, question.Known.ToList().FindIndex(board => board.Address == question.Current?.Address));
        var top = 0;
        var address = new LineEditor("");
        var title = new LineEditor("");
        var owner = 0;
        string? reason = null;

        // The place under the tabs is as tall as the tallest of them, whatever tab is open: a list starts at its top,
        // and the line where a text is typed, with what is said about it, stands at its bottom.
        static int Rows(int count) => Math.Min(count, BoardRows) + (count > BoardRows ? 2 : 0);
        var height = Math.Max(
            Math.Max(3, question.Known.Count > 0 ? Rows(question.Known.Count) + 1 : 0),
            question.Owners.Count > 0 ? Rows(question.Owners.Count) + 4 : 0);
        var ownersTop = 0;

        while (true)
        {
            // Without colours the chosen tab is told by its brackets.
            var tabs = ways.SelectMany((tab, index) => (Part[])
            [
                page.Coloured ? new Part($" {tab.Name} ", Tone.Quiet, index == way ? Ground.Chosen : Ground.Bar)
                : new Part(index == way ? $"[{tab.Name}]" : $" {tab.Name} ", index == way ? Tone.Strong : Tone.Muted),
                new Part(" "),
            ]);
            var lines = new List<Line>();

            IReadOnlyList<SetupBoard> fitting = [];
            Caret? caret = null;
            int? pointed = null;
            string keys;
            switch (ways[way].Way)
            {
                case Way.Known:
                    fitting = BoardSuggestions.Matching(question.Known, filter.Text);
                    at = Math.Clamp(at, 0, Math.Max(0, fitting.Count - 1));
                    var (typed, column) = Field(filter, "Filter  ");
                    caret = new Caret(0, column);
                    lines.Add(typed);
                    if (fitting.Count == 0)
                        lines.Add(Line.Of("  No project fits.", Tone.Muted));
                    var widest = Math.Min(30, fitting.Select(board => board.Title.Length).DefaultIfEmpty().Max());
                    var shown = fitting;
                    top = Window(lines, shown.Count, at, top, BoardRows, out var pointer, index => shown[index].Title.Length == 0
                        ? Row(index == at, shown[index].Address, "", shown[index].Linked ? "linked" : "")
                        : Row(index == at, shown[index].Title.PadRight(widest), shown[index].Address, shown[index].Linked ? "linked" : ""));
                    pointed = fitting.Count > 0 ? pointer : null;
                    keys = "type to filter · ↑↓ move · enter choose";
                    break;

                case Way.Address:
                    lines.Add(reason is null ? Line.Of("Paste the address of the project, or of any page of it.", Tone.Muted) : Line.Of(reason, Tone.Error));
                    var (pasted, where) = Field(address, "");
                    caret = new Caret(1, where);
                    lines.Add(pasted);
                    keys = "enter confirm";
                    break;

                case Way.Created:
                    // Where the board will live is chosen as a board is, from a list; its title is typed under it.
                    lines.Add(Line.Of("Whose project it will be:", Tone.Muted));
                    var logins = question.Owners.Max(candidate => candidate.Login.Length);
                    ownersTop = Window(lines, question.Owners.Count, owner, ownersTop, BoardRows, out var chosenOwner, index =>
                        Row(index == owner, question.Owners[index].Login.PadRight(logins), question.Owners[index].Organisation ? "organisation" : "your account"));
                    pointed = chosenOwner;
                    lines.AddRange(Enumerable.Repeat(Line.Empty, Math.Max(1, height - lines.Count - 2)));
                    var (named, here) = Field(title, "Title   ");
                    caret = new Caret(lines.Count, here);
                    lines.Add(named);
                    lines.Add(reason is null ? Line.Of("It is made when the setup is confirmed, and linked to the repository.", Tone.Muted) : Line.Of(reason, Tone.Error));
                    keys = (question.Owners.Count > 1 ? "↑↓ owner · " : "") + "enter confirm";
                    break;

                default:
                    lines.Add(Line.Of("  The tasks are taken from the repository, and no board keeps their statuses.", Tone.Muted));
                    pointed = 1;
                    lines.Add(Row(true, "Go on without a project"));
                    keys = "enter confirm";
                    break;
            }

            // The tabs, an empty line, and the place of the tab: the list from its top, anything else at its bottom.
            var space = Enumerable.Repeat(Line.Empty, Math.Max(0, height - lines.Count)).ToList();
            var above = ways[way].Way is Way.Known or Way.Created ? [] : space;
            lines = [new Line([.. tabs, new Part(" press ", Tone.Muted), new Part("tab", Tone.Quiet), new Part(" to switch", Tone.Muted)]), Line.Empty, .. above, .. lines, .. above.Count == 0 ? space : []];
            if (caret is { } typing)
                caret = typing with { Line = typing.Line + 2 + above.Count };
            if (pointed is { } row)
                pointed = row + 2 + above.Count;

            page.Draw(Head(question.Label, question.Hint), lines, keys, caret, pointed);

            var key = page.Read();
            if (key.Key == ConsoleKey.Escape)
                return null;

            if (key.Key == ConsoleKey.Tab)
            {
                way = (way + (key.Modifiers.HasFlag(ConsoleModifiers.Shift) ? ways.Length - 1 : 1)) % ways.Length;
                reason = null;
                continue;
            }

            switch (ways[way].Way)
            {
                case Way.Known when key.Key == ConsoleKey.UpArrow && fitting.Count > 0:
                    at = (at + fitting.Count - 1) % fitting.Count;
                    break;
                case Way.Known when key.Key == ConsoleKey.DownArrow && fitting.Count > 0:
                    at = (at + 1) % fitting.Count;
                    break;
                case Way.Known when key.Key == ConsoleKey.Enter:
                    if (fitting.Count > 0)
                        return new BoardAnswer.Existing(fitting[at].Address);
                    break;
                case Way.Known:
                    var before = filter.Text;
                    filter.Press(key);
                    if (filter.Text != before)
                        (at, top) = (0, 0);
                    break;

                case Way.Address when key.Key == ConsoleKey.Enter:
                    if ((reason = question.RefuseAddress(address.Text)) is null)
                        return new BoardAnswer.Existing(address.Text.Trim());
                    break;
                case Way.Address:
                    address.Press(key);
                    break;

                case Way.Created when key.Key == ConsoleKey.UpArrow:
                    owner = (owner + question.Owners.Count - 1) % question.Owners.Count;
                    break;
                case Way.Created when key.Key == ConsoleKey.DownArrow:
                    owner = (owner + 1) % question.Owners.Count;
                    break;
                case Way.Created when key.Key == ConsoleKey.Enter:
                    if ((reason = question.RefuseTitle(title.Text)) is null)
                        return new BoardAnswer.Created(question.Owners[owner], title.Text.Trim());
                    break;
                case Way.Created:
                    title.Press(key);
                    break;

                case Way.None when key.Key == ConsoleKey.Enter:
                    return new BoardAnswer.None();
            }
        }
    }

    static Line Row(bool pointed, string name, string detail = "", string note = "") => Picker.Row(pointed, name, detail, note);

    static List<Line> Head(string label, string hint, string? reason = null) => Picker.Head(label, hint, reason);

    static int Window(List<Line> lines, int count, int at, int top, int rows, out int pointer, Func<int, Line> row) =>
        Picker.Window(lines, count, at, top, rows, out pointer, row);

    // The line where a text is typed, and the column of the caret in it.
    (Line Line, int Column) Field(LineEditor editor, string prompt)
    {
        var (shown, caret) = editor.View(Math.Max(4, page.Width - 3 - prompt.Length));
        return (new Line(new Part(prompt, Tone.Muted), new Part(shown)), prompt.Length + caret);
    }
}
