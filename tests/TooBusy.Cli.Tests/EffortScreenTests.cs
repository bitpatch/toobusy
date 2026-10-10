using TooBusy.Cli.Terminal;

namespace TooBusy.Cli.Tests;

public sealed class EffortScreenTests : IDisposable
{
    readonly TestTerminal terminal = new(width: 80, height: 20);

    public void Dispose() => terminal.Dispose();

    [Fact]
    public void TheLevelsAreOfferedAndThePointerStartsOnHigh()
    {
        terminal.Keys.Press(Keys.Enter);

        Assert.Equal("high", Ask(null));
        terminal.AssertSaw("""
             Effort
             How hard the assistant thinks over a task by default.
               low
               medium
             ❯ high
               xhigh
               max
             ↑↓ move · enter choose
            """.ReplaceLineEndings("\n").TrimEnd(' '));
    }

    [Fact]
    public void ThePointerStartsOnWhatIsChosen()
    {
        terminal.Keys.Press(Keys.Down, Keys.Enter);

        Assert.Equal("medium", Ask("low"));
    }

    [Fact]
    public void WhereThereIsSomewhereToGoBackToTheListEndsWithBack()
    {
        terminal.Keys.Press(Keys.Down, Keys.Down, Keys.Down, Keys.Enter);

        using var page = terminal.Open();
        Assert.Null(EffortScreen.Ask(page, "high"));
        terminal.AssertSaw("   max\n ❯ Back  esc\n ↑↓ move · enter choose · ctrl+c exit");
    }

    [Fact]
    public void EscapeChoosesNothing()
    {
        terminal.Keys.Press(Keys.Escape, Keys.Escape);

        Assert.Null(Ask("max"));
    }

    string? Ask(string? chosen)
    {
        using var page = terminal.Open();
        // As on the first run, where Escape leaves the page: there is nowhere to go back to, and no `Back`.
        page.EscapeLeaves = true;
        return EffortScreen.Ask(page, chosen);
    }
}
