using System.Globalization;
using TooBusy.Core.Run;

namespace TooBusy.Cli.Terminal;

// The page of a run: a tape on the terminal's own screen, so that what a run has done scrolls up into the history
// of the terminal and can be scrolled back to. A task that is over is one line that stays: the mark of how it went,
// its number and title and the time it took. Nothing is said of a task that starts. What a run warns of stays too.
//
// Under these lines stands the task that is worked on: a mark that turns, its number and title and, under them, how
// long it has been worked on and what its session is doing. A session that keeps a plan of its work has it under them
// as a bar, a cell for each step: full for one that is done, blinking for one it is at. While the session waits for
// the owner, what it said last and how it is opened stand there too, and are gone when it goes on.
//
// Under the task is the place of the commands, between two rules. Nothing is typed there: a pointer blinks where
// the menu opens, and `/` opens the menu of the commands that mean something at the moment, the arrows move, Enter
// runs one and Escape closes the menu. A session that waits for the owner brings the two commands that are for it
// without being asked. Once the run was told to stop or to abort, the line of the pointer says that it does instead
// of where the menu opens. At its right edge stands how much of the usage limits is used, `limits used: 5-hour 41% ·
// weekly 12%`, and nothing while the menu is open. Under the second rule stand the keys and, at their right, the place of the task among the tasks
// of the run: `task 4 of 9`, as the queue was read before the task.
//
// The run goes on by itself while the page waits for a key: the page looks for one again and again and draws what
// has changed. When the run is over the page writes how it went, rolls the tape up and gives the result: it does
// not wait to be left. Ctrl+C, twice, kills the run: its session is stopped, and its task stays as it is.
public sealed class RunScreen(Page page, Func<DateTimeOffset> now)
{
    // How often the page is drawn anew for the times it shows.
    static readonly TimeSpan Refresh = TimeSpan.FromMilliseconds(250);

    // As many lines of what a session that waits said are shown at most.
    const int Quoted = 8;

    // The commands that are for a session that waits for the owner.
    const RunCommands ForOwner = RunCommands.Hold | RunCommands.Nudge;

    static readonly Entry[] Commands =
    [
        new("hold", "it gets no message and waits for you", RunCommand.Hold, RunCommands.Hold),
        new("send now", "it is told to go on alone at once", RunCommand.Nudge, RunCommands.Nudge),
        new("stop", "finish the current task, then stop", RunCommand.Stop, RunCommands.Stop),
        new("continue", "take the stop back", RunCommand.Continue, RunCommands.Continue),
        new("abort", "stop as soon as possible: nothing committed, a report in the task", RunCommand.Abort, RunCommands.Abort),
    ];

    readonly List<TaskEnd> ends = [];

    // The tasks that are over, in their order.
    public IReadOnlyList<TaskEnd> Ends => ends;

    // How the run ended; null until it has.
    public RunResult? Result { get; private set; }

    // How the run went, in one line: what its tape ends with.
    public string Summary { get; private set; } = "";

    // Stays until the run is over, and gives how it ended.
    public async Task<RunResult> RunAsync(IQueueRun run, CancellationToken cancellationToken)
    {
        page.EscapeLeaves = false;
        page.Leaving = "stop the session and exit";
        var tape = page.Unroll();
        var started = now();
        var feed = new RunFeed();
        using var kill = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var working = Task.Run(() => run.RunAsync(feed, kill.Token), CancellationToken.None);

        // Whether the menu was opened with `/`, whether the commands that a waiting session brought were put away,
        // and the row the pointer is on. Whether anything is settled on the tape yet: the foot keeps a line off it.
        // The place of the task among the tasks of the run stays as it was between tasks, when no task is worked on.
        var open = false;
        var away = false;
        var at = 0;
        var any = false;
        var counted = "";
        var drawn = "";
        try
        {
            while (!working.IsCompleted)
            {
                var settled = Settled(feed.Take());
                any |= settled.Count > 0;
                var status = feed.Status;
                var waits = status.Phase == RunPhase.WaitingForOwner;
                away &= waits;
                List<Entry> offered = [.. Commands.Where(entry => status.Available.HasFlag(entry.Flag))];
                var listed = open ? offered : waits && !away ? [.. offered.Where(entry => ForOwner.HasFlag(entry.Flag))] : [];
                at = Math.Clamp(at, 0, Math.Max(0, listed.Count - 1));
                if (status.Task is not null)
                    counted = Counted(status);

                // A foot that shows nothing new is not drawn again: the times it shows change once in a second.
                var foot = Foot(status, listed, at, any, counted, tape);
                var showing = string.Join('\n', foot.Lines.Select(Told).Append($"{foot.Chosen}").Append(string.Join(' ', foot.Lit ?? [])));
                if (settled.Count > 0 || showing != drawn)
                {
                    tape.DrawFitted(settled, foot);
                    drawn = showing;
                }

                if (await page.ReadAsync(Task.WhenAny(working, feed.Changed, Task.Delay(Refresh, CancellationToken.None))) is not { } key)
                    continue;

                // A key is answered at once, whatever the page showed before it.
                drawn = "";
                if (key.KeyChar == '/' && !open)
                    (open, at) = (true, 0);
                else if (listed.Count == 0)
                    continue;
                else if (key.Key == ConsoleKey.UpArrow)
                    at = (at + listed.Count - 1) % listed.Count;
                else if (key.Key is ConsoleKey.DownArrow or ConsoleKey.Tab)
                    at = (at + 1) % listed.Count;
                else if (key.Key == ConsoleKey.Enter)
                    (open, at) = (Sent(run, listed[at]), 0);
                else if (key.Key == ConsoleKey.Escape)
                    (away, open, at) = (away || !open, false, 0);
            }

            return Result = await working;
        }
        catch
        {
            // The page is left while the run works, with Ctrl+C or because something went wrong on it: the run is
            // killed, never left to go on with nobody watching it, and what it says of that is kept.
            await kill.CancelAsync();
            Result = await working;
            throw;
        }
        finally
        {
            // However the page is left, what the run did stays on the tape, with how it went under it.
            if (Result is { } result)
            {
                var settled = Settled(feed.Take());
                var (tasks, why) = RunLook.Summary(result, ends, now() - started);
                var spaced = any || settled.Count > 0;
                Summary = Told(tasks) + (why is null ? "" : $" · {why.Text}");
                tape.DrawFitted([.. settled, width => [.. spaced ? [Line.Empty] : (Line[])[], tasks, .. why is null ? [] : RunLook.Wrapped(why, width - 2)]], Strip.Empty);
            }

            tape.Close();
            page.Leaving = "exit";
        }
    }

    // Sends the command of the entry to the run; false, for the menu, which is closed by it.
    static bool Sent(IQueueRun run, Entry entry)
    {
        run.Send(entry.Command);
        return false;
    }

    // What of what the run told stays on the tape: the tasks that are over, which are remembered, and what the run
    // warns of. Each is laid out to the width the tape has, and again when the window gets another one.
    List<Func<int, IReadOnlyList<Line>>> Settled(IReadOnlyList<object> told)
    {
        var pieces = new List<Func<int, IReadOnlyList<Line>>>();
        foreach (var said in told)
        {
            if (said is TaskEnd ended)
            {
                ends.Add(ended);
                pieces.Add(width => [RunLook.Ended(ended, width - 2)]);
            }
            else if (said is RunLine { Mark: RunMark.Attention } warned)
            {
                pieces.Add(width => [.. RunLook.Wrapped(warned, width - 2)]);
            }
        }

        return pieces;
    }

    // The foot of the tape: what the run is doing, the place of the commands between its rules, and the keys.
    Strip Foot(RunStatus status, List<Entry> listed, int at, bool spaced, string counted, Tape tape)
    {
        var width = tape.Width;
        var rule = Line.Of(new string('─', width - 2), Tone.Muted);
        var lines = new List<Line>();
        if (spaced)
            lines.Add(Line.Empty);

        var mark = new Caret(lines.Count, 0);
        lines.AddRange(Doing(status));

        // The bar of the plan is the third line of the task: the cells of the steps the session is at blink.
        List<Caret> lit = status.Task is null ? [] : [.. RunLook.Active(status.Plan).Select(column => new Caret(mark.Line + 2, column))];

        // What the session said gets the lines the window has left over, the rest of the foot being what it is.
        lines.AddRange(Asked(status, width, Math.Min(Quoted, tape.Room - lines.Count - 3 - Math.Max(1, listed.Count))));
        lines.Add(rule);

        int chosen;
        if (listed.Count == 0)
        {
            // Nothing is typed here: the pointer only shows where the menu opens, and blinks as it does in it.
            chosen = lines.Count;
            lines.Add(Edged(new Line(new Part("❯", Tone.Accent), Hint(status)), width, Limits(status)));
        }
        else
        {
            chosen = lines.Count + at;
            var widest = listed.Max(entry => entry.Name.Length);
            lines.AddRange(listed.Select((entry, index) => Picker.Row(index == at, entry.Name.PadRight(widest), entry.About)));
        }

        lines.Add(rule);
        lines.Add(Edged(Keys(listed.Count > 0), width, counted));
        return new Strip(lines, mark, Chosen: chosen, Lit: lit);
    }

    // What the run is doing, in two lines: the task under the mark, and under it the time and what goes on; and in a
    // third the plan of the session, when it keeps one. Between tasks the first line says what the run does, and the
    // second how long it waits for what it waits for.
    IEnumerable<Line> Doing(RunStatus status)
    {
        if (status.Task is not { } task)
        {
            yield return new Line(new Part("● ", Tone.Accent), new Part(status.Text.Length == 0 ? "Starting…" : status.Text, Tone.Muted));
            yield return Line.Of(status.Until is { } reset ? $"  goes on in {RunLook.Clock(reset - now())}" : "", Tone.Muted);
            yield break;
        }

        yield return new Line(new Part("● ", Tone.Accent), new Part(Spoken.Task(task)));

        var parts = new List<Part> { new("  " + (status.Since is { } since ? RunLook.Clock(now() - since) : ""), Tone.Muted) };
        switch (status.Phase)
        {
            case RunPhase.WaitingForOwner:
                parts.Add(new Part(" · ", Tone.Muted));
                parts.Add(new Part("waits for you", Tone.Warning));
                parts.Add(new Part(status.Until is { } told ? $" · goes on alone in {RunLook.Clock(told - now())}" : " · gets no message", Tone.Muted));
                break;
            case RunPhase.WaitingForLimit:
                parts.Add(new Part(" · ", Tone.Muted));
                parts.Add(new Part("waits for a usage limit", Tone.Warning));
                if (status.Until is { } until)
                    parts.Add(new Part($" · goes on in {RunLook.Clock(until - now())}", Tone.Muted));
                break;
            case RunPhase.Preparing:
                parts.Add(new Part($" · {status.Step ?? "starting"}", Tone.Muted));
                break;
            default:
                if (status.Step is { } step)
                    parts.Add(new Part($" · {step}", Tone.Muted));
                break;
        }

        yield return new Line([.. parts]);
        if (status.Plan.Count > 0)
            yield return RunLook.Plan(status.Plan);
    }

    // What a session that waits for the owner said last, on as many lines as there is room for, and how the owner
    // opens the session to answer it.
    static IEnumerable<Line> Asked(RunStatus status, int width, int room)
    {
        if (status.Phase != RunPhase.WaitingForOwner || room <= 0)
            yield break;

        var said = status.Reply.SelectMany(line => RunLook.Pieces(line, Math.Max(10, width - 6))).ToList();
        var opened = status.Open is null ? 0 : 1;
        var shown = Math.Min(said.Count, room - opened);
        for (var line = 0; line < shown; line++)
            yield return new Line(new Part("  │ ", Tone.Muted), new Part(line == shown - 1 && shown < said.Count ? "…" : said[line]));
        if (status.Open is { } open)
            yield return new Line(new Part("  answer it in its session: ", Tone.Muted), new Part(open, Tone.Quiet));
    }

    // What the line of the commands says after the pointer: where the menu opens or, once the run was told to stop
    // or to abort, that it does.
    static Part Hint(RunStatus status) =>
        !status.Stopping && !status.Aborting ? new Part(" press / to show the menu", Tone.Muted)
        : new Part(status.Task is null ? " stopping before the next task" : status.Aborting ? " aborting: the session is wrapping up" : " stopping after this task", Tone.Warning);

    // The keys under the second rule or, when a key that leaves was pressed once, what is asked.
    Line Keys(bool menu) =>
        page.Asking is { } asking ? Line.Of(asking, Tone.Warning)
        : Page.Hints(menu ? "↑↓ move · enter run · esc close" : $"ctrl+c {page.Leaving}");

    // How much of the usage limits is used, of the windows that are known: with the words that say what the figures
    // are, and the figures alone for a line that has no room for the words.
    static string[] Limits(RunStatus status)
    {
        var figures = string.Join(" · ", ((UsageWindow?[])[status.Limits.Near, status.Limits.Far]).OfType<UsageWindow>().Select(window => $"{window.Name} {Spoken.Percent(window.Used)}"));
        return figures.Length == 0 ? [] : [$"limits used: {figures}", figures];
    }

    // The place of the task that is worked on among the tasks of the run: those that are over before it, it, and
    // those that follow it as the queue was read before it.
    static string Counted(RunStatus status) =>
        string.Create(CultureInfo.InvariantCulture, $"task {status.Behind + 1} of {status.Behind + 1 + status.Queued}");

    // The line with a text at its right edge, muted: the first of the texts that has room there, or none.
    static Line Edged(Line line, int width, params string[] rights)
    {
        var taken = line.Parts.Sum(part => part.Text.Length);
        foreach (var right in rights)
        {
            var gap = width - 2 - taken - right.Length;
            if (right.Length > 0 && gap >= 2)
                return new Line([.. line.Parts, new Part(new string(' ', gap)), new Part(right, Tone.Muted)]);
        }

        return line;
    }

    static string Told(Line line) => string.Concat(line.Parts.Select(part => part.Text));

    // A command of the menu, known by the flag that tells whether it means something now.
    sealed record Entry(string Name, string About, RunCommand Command, RunCommands Flag);
}
