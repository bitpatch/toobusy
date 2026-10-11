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
        Assert.Equal(status, BoardStatuses.Of(name));

    [Fact]
    public void ABoardThatHasTheThreeLacksNothing()
    {
        Assert.Empty(BoardStatuses.Missing(["Backlog", "to do", "IN PROGRESS", "done", "Shipped"]));
        Assert.Null(BoardStatuses.Lack(["Backlog", "to do", "IN PROGRESS", "done", "Shipped"]));
    }

    [Fact]
    public void WhatABoardLacksIsSaidWithWhatItHas()
    {
        Assert.Equal(["Todo", "In Progress"], BoardStatuses.Missing(["Backlog", "Ready", "Done"]));
        Assert.Equal("The board has no status “In Progress”: it has Todo, Doing, Done.", BoardStatuses.Lack(["Todo", "Doing", "Done"]));
        Assert.Equal("The board has no statuses “Todo”, “In Progress”, “Done”: it has Backlog, Ready, Shipped.", BoardStatuses.Lack(["Backlog", "Ready", "Shipped"]));
        Assert.Equal("The board has no statuses “Todo”, “In Progress”, “Done”: it has no statuses.", BoardStatuses.Lack([]));
    }
}
