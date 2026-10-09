using TooBusy.Cli.Terminal;
using TooBusy.Core.Setup;

namespace TooBusy.Cli.Tests;

public sealed class TerminalDialogTests : IDisposable
{
    static readonly SetupOption[] Rules = [new("lowest-version", "→ v.0.2.0"), new("earliest-due", "→ Polish"), new("none", "")];

    readonly StringWriter output = new();
    readonly Keys keys = new();

    public void Dispose() => output.Dispose();

    [Fact]
    public void EnterChoosesWhatIsProposed()
    {
        keys.Press(Keys.Enter);

        Assert.Equal(1, Dialog().Choose("Milestone rule", Rules, 1));
        Assert.Equal("✔ Milestone rule   earliest-due", LastLine());
    }

    [Fact]
    public void TheArrowsMoveThePointerAndWrapAround()
    {
        keys.Press(Keys.Up, Keys.Enter);
        Assert.Equal(2, Dialog().Choose("Milestone rule", Rules, 0));

        keys.Press(Keys.Down, Keys.Down, Keys.Down, Keys.Down, Keys.Enter);
        Assert.Equal(1, Dialog().Choose("Milestone rule", Rules, 0));
    }

    [Fact]
    public void ASelectionShowsItsOptionsWithThePointerOnOne()
    {
        keys.Press(Keys.Enter);

        Dialog().Choose("Milestone rule", Rules, 0);

        Assert.Contains("? Milestone rule\n❯ lowest-version  → v.0.2.0\n  earliest-due    → Polish\n  none          \n  ↑↓ to move, Enter to choose", output.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void AnAnsweredQuestionCollapsesIntoOneLine()
    {
        keys.Press(Keys.Enter);

        Dialog().Choose("Milestone rule", Rules, 0);

        // Up to the first of the five lines of the prompt, clear to the end of the screen, write the answer.
        Assert.EndsWith("\u001b[4A\r\u001b[J✔ Milestone rule   lowest-version" + Environment.NewLine, output.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void TheSpaceBarMarksAndUnmarksLabels()
    {
        keys.Press(Keys.Space, Keys.Down, Keys.Down, Keys.Space, Keys.Up, Keys.Space, Keys.Enter);

        var chosen = Dialog().ChooseMany("Blocking labels", ["bug", "draft", "manual"], [0], "none");

        Assert.Equal([1, 2], chosen);
        Assert.Contains("  ◯ bug   \n❯ ◉ draft \n  ◉ manual", output.ToString(), StringComparison.Ordinal);
        Assert.Equal("✔ Blocking labels  draft, manual", LastLine());
    }

    [Fact]
    public void NothingChosenHasItsOwnWords()
    {
        keys.Press(Keys.Enter);

        Assert.Empty(Dialog().ChooseMany("Labels to take", ["bug", "feature"], [], "any task"));
        Assert.Equal("✔ Labels to take   any task", LastLine());
    }

    [Fact]
    public void AMultipleChoiceOverNothingAsksNothing()
    {
        Assert.Empty(Dialog().ChooseMany("Blocking labels", [], [], "none"));
        Assert.Equal("✔ Blocking labels  none", LastLine());
    }

    [Fact]
    public void ALongListScrolls()
    {
        string[] labels = [.. Enumerable.Range(1, 12).Select(number => $"label-{number:00}")];
        keys.Press([.. Enumerable.Repeat(Keys.Down, 11), Keys.Space, Keys.Enter]);

        var chosen = Dialog().ChooseMany("Blocking labels", labels, [], "none");

        Assert.Equal([11], chosen);
        Assert.Contains("12 of 12, ↑↓ to move", output.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain("label-01\n  ◯ label-02\n  ◯ label-03\n  ◯ label-04\n  ◯ label-05\n  ◯ label-06\n  ◯ label-07\n  ◯ label-08\n  ◯ label-09\n  ◯ label-10\n  ◯ label-11", output.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void ATextPromptStartsFromWhatIsProposed()
    {
        keys.Press(Keys.Enter);

        Assert.Equal("acme/rocket", Dialog().Ask("Repository", "owner/name", "acme/rocket", "", _ => null));
        Assert.Contains("? Repository  owner/name\n❯ acme/rocket", output.ToString(), StringComparison.Ordinal);
        Assert.Equal("✔ Repository       acme/rocket", LastLine());
    }

    [Fact]
    public void ATextPromptIsEdited()
    {
        keys.Press(Keys.Backspace, Keys.Backspace, Keys.Backspace, Keys.Backspace, Keys.Backspace, Keys.Backspace).Type("moon").Press(Keys.Enter);

        Assert.Equal("acme/moon", Dialog().Ask("Repository", "owner/name", "acme/rocket", "", _ => null));
    }

    [Fact]
    public void ARefusedAnswerStaysInThePromptWithTheReason()
    {
        keys.Press(Keys.Enter).Type("/rocket").Press(Keys.Enter);

        var answer = Dialog().Ask("Repository", "owner/name", "acme", "", text => text.Contains('/', StringComparison.Ordinal) ? null : "no slash");

        Assert.Equal("acme/rocket", answer);
        Assert.Contains("? Repository  no slash\n❯ acme", output.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void AnEmptyAnswerHasItsOwnWords()
    {
        keys.Press(Keys.Enter);

        Assert.Equal("", Dialog().Ask("Board", "a URL", "", "none", _ => null));
        Assert.Equal("✔ Board            none", LastLine());
    }

    [Fact]
    public void EnterConfirmsAndNDeclines()
    {
        keys.Press(Keys.Enter, Keys.Space, Keys.No);

        Assert.True(Dialog().Confirm("Write the settings?"));
        Assert.Equal("✔ Write the settings? yes", LastLine());
        Assert.False(Dialog().Confirm("Write the settings?"));
        Assert.Equal("✘ Write the settings? no", LastLine());
    }

    [Fact]
    public void ColoursComeFromThePalette()
    {
        var palette = Palette.Dark;
        keys.Press(Keys.Enter);
        var dialog = new TerminalDialog(output, keys.Read, palette);

        dialog.Choose("Milestone rule", Rules, 0);
        dialog.Say(SetupTone.Added, "+ version = 1");
        dialog.Say(SetupTone.Removed, "- version = 2");
        dialog.Say(SetupTone.Failure, "gh is missing");
        dialog.Say(SetupTone.Warning, "nothing is verified");
        dialog.Say(SetupTone.Muted, "fix: brew install gh");

        var text = output.ToString();
        Assert.Contains($"{palette.Accent("?")} Milestone rule", text, StringComparison.Ordinal);
        Assert.Contains($"{palette.Accent("❯ lowest-version")}  {palette.Muted("→ v.0.2.0")}", text, StringComparison.Ordinal);
        Assert.Contains($"{palette.Success("✔")} Milestone rule", text, StringComparison.Ordinal);
        Assert.Contains(palette.Success("+ version = 1"), text, StringComparison.Ordinal);
        Assert.Contains(palette.Error("- version = 2"), text, StringComparison.Ordinal);
        Assert.Contains($"{palette.Error("✘")} {palette.Error("gh is missing")}", text, StringComparison.Ordinal);
        Assert.Contains($"{palette.Warning("!")} {palette.Warning("nothing is verified")}", text, StringComparison.Ordinal);
        Assert.Contains(palette.Muted("fix: brew install gh"), text, StringComparison.Ordinal);
    }

    TerminalDialog Dialog() => new(output, keys.Read, Palette.None);

    string LastLine() => output.ToString().Split('\n', StringSplitOptions.RemoveEmptyEntries)[^1]
        .Split("\u001b[J")[^1].Replace(TerminalDialog.ShowCursor, "", StringComparison.Ordinal).TrimEnd('\r');
}
