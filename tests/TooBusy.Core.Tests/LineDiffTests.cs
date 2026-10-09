using TooBusy.Core.Setup;

namespace TooBusy.Core.Tests;

public class LineDiffTests
{
    [Fact]
    public void ANewTextIsAllAdditions()
    {
        Assert.Equal([new ChangedLine(true, "a = 1"), new ChangedLine(true, ""), new ChangedLine(true, "b = 2")], LineDiff.Changes("", "a = 1\n\nb = 2\n"));
    }

    [Fact]
    public void TheSameTextHasNoChanges()
    {
        Assert.Empty(LineDiff.Changes("a = 1\nb = 2\n", "a = 1\nb = 2\n"));
    }

    [Fact]
    public void AChangedLineIsRemovedAndAdded()
    {
        Assert.Equal(
            [new ChangedLine(false, "b = 2"), new ChangedLine(true, "b = 3")],
            LineDiff.Changes("a = 1\nb = 2\nc = 3\n", "a = 1\nb = 3\nc = 3\n"));
    }

    [Fact]
    public void LinesThatComeAndGoAreToldApart()
    {
        Assert.Equal(
            [new ChangedLine(false, "a = 1"), new ChangedLine(true, "d = 4")],
            LineDiff.Changes("a = 1\nb = 2\nc = 3", "b = 2\r\nc = 3\r\nd = 4\r\n"));
    }
}
