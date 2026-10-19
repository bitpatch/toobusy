using TooBusy.Core.Setup;
using TooBusy.Infrastructure.Settings;

namespace TooBusy.Cli.Terminal;

// The questions of the setup on a page: the notes and the answers so far in its body, the question of the moment
// under them, and the steps of the setup, done and to come, in its foot. A question is its name and what to do, then
// the lines where the user acts, one choice under another. The choice under the pointer is the chosen line of the
// page, and a text is typed at its cursor. Escape goes back from every question; from the first one it leaves the
// page, unless the setup was opened from somewhere to go back to. The questions of one setting that the menu of the
// settings opens have no steps, and going back from the first of them gives what is chosen or typed there.
public sealed class SetupScreen(Page page, bool leavesPage = true) : ISetupDialog
{
    // The boards are shown fewer at a time than other lists, because other things share their place.
    const int BoardRows = 5;

    // Whether the question that is asked is the first one of the first step: going back from it leaves the setup.
    bool first;

    // Whether the question is of a setting of the menu of the settings, and whether going back from it gives what is
    // chosen or typed in it.
    bool menu;
    bool keeps;

    // A note of the setup as a line of the body.
    public static Line Told(SetupNote note) => note.Tone switch
    {
        SetupTone.Muted => Line.Of(note.Text, Tone.Muted),
        SetupTone.Warning => Line.Of("! " + note.Text, Tone.Warning),
        SetupTone.Failure => Line.Of("✘ " + note.Text, Tone.Error),
        SetupTone.Change => new Line(new Part("  → ", Tone.Accent), new Part(note.Text)),
        _ => Line.Of(note.Text),
    };

    // How a setup ended, in one line, and whether that is a failure.
    public static (bool Failed, string Text) Ending(SetupResult result, bool demo) => result.Outcome switch
    {
        SetupOutcome.Written when demo => (false, "Demo: nothing was made, linked or written."),
        SetupOutcome.Written when result.SettingsWritten => (false, $"The settings are written to {SettingsFile.DisplayPath}. Commit the file."),
        SetupOutcome.Written => (false, "GitHub is set up as the settings say."),
        SetupOutcome.NothingToChange => (false, "Nothing to change: the settings already say this."),
        SetupOutcome.Declined => (true, "The setup was declined: nothing was changed."),
        SetupOutcome.Failed => (true, $"{result.Failure} The settings were not written."),
        _ => (true, "The setup was left: nothing was changed."),
    };

    public void Show(SetupProgress progress)
    {
        var body = progress.Notes.Select(Told).ToList();

        // The answers come first; what is said about them, and what is about to change, follows.
        page.Body =
        [
            .. progress.Answers.Select(answer => Picker.Answer(answer.Label, answer.Value)),
            .. body.Count > 0 ? (Line[])[Line.Empty] : [],
            .. body,
        ];

        // The steps are marks on a line: those that are done, the one that is asked, those to come.
        page.Foot = progress.Steps.Count == 0 ? Line.Empty : new Line([.. progress.Steps.Select((_, step) =>
            step < progress.Step ? new Part(step == 0 ? "●" : "──●", Tone.Success)
            : step == progress.Step ? new Part(step == 0 ? "◉" : "──◉", Tone.Accent)
            : new Part(step == 0 ? "○" : "──○", Tone.Muted))]);
        first = progress.Step == 0 && !progress.Later && leavesPage;
        (menu, keeps) = (progress.Menu, progress.Menu && !progress.Later);
        Leaves(first);
    }

    // Says whether Escape leaves the setup from the question that is on the page now.
    void Leaves(bool leaves)
    {
        page.EscapeLeaves = leaves;
        page.Keys = leaves ? "esc exit" : "esc back";
    }

    public async Task<T?> WaitAsync<T>(string label, string hint, string text, Func<CancellationToken, Task<T>> work, CancellationToken cancellationToken)
        where T : class =>
        await page.WaitAsync(text, work, cancellationToken, Head(label, hint)) is (true, var value) ? value : null;

    public int? Choose(string label, string hint, IReadOnlyList<SetupOption> options, int proposed) =>
        Picker.Pick(page, Head(label, hint), [.. options.Select(Offered)], at: proposed);

    public IReadOnlyList<int>? ChooseMany(string label, string hint, IReadOnlyList<SetupOption> options, IReadOnlyList<int> proposed) =>
        Picker.PickMany(page, Head(label, hint), [.. options.Select(Offered)], proposed, keeps);

    static Choice Offered(SetupOption option) => new(option.Name, option.Detail, Off: option.Off, Current: option.Current);

    public string? Ask(string label, string hint, string proposed, Func<string, string?> refuse) => Prompt.Ask(page, label, hint, proposed, refuse, keeps);

    // The last question of the setup: what was answered is saved, or the setup is left as if nothing was answered.
    // In the menu of the settings it is a change of one setting, and the menu is back either way.
    public bool? Confirm(string question) =>
        Picker.Pick(page, [Line.Of(question, Tone.Strong)], menu ? [new Choice("Save"), new Choice("Don't save")] : [new Choice("Save and exit"), new Choice("Exit without saving")]) is { } picked ? picked == 0 : null;

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
            new(current.Title.Length == 0 ? current.Address : current.Title, current.Title.Length == 0 ? "" : current.Address, current.Linked ? "linked to this repository" : "", Current: Saved(question, current)),
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

    // The ways to name a board, as tabs that Tab goes through: one of the known boards, an address, a new board, none.
    // The boards that are not known because GitHub did not answer have their tab all the same, which says so. Each
    // tab shows its own lines and takes its own keys; around them stand the tabs, and a place that is as tall as the
    // tallest of them, whatever tab is open: a list starts at its top, and anything else stands at its bottom.
    BoardAnswer? PickBoard(BoardQuestion question)
    {
        // The lists of the tabs end with `Back`, where there is somewhere to go back to. A tab where a text is typed
        // has no list to put it in: Escape goes back from there.
        var back = Picker.GoesBack(page);
        BoardTab[] tabs =
        [
            .. question.Unread is { } unread ? [new UnreadTab(unread, back)] : question.Known.Count > 0 ? [new KnownTab(page, question, back)] : (BoardTab[])[],
            new AddressTab(page, question),
            .. question.Owners.Count > 0 ? [new CreatedTab(page, question, question.Proposed as BoardAnswer.Created)] : (BoardTab[])[],
            new NoneTab(back),
        ];

        // The tabs open on the way that is proposed, when a new board or none is.
        var open = Math.Max(0, Array.FindIndex(tabs, tab => tab.Opens(question.Proposed)));
        var height = tabs.Max(tab => tab.Height);
        while (true)
        {
            var tab = tabs[open];
            var shown = tab.Show(height);

            // Without colours the chosen tab is told by its brackets.
            var bar = tabs.SelectMany((each, index) => (Part[])
            [
                page.Coloured ? new Part($" {each.Name} ", Tone.Quiet, index == open ? Ground.Chosen : Ground.Bar)
                : new Part(index == open ? $"[{each.Name}]" : $" {each.Name} ", index == open ? Tone.Strong : Tone.Muted),
                new Part(" "),
            ]);

            // The tabs, an empty line, and the place of the tab.
            var space = Enumerable.Repeat(Line.Empty, Math.Max(0, height - shown.Lines.Count)).ToList();
            var above = tab.Top ? [] : space;
            List<Line> lines = [new Line([.. bar, new Part(" press ", Tone.Muted), new Part("tab", Tone.Quiet), new Part(" to switch", Tone.Muted)]), Line.Empty, .. above, .. shown.Lines, .. above.Count == 0 ? space : []];
            var offset = 2 + above.Count;
            page.Draw(Head(question.Label, question.Hint), lines, shown.Keys, shown.Caret is { } caret ? caret with { Line = caret.Line + offset } : null, shown.Pointed + offset, back: shown.Back);

            var key = page.Read();
            if (key.Key == ConsoleKey.Escape)
                return null;

            if (key.Key == ConsoleKey.Tab)
            {
                tab.Leave();
                open = (open + (key.Modifiers.HasFlag(ConsoleModifiers.Shift) ? tabs.Length - 1 : 1)) % tabs.Length;
                continue;
            }

            if (tab.Press(key) is (true, var answer))
                return answer;
        }
    }

    // How many lines a list of so many rows takes among the boards, with the marks of a list that scrolls.
    static int Rows(int count) => Math.Min(count, BoardRows) + (count > BoardRows ? 2 : 0);

    // A way to name a board, as a tab of the board question: its name, what it shows in the place under the tabs and
    // what a key does there. Tab and Escape are the question's own.
    abstract class BoardTab(string name)
    {
        // What a key does: it ends the question with an answer, or with none, which goes back; or the question goes on.
        protected static readonly (bool Ends, BoardAnswer? Answer) Stay = (false, null), GoBack = (true, null);

        public string Name => name;

        // How many lines the tab shows at most. A tab without a list shows three at most.
        public virtual int Height => 3;

        // Whether what the tab shows starts at the top of the place, as a list does, or stands at its bottom.
        public virtual bool Top => false;

        // Whether the tabs open on this one: it is the way of the answer that is proposed.
        public virtual bool Opens(BoardAnswer? proposed) => false;

        // What the tab shows in a place of this height.
        public abstract Shown Show(int height);

        public abstract (bool Ends, BoardAnswer? Answer) Press(ConsoleKeyInfo key);

        // Another tab is opened: what was said of a text that was refused is forgotten.
        public virtual void Leave()
        {
        }

        protected static (bool Ends, BoardAnswer? Answer) Answer(BoardAnswer answer) => (true, answer);
    }

    // What a tab shows: its lines, the keys it takes, where the caret and the pointer are among its lines, and whether
    // the `Back` it ends with names its key.
    readonly record struct Shown(List<Line> Lines, string Keys, Caret? Caret = null, int? Pointed = null, bool Back = false);

    // One of the boards the user can reach, chosen from a list that what is typed filters. The list opens on the
    // board the project has now, and the board of the settings is marked where it is among those that fit.
    sealed class KnownTab(Page page, BoardQuestion question, bool back) : BoardTab("Your projects")
    {
        readonly LineEditor filter = new("");
        IReadOnlyList<SetupBoard> fitting = [];
        int at = Math.Max(0, question.Known.ToList().FindIndex(board => board.Address == question.Current?.Address));
        int top;

        // The filter over the list of the boards, which ends with `Back`.
        public override int Height => Rows(question.Known.Count + Backs) + 1;

        public override bool Top => true;

        int Backs => back ? 1 : 0;

        public override Shown Show(int height)
        {
            fitting = BoardSuggestions.Matching(question.Known, filter.Text);

            // A filter that fits nothing leaves no list, and so no `Back` for Enter to fall on.
            var listed = fitting.Count == 0 ? 0 : fitting.Count + Backs;
            at = Math.Clamp(at, 0, Math.Max(0, listed - 1));
            var (typed, column) = Prompt.Field(page, filter, "Filter  ");
            List<Line> lines = [typed];
            if (fitting.Count == 0)
                lines.Add(Line.Of("  No project fits.", Tone.Muted));
            var widest = Math.Min(30, fitting.Select(board => board.Title.Length).DefaultIfEmpty().Max());
            bool? saved = fitting.Any(board => Saved(question, board)) ? false : null;
            top = Picker.Window(lines, listed, at, top, BoardRows, out var pointer, index => index == fitting.Count
                ? Picker.BackRow(index == at)
                : fitting[index].Title.Length == 0
                    ? Row(index == at, fitting[index].Address, "", fitting[index].Linked ? "linked" : "", saved is null ? null : Saved(question, fitting[index]))
                    : Row(index == at, fitting[index].Title.PadRight(widest), fitting[index].Address, fitting[index].Linked ? "linked" : "", saved is null ? null : Saved(question, fitting[index])));
            return new Shown(lines, "type to filter · ↑↓ move · enter choose", new Caret(0, column), listed > 0 ? pointer : null, listed > 0 && back);
        }

        public override (bool Ends, BoardAnswer? Answer) Press(ConsoleKeyInfo key)
        {
            switch (key.Key)
            {
                case ConsoleKey.UpArrow when fitting.Count > 0:
                    at = (at + fitting.Count + Backs - 1) % (fitting.Count + Backs);
                    return Stay;
                case ConsoleKey.DownArrow when fitting.Count > 0:
                    at = (at + 1) % (fitting.Count + Backs);
                    return Stay;
                case ConsoleKey.Enter:
                    return at < fitting.Count ? Answer(new BoardAnswer.Existing(fitting[at].Address)) : fitting.Count > 0 ? GoBack : Stay;
                default:
                    var before = filter.Text;
                    filter.Press(key);
                    if (filter.Text != before)
                        (at, top) = (0, 0);
                    return Stay;
            }
        }
    }

    // The boards the user can reach where GitHub did not answer for them: there is nothing to filter, and nothing to
    // choose but `Back`.
    sealed class UnreadTab(string unread, bool back) : BoardTab("Your projects")
    {
        public override bool Top => true;

        public override Shown Show(int height) => back
            ? new Shown([Line.Of($"  {unread}", Tone.Error), Picker.BackRow(true)], "enter choose", Pointed: 1, Back: true)
            : new Shown([Line.Of($"  {unread}", Tone.Error)], "");

        public override (bool Ends, BoardAnswer? Answer) Press(ConsoleKeyInfo key) =>
            key.Key == ConsoleKey.Enter && back ? GoBack : Stay;
    }

    // A board by its address, or by the address of any page of it; one that is not is refused with the reason.
    sealed class AddressTab(Page page, BoardQuestion question) : BoardTab("By URL")
    {
        readonly LineEditor address = new("");
        string? reason;

        public override Shown Show(int height)
        {
            var (pasted, where) = Prompt.Field(page, address, "");
            return new Shown(
                [reason is null ? Line.Of("Paste the address of the project, or of any page of it.", Tone.Muted) : Line.Of(reason, Tone.Error), pasted],
                "enter confirm",
                new Caret(1, where));
        }

        public override (bool Ends, BoardAnswer? Answer) Press(ConsoleKeyInfo key)
        {
            if (key.Key == ConsoleKey.Enter)
                return (reason = question.RefuseAddress(address.Text)) is null ? Answer(new BoardAnswer.Existing(address.Text.Trim())) : Stay;

            address.Press(key);
            return Stay;
        }

        public override void Leave() => reason = null;
    }

    // A new board, made when the setup is confirmed: whose it will be is chosen as a board is, from a list, and its
    // title is typed under it, at the bottom of the place. The proposed one is where the tab opens.
    sealed class CreatedTab(Page page, BoardQuestion question, BoardAnswer.Created? created) : BoardTab("New project")
    {
        readonly LineEditor title = new(created?.Title ?? "");
        int owner = created is null ? 0 : Math.Max(0, question.Owners.ToList().IndexOf(created.Owner));
        int top;
        string? reason;

        // What the list is, the list of the owners, an empty line at least, the title and what is said of it.
        public override int Height => Rows(question.Owners.Count) + 4;

        public override bool Top => true;

        public override bool Opens(BoardAnswer? proposed) => proposed is BoardAnswer.Created;

        public override Shown Show(int height)
        {
            List<Line> lines = [Line.Of("Whose project it will be:", Tone.Muted)];
            var logins = question.Owners.Max(candidate => candidate.Login.Length);
            top = Picker.Window(lines, question.Owners.Count, owner, top, BoardRows, out var pointer, index =>
                Row(index == owner, question.Owners[index].Login.PadRight(logins), question.Owners[index].Organisation ? "organisation" : "your account"));
            lines.AddRange(Enumerable.Repeat(Line.Empty, Math.Max(1, height - lines.Count - 2)));
            var (titled, here) = Prompt.Field(page, title, "Title   ");
            var caret = new Caret(lines.Count, here);
            lines.Add(titled);
            lines.Add(reason is null ? Line.Of("It is made when the setup is confirmed, and linked to the repository.", Tone.Muted) : Line.Of(reason, Tone.Error));
            return new Shown(lines, (question.Owners.Count > 1 ? "↑↓ owner · " : "") + "enter confirm", caret, pointer);
        }

        public override (bool Ends, BoardAnswer? Answer) Press(ConsoleKeyInfo key)
        {
            switch (key.Key)
            {
                case ConsoleKey.UpArrow:
                    owner = (owner + question.Owners.Count - 1) % question.Owners.Count;
                    return Stay;
                case ConsoleKey.DownArrow:
                    owner = (owner + 1) % question.Owners.Count;
                    return Stay;
                case ConsoleKey.Enter:
                    return (reason = question.RefuseTitle(title.Text)) is null ? Answer(new BoardAnswer.Created(question.Owners[owner], title.Text.Trim())) : Stay;
                default:
                    title.Press(key);
                    return Stay;
            }
        }

        public override void Leave() => reason = null;
    }

    // No board: the tasks are taken from the repository, and `Back` follows where there is somewhere to go back to.
    sealed class NoneTab(bool back) : BoardTab("No project")
    {
        // Whether the pointer is on `Back` rather than on going on without a board.
        bool backing;

        public override bool Opens(BoardAnswer? proposed) => proposed is BoardAnswer.None;

        public override Shown Show(int height) => new(
            [
                Line.Of("  The tasks are taken from the repository, and no board keeps their statuses.", Tone.Muted),
                Row(!backing, "Go on without a project"),
                .. back ? [Picker.BackRow(backing)] : (Line[])[],
            ],
            back ? "↑↓ move · enter confirm" : "enter confirm",
            Pointed: backing ? 2 : 1,
            Back: back);

        public override (bool Ends, BoardAnswer? Answer) Press(ConsoleKeyInfo key)
        {
            switch (key.Key)
            {
                case ConsoleKey.UpArrow or ConsoleKey.DownArrow when back:
                    backing = !backing;
                    return Stay;
                case ConsoleKey.Enter:
                    return backing ? GoBack : Answer(new BoardAnswer.None());
                default:
                    return Stay;
            }
        }
    }

    static Line Row(bool pointed, string name, string detail = "", string note = "", bool? current = null) => Picker.Row(pointed, name, detail, note, current);

    // Whether the board is the one the settings have now.
    static bool Saved(BoardQuestion question, SetupBoard board) => board.Address.Equals(question.Saved, StringComparison.OrdinalIgnoreCase);

    static List<Line> Head(string label, string hint, string? reason = null) => Picker.Head(label, hint, reason);
}
