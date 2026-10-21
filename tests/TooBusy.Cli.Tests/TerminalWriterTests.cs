using TooBusy.Cli.Terminal;

namespace TooBusy.Cli.Tests;

public sealed class TerminalWriterTests
{
    [Fact]
    public void WhatIsWrittenGoesToTheTerminalWhileItIsThere()
    {
        using var written = new StringWriter();
        using var terminal = new TerminalWriter(written);

        terminal.Write("toobusy");
        terminal.Write(' ');
        terminal.WriteLine("runs");
        terminal.Flush();

        Assert.Equal("toobusy runs" + Environment.NewLine, written.ToString());
        Assert.Null(terminal.Lost);
    }

    // The window was closed: the first write that fails says why, and nothing after it reaches the terminal or throws.
    [Fact]
    public void ATerminalThatWentAwayIsWrittenToNoMoreAndThrowsNothing()
    {
        using var gone = new Gone();
        using var terminal = new TerminalWriter(gone);

        terminal.Write("the foot of the run");
        terminal.WriteLine("how the run went");
        terminal.Flush();

        Assert.Equal("Input/output error", terminal.Lost);
        Assert.Equal(1, gone.Tries);
    }

    sealed class Gone : StringWriter
    {
        public int Tries { get; private set; }

        public override void Write(string? value)
        {
            Tries++;
            throw new IOException("Input/output error");
        }
    }
}
