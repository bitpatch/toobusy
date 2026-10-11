using TooBusy.Core.Queue;

namespace TooBusy.Core.Tests;

public class BoardStatusesTests
{
    [Theory]
    [InlineData("Todo", BoardStatus.Todo)]
    [InlineData("To do", BoardStatus.Todo)]
    [InlineData("In Progress", BoardStatus.InProgress)]
    [InlineData("in-progress", BoardStatus.InProgress)]
    [InlineData("🚧 IN PROGRESS", BoardStatus.InProgress)]
    [InlineData("Done", BoardStatus.Done)]
    [InlineData("Backlog", BoardStatus.Other)]
    [InlineData("", BoardStatus.Missing)]
    public void AStatusIsKnownByItsLettersWhateverTheirCase(string name, BoardStatus status) =>
        Assert.Equal(status, BoardStatuses.Default.Of(name));

    [Fact]
    public void ABoardThatHasTheThreeLacksNothing()
    {
        Assert.Empty(BoardStatuses.Default.Missing(["Backlog", "to do", "IN PROGRESS", "done", "Shipped"]));
        Assert.Null(BoardStatuses.Default.Lack(["Backlog", "to do", "IN PROGRESS", "done", "Shipped"]));
    }

    [Fact]
    public void WhatABoardLacksIsSaidWithWhatItHas()
    {
        Assert.Equal(["Todo", "In Progress"], BoardStatuses.Default.Missing(["Backlog", "Ready", "Done"]));
        Assert.Equal("The board has no status “In Progress”: it has Todo, Doing, Done.", BoardStatuses.Default.Lack(["Todo", "Doing", "Done"]));
        Assert.Equal("The board has no statuses “Todo”, “In Progress”, “Done”: it has Backlog, Ready, Shipped.", BoardStatuses.Default.Lack(["Backlog", "Ready", "Shipped"]));
        Assert.Equal("The board has no statuses “Todo”, “In Progress”, “Done”: it has no statuses.", BoardStatuses.Default.Lack([]));
    }

    static readonly BoardStatuses Named = new("Ready", "Doing", "Stage 2");

    [Theory]
    [InlineData("ready", BoardStatus.Todo)]
    [InlineData("DOING", BoardStatus.InProgress)]
    [InlineData("stage-2", BoardStatus.Done)]
    [InlineData("Stage 1", BoardStatus.Other)]
    [InlineData("Todo", BoardStatus.Other)]
    [InlineData("Done", BoardStatus.Other)]
    [InlineData("", BoardStatus.Missing)]
    public void AStatusIsKnownByTheNameOfTheSettings(string name, BoardStatus status) =>
        Assert.Equal(status, Named.Of(name));

    [Fact]
    public void AStatusIsNamedAsTheSettingsNameIt()
    {
        Assert.Equal("Ready", Named.NameOf(BoardStatus.Todo));
        Assert.Equal("Doing", Named.NameOf(BoardStatus.InProgress));
        Assert.Equal("Stage 2", Named.NameOf(BoardStatus.Done));
        Assert.Equal(["Ready", "Doing", "Stage 2"], Named.Names);
    }

    [Fact]
    public void WhatABoardLacksGoesByTheNamesOfTheSettings()
    {
        Assert.Null(Named.Lack(["Backlog", "READY", "doing", "Stage 2"]));
        Assert.Equal("The board has no statuses “Ready”, “Stage 2”: it has Todo, Doing, Done.", Named.Lack(["Todo", "Doing", "Done"]));
    }

    [Fact]
    public void ANameWithoutLettersAndDigitsIsToldByAllOfIt()
    {
        var signs = new BoardStatuses("📥", "🚧", "✅");

        Assert.Equal(BoardStatus.InProgress, signs.Of("🚧"));
        Assert.Equal(BoardStatus.Other, signs.Of("🚀"));
        Assert.False(BoardStatuses.Same("📥", "✅"));
    }
}
