using System.Text;

namespace TooBusy.Cli.Terminal;

// The output of a terminal that may go away while the tool writes to it, as when its window is closed or the user
// is logged out: the first write that fails says why, in Lost, and what is written after it is dropped. Nothing
// written to it throws then, so that a page that has the terminal, a timer that draws on it and what gives it back
// as the tool ends do not take the tool down before a run is over; the page learns of it from Lost.
public sealed class TerminalWriter(TextWriter terminal) : TextWriter
{
    readonly Lock gate = new();
    volatile string? lost;

    // Why the terminal can no longer be written to; null while it can.
    public string? Lost => lost;

    public override Encoding Encoding => terminal.Encoding;

    public override IFormatProvider FormatProvider => terminal.FormatProvider;

    public override void Write(char value) => Put(() => terminal.Write(value));

    public override void Write(char[] buffer, int index, int count) => Put(() => terminal.Write(buffer, index, count));

    public override void Write(ReadOnlySpan<char> buffer)
    {
        var text = buffer.ToString();
        Put(() => terminal.Write(text));
    }

    public override void Write(string? value) => Put(() => terminal.Write(value));

    public override void WriteLine() => Put(terminal.WriteLine);

    public override void WriteLine(string? value) => Put(() => terminal.WriteLine(value));

    public override void Flush() => Put(terminal.Flush);

    void Put(Action write)
    {
        lock (gate)
        {
            if (lost is not null)
                return;

            try
            {
                write();
            }
            catch (Exception failed) when (failed is IOException or ObjectDisposedException)
            {
                lost = failed.Message;
            }
        }
    }
}
