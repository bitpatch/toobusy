using System.Globalization;
using TooBusy.Core.Run;

namespace TooBusy.Cli.Terminal;

// What a run looks like: the lines of its log, on a tape and as they are printed when there is no terminal, the line
// of a task that is over, and the summary a run ends with. The mark of a line has the colour of what the line tells,
// and what is said under a line is muted.
public static class RunLook
{
    public static Tone Mark(RunMark mark) => mark switch
    {
        RunMark.Head => Tone.Strong,
        RunMark.Started or RunMark.GoesOn => Tone.Accent,
        RunMark.Done => Tone.Success,
        RunMark.Failed => Tone.Error,
        RunMark.Partial or RunMark.Owner or RunMark.Interrupted or RunMark.Attention => Tone.Warning,
        _ => Tone.Muted,
    };

    public static Tone Text(RunMark mark) => mark switch
    {
        RunMark.Head => Tone.Strong,
        RunMark.Note => Tone.Muted,
        _ => Tone.Plain,
    };

    // The line as the terminal shows it when there is no page around it.
    public static string Painted(RunLine line, Palette palette) =>
        palette.Paint(Mark(line.Mark), line.Symbol) + " " + palette.Paint(Text(line.Mark), line.Text);

    // The line on as many lines as the width asks for, broken between words; what goes on to the next line stands
    // under the text, not under the mark.
    public static IEnumerable<Line> Wrapped(RunLine line, int width)
    {
        var room = Math.Max(10, width - 2);
        var first = true;
        foreach (var piece in Pieces(line.Text, room))
        {
            yield return first
                ? new Line(new Part(line.Symbol + " ", Mark(line.Mark)), new Part(piece, Text(line.Mark)))
                : new Line(new Part("  " + piece, Text(line.Mark)));
            first = false;
        }
    }

    // A task that is over, in one line: the mark of how it went, its number and title, and the time it took. A title
    // that does not fit gives way, so that the time is always there.
    public static Line Ended(TaskEnd ended, int width)
    {
        var took = "  " + Clock(ended.Took);
        var room = Math.Max(4, width - 2 - took.Length);
        var task = Spoken.Task(ended.Task);
        var title = task.Length > room ? task[..(room - 1)] + "…" : task;
        return new Line(new Part(new RunLine(ended.Mark, "").Symbol + " ", Mark(ended.Mark)), new Part(title), new Part(took, Tone.Muted));
    }

    // The plan a session keeps of its task, as a bar under it: a cell for each step, full and in the accent for one
    // that is done, empty and muted for the others. It stands under the text of the lines above it.
    public static Line Plan(IReadOnlyList<PlanStep> plan)
    {
        var parts = new List<Part> { new(new string(' ', Inset)) };
        for (var from = 0; from < plan.Count;)
        {
            var done = plan[from] == PlanStep.Done;
            var count = plan.Skip(from).TakeWhile(step => step == PlanStep.Done == done).Count();
            parts.Add(new Part(string.Concat(Enumerable.Repeat(done ? Tape.Full : "▱", count)), done ? Tone.Accent : Tone.Muted));
            from += count;
        }

        return new Line([.. parts]);
    }

    // The columns of the cells of that bar whose steps the session is at: they blink.
    public static IEnumerable<int> Active(IReadOnlyList<PlanStep> plan) =>
        Enumerable.Range(0, plan.Count).Where(step => plan[step] == PlanStep.Active).Select(step => Inset + step);

    const int Inset = 2;

    // What a run that is over leaves under its tasks: how many there were, how long it took and how they went; and,
    // for a run that did not simply run out of tasks, the line of what ended it.
    public static (Line Tasks, RunLine? Why) Summary(RunResult result, IReadOnlyList<TaskEnd> ends, TimeSpan took)
    {
        (RunMark Mark, string Went)[] kinds =
        [
            (RunMark.Done, "done"), (RunMark.Partial, "done in part"), (RunMark.Owner, "for the owner"),
            (RunMark.Interrupted, "interrupted"), (RunMark.Failed, "failed"), (RunMark.Paused, "paused"),
        ];
        var went = kinds.Select(kind => (kind.Went, Count: ends.Count(ended => ended.Mark == kind.Mark))).Where(kind => kind.Count > 0)
            .Select(kind => string.Create(CultureInfo.InvariantCulture, $" · {kind.Count} {kind.Went}"));
        var tasks = new Line(new Part($"{Spoken.Tasks(ends.Count)} in {Spoken.Time(took)}", Tone.Strong), new Part(string.Concat(went), Tone.Muted));
        return (tasks, result switch
        {
            { End: RunEnd.Emptied } => null,
            { End: RunEnd.Stopped } => new RunLine(RunMark.Paused, "Stopped, as you asked"),
            { End: RunEnd.Killed } => new RunLine(RunMark.Interrupted, result.Why is null ? "Killed" : $"Killed: {result.Why}"),
            { End: RunEnd.Limited } => new RunLine(RunMark.Paused, $"Stopped: {result.Why ?? "a usage limit"}"),
            _ => new RunLine(RunMark.Failed, $"Stopped: {result.Why ?? "something went wrong"}"),
        });
    }

    // A length of time as a clock shows it: `0:07`, `12:40`, `1:05:09`.
    public static string Clock(TimeSpan time)
    {
        time = time < TimeSpan.Zero ? TimeSpan.Zero : time;
        return time.TotalHours >= 1
            ? string.Create(CultureInfo.InvariantCulture, $"{(int)time.TotalHours}:{time.Minutes:00}:{time.Seconds:00}")
            : string.Create(CultureInfo.InvariantCulture, $"{(int)time.TotalMinutes}:{time.Seconds:00}");
    }

    // A text on as many lines as the room asks for, broken between words.
    public static IEnumerable<string> Pieces(string text, int room)
    {
        var rest = text;
        while (rest.Length > room)
        {
            var cut = rest.LastIndexOf(' ', room);
            if (cut <= 0)
                cut = room;
            yield return rest[..cut];
            rest = rest[cut..].TrimStart(' ');
        }

        yield return rest;
    }
}

// The lines of a run as they are printed when there is no terminal to open a page in.
public sealed class PlainRun(TextWriter output, Palette palette) : IRunView
{
    public void Say(RunLine line) => output.WriteLine(RunLook.Painted(line, palette));

    // The log has said how the task went already.
    public void Report(TaskEnd ended)
    {
    }

    public void Show(RunStatus status)
    {
    }
}
