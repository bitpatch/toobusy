using TooBusy.Cli.Terminal;
using TooBusy.Core.Queue;

namespace TooBusy.Cli.Tests;

public sealed class MilestoneScreenTests : IDisposable
{
    static readonly Milestone[] Open = [new("Backlog", null, 1), new("v0.10.0", null, 0), new("v0.2.0", new DateOnly(2030, 1, 15), 12)];

    readonly TestTerminal terminal = new(width: 80, height: 20);

    public void Dispose() => terminal.Dispose();

    [Fact]
    public void TheMilestonesAreOfferedInTheirOrderAndWorkingWithoutOneAfterThem()
    {
        terminal.Keys.Press(Keys.Enter);

        var choice = Ask(Open, null);

        Assert.Equal(new MilestoneChoice("v0.2.0"), choice);
        terminal.AssertSaw("""
             Milestone
             The milestone to work on: only its tasks are taken.
             ❯ v0.2.0        due 2030-01-15 · 12 open tasks
               v0.10.0       no open tasks
               Backlog       1 open task
               No milestone  take tasks whatever their milestone
             ↑↓ move · enter choose
            """.ReplaceLineEndings("\n").TrimEnd(' '));
    }

    [Fact]
    public void TheLastChoiceIsWorkingWithoutAMilestone()
    {
        terminal.Keys.Press(Keys.Up, Keys.Enter);

        Assert.Equal(MilestoneChoice.None, Ask(Open, null));
    }

    [Fact]
    public void ThePointerStartsOnWhatIsChosen()
    {
        terminal.Keys.Press(Keys.Enter, Keys.Enter);

        Assert.Equal(new MilestoneChoice("Backlog"), Ask(Open, new MilestoneChoice("Backlog")));
        Assert.Equal(MilestoneChoice.None, Ask(Open, MilestoneChoice.None));
    }

    [Fact]
    public void AMilestoneThatIsGoneIsToBeChosenAgain()
    {
        terminal.Keys.Press(Keys.Enter);

        Ask(Open, new MilestoneChoice("v0.1.0"));

        terminal.AssertSaw(" Milestone\n The milestone “v0.1.0” is not open any more. Choose again.\n ❯ v0.2.0");
    }

    [Fact]
    public void WithoutMilestonesToReadOnlyWorkingWithoutOneIsOffered()
    {
        terminal.Keys.Press(Keys.Enter);

        Assert.Equal(MilestoneChoice.None, Ask(null, null));
        terminal.AssertSaw(" The milestones cannot be read from GitHub, so there are none to choose from.\n ❯ No milestone  take tasks whatever their milestone");
    }

    [Fact]
    public void WhereThereIsSomewhereToGoBackToTheListEndsWithBack()
    {
        terminal.Keys.Press(Keys.Up, Keys.Enter);

        using var page = terminal.Open();
        Assert.Null(MilestoneScreen.Ask(page, Open, MilestoneStanding.Of(null, Open)));
        terminal.AssertSaw("   No milestone  take tasks whatever their milestone\n ❯ Back\n ↑↓ move · enter choose");
    }

    [Fact]
    public void EscapeChoosesNothing()
    {
        terminal.Keys.Press(Keys.Escape, Keys.Escape);

        Assert.Null(Ask(Open, null));
    }

    MilestoneChoice? Ask(IReadOnlyList<Milestone>? open, MilestoneChoice? chosen)
    {
        using var page = terminal.Open();
        // As on the first run, where Escape leaves the page: there is nowhere to go back to, and no `Back`.
        page.EscapeLeaves = true;
        return MilestoneScreen.Ask(page, open, MilestoneStanding.Of(chosen, open));
    }
}
