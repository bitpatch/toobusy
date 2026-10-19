using TooBusy.Cli.Terminal;

namespace TooBusy.Cli.Tests;

public sealed class PickerTests : IDisposable
{
    readonly TestTerminal terminal = new(width: 80, height: 20);

    public void Dispose() => terminal.Dispose();

    // Where Escape leaves the page a list has no `Back`: without choices it has nothing to point at, and says so.
    [Fact]
    public void AListWithoutChoicesOrBackSaysSoAndOnlyEscapeLeavesIt()
    {
        terminal.Keys.Press(Keys.Enter, Keys.Down, Keys.Letter('k'), Keys.Escape, Keys.Escape);
        using var page = terminal.Open();
        page.EscapeLeaves = true;

        Assert.Null(Picker.Pick(page, Picker.Head("Labels", ""), []));
        terminal.AssertSaw(" Labels\n There is nothing to choose from.\n");
    }

    // What is settled is left in the terminal as the page lists it: the mark in the colour of success, the label
    // muted, and the values in one column.
    [Fact]
    public void AnAnswerIsPrintedAsThePageListsIt()
    {
        var palette = Palette.Dark;

        Assert.Equal($"{palette.Success("✔ ")}{palette.Muted("Model            ")}opus", Picker.Answer("Model", "opus").Painted(palette));
        Assert.Equal("✔ Blocking labels  draft", Picker.Answer("Blocking labels", "draft").Painted(Palette.None));
    }

    // `k` and `j` with Ctrl or Alt are other keys, such as those of a line of text that shares a screen with a list.
    [Fact]
    public void KAndJWithCtrlOrAltDoNotMoveThePointer()
    {
        terminal.Keys.Press(Keys.Control('j'), new ConsoleKeyInfo('j', ConsoleKey.J, shift: false, alt: true, control: false), Keys.Control('k'), Keys.Enter);
        using var page = terminal.Open();

        Assert.Equal(0, Picker.Pick(page, Picker.Head("Rule", ""), [new Choice("lowest-version"), new Choice("earliest-due")]));
    }
}
