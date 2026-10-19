using TooBusy.Cli.Terminal;

namespace TooBusy.Cli.Tests;

// What the screen tests rely on the terminal of a test for.
public sealed class TestTerminalTests : IDisposable
{
    readonly TestTerminal terminal = new(width: 40, height: 8);

    public void Dispose() => terminal.Dispose();

    static string Settled(int frame) => $"✔ #{frame} Export the data";

    // The foot of a frame: its rows in other letters than those of the frame before, which it is written over.
    static string[] Foot(int frame) => [.. Enumerable.Repeat(new string(frame % 2 == 0 ? 'a' : 'b', 30), 2)];

    // A page draws a tape on a thread of its own while a test reads it, as the page of a run does: the test sees each
    // frame whole, never a row half written over the one before or the rows while one is added to them.
    [Fact]
    public async Task ATestReadsATapeWholeWhileAPageDrawsItOnAThreadOfItsOwn()
    {
        const int Frames = 200;
        using var page = terminal.Open();
        page.Status = "Running";
        var tape = page.Unroll();
        var bar = terminal.Tape.Text;

        // The tape after so many frames: each settles a line, which takes the window a row down, and draws its foot.
        string Whole(int frames) => string.Join('\n', [bar, .. Enumerable.Range(1, frames).Select(frame => " " + Settled(frame)), .. frames == 0 ? [] : Foot(frames).Select(row => " " + row)]);

        var drawing = Task.Run(
            () =>
            {
                for (var frame = 1; frame <= Frames; frame++)
                    tape.Draw([Line.Of(Settled(frame))], new Strip([.. Foot(frame).Select(row => Line.Of(row))]));
            },
            TestContext.Current.CancellationToken);

        var waited = System.Diagnostics.Stopwatch.StartNew();
        try
        {
            while (!drawing.IsCompleted)
            {
                Assert.True(waited.Elapsed < Keys.Patience, $"The page did not draw {Frames} frames in {Keys.Patience.TotalSeconds} s.");
                var text = terminal.Tape.Text;
                Assert.Equal(Whole(text.Split('\n').Count(row => row.StartsWith(" ✔", StringComparison.Ordinal))), text);
                Assert.True(terminal.Saw(text), $"The tape never had what was read from it:\n{text}");
            }
        }
        finally
        {
            await drawing.WaitAsync(Keys.Patience, TestContext.Current.CancellationToken);
        }

        Assert.Equal(Whole(Frames), terminal.Tape.Text);
    }
}
