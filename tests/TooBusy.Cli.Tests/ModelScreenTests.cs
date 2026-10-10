using TooBusy.Cli.Terminal;
using TooBusy.Core.Assistant;

namespace TooBusy.Cli.Tests;

public sealed class ModelScreenTests : IDisposable
{
    readonly TestTerminal terminal = new(width: 80, height: 20);

    public void Dispose() => terminal.Dispose();

    [Fact]
    public void TheAssistantsOwnModelIsOfferedFirstThenTheKnownOnesAndAnyOther()
    {
        terminal.Keys.Press(Keys.Enter);

        Assert.Equal(ModelChoice.AssistantsOwn, Ask(null));
        terminal.AssertSaw("""
             Model
             The model the tasks are done with by default.
             ❯ Assistant's own  no model is named: the assistant takes its default
               fable
               opus
               sonnet
               haiku
               Other…           any model, by its name
             ↑↓ move · enter choose
            """.ReplaceLineEndings("\n").TrimEnd(' '));
    }

    [Fact]
    public void AKnownModelIsChosenByItsRow()
    {
        terminal.Keys.Press(Keys.Down, Keys.Down, Keys.Enter);

        Assert.Equal(new ModelChoice("opus"), Ask(null));
    }

    [Fact]
    public void ThePointerStartsOnWhatIsChosen()
    {
        terminal.Keys.Press(Keys.Enter, Keys.Enter);

        Assert.Equal(new ModelChoice("sonnet"), Ask(new ModelChoice("sonnet")));
        Assert.Equal(ModelChoice.AssistantsOwn, Ask(ModelChoice.AssistantsOwn));
    }

    [Fact]
    public void OtherAsksForTheNameOfTheModel()
    {
        terminal.Keys.Press(Keys.Up, Keys.Enter).Type(" claude-opus-4 ").Press(Keys.Enter);

        Assert.Equal(new ModelChoice("claude-opus-4"), Ask(null));
        terminal.AssertSaw(" Model\n The name of the model, as the assistant takes it.\n  claude-opus-4");
    }

    [Fact]
    public void AnEmptyNameIsRefused()
    {
        terminal.Keys.Press(Keys.Up, Keys.Enter, Keys.Enter).Type("x").Press(Keys.Enter);

        Assert.Equal(new ModelChoice("x"), Ask(null));
        terminal.AssertSaw(" Model\n Type the name of a model.");
    }

    [Fact]
    public void EscapeFromTheNameGoesBackToTheList()
    {
        terminal.Keys.Press(Keys.Up, Keys.Enter, Keys.Escape, Keys.Up, Keys.Enter);

        Assert.Equal(new ModelChoice("haiku"), Ask(null));
    }

    [Fact]
    public void AModelOfAnotherNameIsProposedWhenItIsAskedAgain()
    {
        terminal.Keys.Press(Keys.Enter, Keys.Enter);

        Assert.Equal(new ModelChoice("claude-opus-4"), Ask(new ModelChoice("claude-opus-4")));
        terminal.AssertSaw(" ❯ Other…           any model, by its name  claude-opus-4");
    }

    [Fact]
    public void WhereThereIsSomewhereToGoBackToTheListEndsWithBack()
    {
        terminal.Keys.Press(Keys.Up, Keys.Enter);

        using var page = terminal.Open();
        Assert.Null(ModelScreen.Ask(page, null));
        terminal.AssertSaw("   Other…           any model, by its name\n ❯ Back             esc\n ↑↓ move · enter choose · ctrl+c exit");
    }

    [Fact]
    public void EscapeChoosesNothing()
    {
        terminal.Keys.Press(Keys.Escape, Keys.Escape);

        Assert.Null(Ask(null));
    }

    ModelChoice? Ask(ModelChoice? chosen)
    {
        using var page = terminal.Open();
        // As on the first run, where Escape leaves the page: there is nowhere to go back to, and no `Back`.
        page.EscapeLeaves = true;
        return ModelScreen.Ask(page, chosen);
    }
}
