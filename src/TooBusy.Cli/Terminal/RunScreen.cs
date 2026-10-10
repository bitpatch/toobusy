using System.Globalization;
using TooBusy.Core.Run;

namespace TooBusy.Cli.Terminal;

// Where the page goes when the run on it is over.
public enum AfterRun
{
    Menu,
    Exit,
}

// The page of a run. Its body is the log of the run, the newest lines at the bottom. Under it stands what the run is
// doing right now, with the dots that run while it works, and the line where commands are typed: `/` starts one, the
// commands that mean something at the moment are listed under the line, and Enter runs the first that fits. While the
// run works they are those of the run; when it is over, the ones that leave the page.
//
// The run goes on by itself while the page waits for a key: the page looks for one again and again and draws what
// has changed. Leaving the page with Ctrl+C kills the run: its session is stopped, and its task stays as it is.
public sealed class RunScreen(Page page, Func<DateTimeOffset> now)
{
    // How often the page is drawn anew for the times it shows.
    static readonly TimeSpan Refresh = TimeSpan.FromMilliseconds(250);

    // As many lines of the log are kept for the body: the page never shows more.
    const int Kept = 400;

    // A title longer than this gives way to what follows it.
    const int Title = 48;

    static readonly Entry[] OfRun =
    [
        new("stop", "finish the current task, then stop", RunCommand.Stop, RunCommands.Stop),
        new("continue", "take /stop back", RunCommand.Continue, RunCommands.Continue),
        new("abort", "stop as soon as possible: nothing committed, a report in the task", RunCommand.Abort, RunCommands.Abort),
        new("nudge", "tell the session to go on alone, now", RunCommand.Nudge, RunCommands.Nudge),
        new("hold", "tell the session nothing: it waits for you", RunCommand.Hold, RunCommands.Hold),
    ];

    static readonly Entry[] OfEnd =
    [
        new("menu", "go back to the menu", After: AfterRun.Menu),
        new("exit", "leave toobusy", After: AfterRun.Exit),
    ];

    // The most commands that are listed at once: the place under the line is as tall, so that nothing jumps.
    const int Listed = 4;

    readonly List<RunLine> log = [];

    // Everything the run said, for what is left in the terminal when the page is closed.
    public IReadOnlyList<RunLine> Log => log;

    // How the run ended; null while it goes on, and when the page was left before it ended.
    public RunResult? Result { get; private set; }

    // Stays until the run is over and the user leaves with a command; says which one.
    public async Task<AfterRun> RunAsync(IQueueRun run, CancellationToken cancellationToken)
    {
        var bar = page.Status;
        page.EscapeLeaves = false;
        page.Keys = "";
        page.Foot = Line.Empty;
        page.Leaving = "stop the session and exit";

        var feed = new RunFeed();
        using var kill = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var working = Task.Run(() => run.RunAsync(feed, kill.Token), CancellationToken.None);
        var never = new TaskCompletionSource().Task;
        var editor = new LineEditor("");
        var drawn = "";
        try
        {
            while (true)
            {
                if (working.IsCompleted && Result is null)
                {
                    Result = await working;
                    page.Leaving = "exit";
                }

                feed.Take(log);
                var status = feed.Status;
                var entries = Result is not null ? OfEnd : [.. OfRun.Where(entry => status.Available.HasFlag(entry.Flag))];
                var typed = editor.Text;
                var fitting = typed.StartsWith('/') ? Fitting(entries, typed[1..].Trim()) : [];

                var (shown, caret) = editor.View(Math.Max(4, page.Width - 3));
                var choice = new List<Line> { Line.Of(shown) };
                if (typed.Length > 0 && !typed.StartsWith('/'))
                    choice.Add(Line.Of("Commands start with /.", Tone.Muted));
                else if (typed.Length > 0 && fitting.Count == 0)
                    choice.Add(Line.Of("No command fits.", Tone.Error));
                else if (typed.Length > 0)
                    choice.AddRange(fitting.Select((entry, index) => Picker.Row(index == 0, "/" + entry.Name.PadRight(Widest(fitting)), entry.About)));
                else
                    choice.AddRange(entries.Select(entry => new Line(new Part("  /" + entry.Name.PadRight(Widest(entries)), Tone.Quiet), new Part("  " + entry.About, Tone.Muted))));
                while (choice.Count < Listed + 1)
                    choice.Add(Line.Empty);

                page.Status = Bar(bar, status.Limits);
                page.Body = [.. log.Skip(Math.Max(0, log.Count - Kept)).SelectMany(line => RunLook.Wrapped(line, page.Width - 2))];
                // An empty line parts what the run is doing from the log, which fills the page down to it.
                List<Line> question = [Line.Empty, Doing(status), Result is null ? Line.Of(new string(Screen.Dot, Screen.Dots), Tone.Muted) : Line.Empty];

                // A page that shows nothing new is not drawn again: the times it shows change once in a second.
                var showing = string.Join('\n', page.Status, log.Count, Told(question[1]), string.Join('\n', choice.Select(Told)), caret, page.Width);
                if (showing != drawn)
                {
                    page.Draw(question, choice, fitting.Count > 0 ? "tab complete · enter run" : "", new Caret(0, caret), fitting.Count > 0 ? 1 : null, running: Result is null ? 2 : null);
                    drawn = showing;
                }

                var wake = Result is not null ? never : Task.WhenAny(working, feed.Changed, Task.Delay(Refresh, CancellationToken.None));
                if (await page.ReadAsync(wake) is not { } key)
                    continue;

                // A key is answered at once, whatever the page showed before it.
                drawn = "";
                if (key.Key == ConsoleKey.Enter && fitting.Count > 0)
                {
                    editor.Set("");
                    if (fitting[0].After is { } after)
                        return after;

                    log.Add(new RunLine(RunMark.Note, $"❯ /{fitting[0].Name}"));
                    run.Send(fitting[0].Command);
                }
                else if (key.Key == ConsoleKey.Tab && fitting.Count > 0)
                {
                    editor.Set("/" + fitting[0].Name);
                }
                else if (key.Key == ConsoleKey.Escape)
                {
                    editor.Set("");
                }
                else if (key.Key != ConsoleKey.Enter)
                {
                    editor.Press(key);
                }
            }
        }
        catch (OperationCanceledException)
        {
            // The page is left while the run works: the run is killed, and what it says of that is kept.
            await kill.CancelAsync();
            Result ??= await working;
            feed.Take(log);
            throw;
        }
        finally
        {
            page.Leaving = "exit";
            page.Status = bar;
        }
    }

    // What the run is doing, in one line.
    Line Doing(RunStatus status)
    {
        if (Result is { } result)
            return new Line(new Part("The run is over", Tone.Strong), new Part($" · {result.Done} done", Tone.Muted));
        if (status.Task is not { } task)
            return new Line(new Part(status.Text, Tone.Muted), new Part(status.Until is { } reset ? $" · {Clock(reset - now())}" : "", Tone.Muted));

        var title = task.Title.Length > Title ? task.Title[..(Title - 1)] + "…" : task.Title;
        var parts = new List<Part> { new($"#{task.Number} {title}", Tone.Strong) };
        if (status.Since is { } since)
            parts.Add(new Part($" · {Clock(now() - since)}", Tone.Muted));

        switch (status.Phase)
        {
            case RunPhase.WaitingForOwner:
                parts.Add(new Part(" · waits for the owner", Tone.Warning));
                parts.Add(new Part(status.Until is { } told ? $" · is told to go on alone in {Clock(told - now())}" : " · gets no message", Tone.Muted));
                break;
            case RunPhase.WaitingForLimit:
                parts.Add(new Part(" · waits for a usage limit", Tone.Warning));
                if (status.Until is { } until)
                    parts.Add(new Part($" · goes on in {Clock(until - now())}", Tone.Muted));
                break;
            case RunPhase.Preparing:
                parts.Add(new Part(" · starting", Tone.Muted));
                break;
            default:
                if (status.Step is { } step)
                    parts.Add(new Part($" · {step}", Tone.Muted));
                if (status.Context > 0)
                    parts.Add(new Part(string.Create(CultureInfo.InvariantCulture, $" · {status.Context / 1000}k context"), Tone.Muted));
                break;
        }

        if (status.Aborting)
            parts.Add(new Part(" · wrapping up", Tone.Warning));
        else if (status.Stopping)
            parts.Add(new Part(" · the last task of this run", Tone.Warning));
        else if (status.Queued > 0)
            parts.Add(new Part(string.Create(CultureInfo.InvariantCulture, $" · {status.Queued} queued"), Tone.Muted));
        return new Line([.. parts]);
    }

    // What the bar says at its right: what the page is doing, and how much of the limits is used, when it is known.
    static string Bar(string doing, UsageLimits limits)
    {
        var used = ((UsageWindow?[])[limits.Near, limits.Far]).OfType<UsageWindow>().Select(window => $"{window.Name} {Spoken.Percent(window.Used)}");
        return string.Join(" · ", ((string[])[doing, .. used]).Where(part => part.Length > 0));
    }

    // A length of time as a clock shows it: `0:07`, `12:40`, `1:05:09`.
    static string Clock(TimeSpan time)
    {
        time = time < TimeSpan.Zero ? TimeSpan.Zero : time;
        return time.TotalHours >= 1
            ? string.Create(CultureInfo.InvariantCulture, $"{(int)time.TotalHours}:{time.Minutes:00}:{time.Seconds:00}")
            : string.Create(CultureInfo.InvariantCulture, $"{(int)time.TotalMinutes}:{time.Seconds:00}");
    }

    static string Told(Line line) => string.Concat(line.Parts.Select(part => part.Text));

    static int Widest(IReadOnlyList<Entry> entries) => entries.Select(entry => entry.Name.Length).DefaultIfEmpty().Max();

    // The commands that fit what is typed after the slash, those that start with it first.
    static List<Entry> Fitting(IReadOnlyList<Entry> entries, string typed) =>
    [
        .. entries.Where(entry => entry.Name.StartsWith(typed, StringComparison.OrdinalIgnoreCase)),
        .. entries.Where(entry => !entry.Name.StartsWith(typed, StringComparison.OrdinalIgnoreCase) && entry.Name.Contains(typed, StringComparison.OrdinalIgnoreCase)),
    ];

    // A command of the line: one that is sent to the run, known by the flag that tells whether it means something
    // now, or one that leaves the page.
    sealed record Entry(string Name, string About, RunCommand Command = default, RunCommands Flag = RunCommands.None, AfterRun? After = null);
}
