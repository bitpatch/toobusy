using TooBusy.Cli.Terminal;

namespace TooBusy.Cli.Tests;

public class LineEditorTests
{
    [Fact]
    public void TypingStartsAtTheEndOfTheText()
    {
        var editor = new LineEditor("acme");
        editor.Insert('/');

        Assert.Equal(("acme/", 5, true), (editor.Text, editor.Caret, editor.AtEnd));
    }

    [Fact]
    public void ACharacterIsInsertedAtTheCaret()
    {
        var editor = new LineEditor("ame");
        editor.Home();
        editor.Right();
        editor.Insert('c');

        Assert.Equal(("acme", 2, false), (editor.Text, editor.Caret, editor.AtEnd));
    }

    [Fact]
    public void BackspaceRemovesWhatIsBeforeTheCaretAndDeleteWhatIsUnderIt()
    {
        var editor = new LineEditor("acme");
        editor.Left();
        editor.Backspace();
        Assert.Equal(("ace", 2), (editor.Text, editor.Caret));

        editor.Delete();
        Assert.Equal(("ac", 2), (editor.Text, editor.Caret));
    }

    [Fact]
    public void AtTheEdgesThereIsNothingToRemoveOrToMoveTo()
    {
        var editor = new LineEditor("a");
        editor.Delete();
        editor.Right();
        editor.Home();
        editor.Backspace();
        editor.Left();

        Assert.Equal(("a", 0), (editor.Text, editor.Caret));
    }

    [Fact]
    public void TheTextBeforeTheCaretIsCleared()
    {
        var editor = new LineEditor("acme/rocket");
        editor.Left();
        editor.ClearToStart();

        Assert.Equal(("t", 0), (editor.Text, editor.Caret));
    }

    [Fact]
    public void TheWordBeforeTheCaretIsRemovedWithTheSpacesAfterIt()
    {
        var editor = new LineEditor("bug, good first  ");
        editor.DeleteWordBack();
        Assert.Equal("bug, good ", editor.Text);

        editor.Home();
        editor.DeleteWordBack();
        Assert.Equal("bug, good ", editor.Text);
    }

    [Fact]
    public void ATextThatFitsIsShownWhole() => Assert.Equal(("acme", 4), new LineEditor("acme").View(5));

    [Fact]
    public void ALongTextIsShownAroundTheCaret()
    {
        var editor = new LineEditor("0123456789");
        Assert.Equal(("…6789", 5), editor.View(6));

        editor.Home();
        Assert.Equal(("0123…", 0), editor.View(6));

        editor.Right();
        editor.Right();
        editor.Right();
        editor.Right();
        editor.Right();
        Assert.Equal(("…345…", 3), editor.View(6));
    }
}
