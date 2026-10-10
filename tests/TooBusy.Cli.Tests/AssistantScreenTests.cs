using TooBusy.Cli.Terminal;

namespace TooBusy.Cli.Tests;

public sealed class AssistantScreenTests : IDisposable
{
    readonly TestTerminal terminal = new(width: 80, height: 20);

    public void Dispose() => terminal.Dispose();

    [Fact]
    public void TheModelTheEffortAndTheWeeklyLimitAreOfferedWithWhatIsChosen()
    {
        terminal.Keys.Press(Keys.Down, Keys.Enter);

        Assert.Equal(AssistantAction.Effort, Ask());
        terminal.AssertSaw("""
             Assistant
             ❯ Model         opus
               Effort        high
               Weekly limit  80%
               Back          esc
             ↑↓ move · enter choose · ctrl+c exit
            """.ReplaceLineEndings("\n").TrimEnd(' '));
    }

    [Fact]
    public void ThePointerStartsOnTheGivenRow()
    {
        terminal.Keys.Press(Keys.Enter);

        Assert.Equal(AssistantAction.Effort, Ask(at: 1));
    }

    [Fact]
    public void TheLastOfTheThreeIsTheWeeklyLimit()
    {
        terminal.Keys.Press(Keys.Down, Keys.Down, Keys.Enter);

        Assert.Equal(AssistantAction.Limit, Ask());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void EscapeAndTheLastRowGoBack(bool row)
    {
        terminal.Keys.Press(row ? [Keys.Up, Keys.Enter] : [Keys.Escape]);

        Assert.Null(Ask());
    }

    AssistantAction? Ask(int at = 0)
    {
        using var page = terminal.Open();
        return AssistantScreen.Ask(page, "opus", "high", "80%", at);
    }
}
