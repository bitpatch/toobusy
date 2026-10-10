using TooBusy.Core.Run;

namespace TooBusy.Cli.Terminal;

// What the lines of a run look like, on its page and in the lines it leaves in the terminal: the mark of a line has
// the colour of what the line tells, and what is said under a line is muted.
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

    static IEnumerable<string> Pieces(string text, int room)
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

    public void Show(RunStatus status)
    {
    }
}
