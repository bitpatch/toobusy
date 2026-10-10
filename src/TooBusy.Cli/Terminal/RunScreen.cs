using TooBusy.Core.Run;

namespace TooBusy.Cli.Terminal;

// The page of a run: a tape on the terminal's own screen, so that what a run has done scrolls up into the history
// of the terminal and can be scrolled back to. A task that is over is one line that stays: the mark of how it went,
// its title and the time it took. Nothing is said of a task that starts. What a run warns of stays too.
//
// Under these lines stands the task that is worked on: a mark that turns, its title and, under the title, how long
// it has been worked on and what its session is doing. While the session waits for the owner, what it said last and
// how it is opened stand there too, and are gone when it goes on.
//
// Under the task is the place of the commands, between two rules. Nothing is typed there: a pointer blinks where
// the menu opens, and `/` opens the menu of the commands that mean something at the moment, the arrows move, Enter
// runs one and Escape closes the menu. A session that waits for the owner brings the two commands that are for it
// without being asked. Under the second rule stand the keys and, at their right, how much of the usage limits is
// used.
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
        var open = false;
        var away = false;
        var at = 0;
        var any = false;
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

                // A foot that shows nothing new is not drawn again: the times it shows change once in a second.
                var foot = Foot(status, listed, at, any, tape);
                var showing = string.Join('\n', foot.Lines.Select(Told).Append($"{foot.Chosen}"));
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
    Strip Foot(RunStatus status, List<Entry> listed, int at, bool spaced, Tape tape)
    {
        var width = tape.Width;
        var rule = Line.Of(new string('─', width - 2), Tone.Muted);
        var lines = new List<Line>();
        if (spaced)
            lines.Add(Line.Empty);

        var mark = new Caret(lines.Count, 0);
        lines.AddRange(Doing(status));

        // What the session said gets the lines the window has left over, the rest of the foot being what it is.
        lines.AddRange(Asked(status, width, Math.Min(Quoted, tape.Room - lines.Count - 3 - Math.Max(1, listed.Count))));
        lines.Add(rule);

        int chosen;
        if (listed.Count == 0)
        {
            // Nothing is typed here: the pointer only shows where the menu opens, and blinks as it does in it.
            chosen = lines.Count;
            lines.Add(new Line(new Part("❯", Tone.Accent), new Part(" press / to show the menu", Tone.Muted)));
        }
        else
        {
            chosen = lines.Count + at;
            var widest = listed.Max(entry => entry.Name.Length);
            lines.AddRange(listed.Select((entry, index) => Picker.Row(index == at, entry.Name.PadRight(widest), entry.About)));
        }

        lines.Add(rule);
        lines.Add(Keys(status, listed.Count > 0, width));
        return new Strip(lines, mark, Chosen: chosen);
    }

    // What the run is doing, in two lines: the task under the mark, and under it the time and what goes on.
    // Between tasks the first line says what the run does, and the second how long it waits for what it waits for.
    IEnumerable<Line> Doing(RunStatus status)
    {
        if (status.Task is not { } task)
        {
            yield return new Line(new Part("● ", Tone.Accent), new Part(status.Text.Length == 0 ? "Starting…" : status.Text, Tone.Muted));
            yield return Line.Of(status.Until is { } reset ? $"  goes on in {RunLook.Clock(reset - now())}" : "", Tone.Muted);
            yield break;
        }

        yield return new Line(new Part("● ", Tone.Accent), new Part(task.Title));

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
                parts.Add(new Part(" · starting", Tone.Muted));
                break;
            default:
                if (status.Step is { } step)
                    parts.Add(new Part($" · {step}", Tone.Muted));
                break;
        }

        if (status.Aborting || status.Stopping)
        {
            parts.Add(new Part(" · ", Tone.Muted));
            parts.Add(new Part(status.Aborting ? "wrapping up" : "the last task of this run", Tone.Warning));
        }

        yield return new Line([.. parts]);
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

    // The line under the second rule: the keys or, when a key that leaves was pressed once, what is asked; and at
    // the right edge how much of the usage limits is used, when it is known and there is room.
    Line Keys(RunStatus status, bool menu, int width)
    {
        var keys = page.Asking is { } asking ? Line.Of(asking, Tone.Warning)
            : Page.Hints(menu ? "↑↓ move · enter run · esc close" : $"ctrl+c {page.Leaving}");
        var limits = string.Join(" · ", ((UsageWindow?[])[status.Limits.Near, status.Limits.Far]).OfType<UsageWindow>().Select(window => $"{window.Name} {Spoken.Percent(window.Used)}"));
        var gap = width - 2 - keys.Parts.Sum(part => part.Text.Length) - limits.Length;
        return limits.Length == 0 || gap < 2 ? keys : new Line([.. keys.Parts, new Part(new string(' ', gap)), new Part(limits, Tone.Muted)]);
    }

    static string Told(Line line) => string.Concat(line.Parts.Select(part => part.Text));

    // A command of the menu, known by the flag that tells whether it means something now.
    sealed record Entry(string Name, string About, RunCommand Command, RunCommands Flag);
}
