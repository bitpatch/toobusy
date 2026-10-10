using TooBusy.Cli.Terminal;

namespace TooBusy.Cli.Tests;

public sealed class LimitScreenTests : IDisposable
{
    readonly TestTerminal terminal = new(width: 80, height: 20);

    public void Dispose() => terminal.Dispose();

    [Fact]
    public void TheSharesAreOfferedAndThePointerStartsOnTheOneInForce()
    {
        terminal.Keys.Press(Keys.Up, Keys.Enter);

        Assert.Equal(90, Ask(96));
        terminal.AssertSaw("""
             Weekly limit
             A run takes no next task once this much of the weekly limit is used.
               50%
               60%
               70%
               80%
               90%
             ❯ 96%
               Back  esc
             ↑↓ move · enter choose · ctrl+c exit
            """.ReplaceLineEndings("\n").TrimEnd(' '));
    }

    [Fact]
    public void AShareThatIsNotOfferedStandsAmongThoseThatAre()
    {
        terminal.Keys.Press(Keys.Enter);

        Assert.Equal(75, Ask(75));
        terminal.AssertSaw("   70%\n ❯ 75%\n   80%");
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void EscapeAndTheLastRowGoBack(bool row)
    {
        terminal.Keys.Press(row ? [Keys.Down, Keys.Enter] : [Keys.Escape]);

        Assert.Null(Ask(96));
    }

    int? Ask(int share)
    {
        using var page = terminal.Open();
        return LimitScreen.Ask(page, share);
    }
}
