using TooBusy.Core.Doctor;

namespace TooBusy.Cli.Terminal;

// The checks of `doctor` as lines: a mark, the name of the check and what it found, and under a failed one what
// else is wrong and its fix. In a terminal the check that is running has a line of its own with a sign that turns,
// written to `live`; the line is erased when the check ends, and its result is written in its place. Without a
// terminal a line is written when its check ends. A list that tells failures only leaves nothing of the others.
public sealed class CheckList : ICheckupView, IDisposable
{
    // The names of the checks stand in a column as wide as the longest of them.
    const int Names = 17;

    readonly TextWriter output;
    readonly Palette palette;
    readonly TextWriter? live;
    readonly Palette livePalette;
    readonly bool failuresOnly;
    readonly Lock drawing = new();
    readonly IDisposable? beat;

    string? running;
    int step;

    public CheckList(TextWriter output, Palette palette, TextWriter? live = null, Palette? livePalette = null, TerminalDevice? terminal = null, bool failuresOnly = false)
    {
        (this.output, this.palette, this.failuresOnly) = (output, palette, failuresOnly);
        this.live = terminal is null ? null : live;
        this.livePalette = livePalette ?? palette;

        // Without colours nothing turns, as on a tape: the sign stands as it is drawn.
        beat = this.live is null || !this.livePalette.DrawsCursor ? null : terminal!.Every(Screen.Beat, Turn);
    }

    public void Start(string name)
    {
        lock (drawing)
        {
            if (live is null)
                return;

            running = name;
            Draw();
        }
    }

    public void Done(CheckResult result)
    {
        lock (drawing)
        {
            Erase();
            if (failuresOnly && result.State != CheckState.Failed)
                return;

            var (mark, text) = result.State switch
            {
                CheckState.Passed => (palette.Success("✔"), result.Text),
                CheckState.Failed => (palette.Error("✘"), palette.Error(result.Text)),
                _ => (palette.Muted("○"), palette.Muted(result.Text)),
            };
            output.WriteLine(text.Length == 0 ? $"{mark} {result.Name}" : $"{mark} {result.Name.PadRight(Names)}  {text}");
            foreach (var detail in result.Details)
                output.WriteLine($"  {palette.Error(detail)}");
            if (result.Fix is { } fix)
                output.WriteLine($"  {palette.Muted($"fix: {fix}")}");
            output.Flush();
        }
    }

    public void Dispose()
    {
        beat?.Dispose();
        lock (drawing)
            Erase();
    }

    void Turn()
    {
        lock (drawing)
        {
            if (running is null)
                return;

            step++;
            Draw();
        }
    }

    // The line of the check that is running, written over itself.
    void Draw()
    {
        live!.Write($"\r{livePalette.Accent(Tape.Sign(step))} {running}");
        live.Flush();
    }

    void Erase()
    {
        if (running is null)
            return;

        running = null;
        live!.Write("\r\u001b[K");
        live.Flush();
    }
}
