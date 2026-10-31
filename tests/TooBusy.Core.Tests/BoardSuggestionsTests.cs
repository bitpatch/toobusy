using TooBusy.Core.Setup;

namespace TooBusy.Core.Tests;

public class BoardSuggestionsTests
{
    static readonly SetupBoard Toobusy = new("https://github.com/orgs/bitpatch/projects/4", "Toobusy", true);
    static readonly SetupBoard Fatgard = new("https://github.com/users/denis/projects/3", "Fatgard", false);
    static readonly SetupBoard Dialog = new("https://github.com/orgs/gamedialog/projects/1", "Game Dialog, not too busy", false);
    static readonly SetupBoard[] Known = [Fatgard, Dialog, Toobusy];

    [Theory]
    [InlineData("https://github.com/orgs/bitpatch/projects/4")]
    [InlineData("  https://github.com/orgs/bitpatch/projects/4/ ")]
    [InlineData("https://github.com/orgs/bitpatch/projects/4/views/2")]
    [InlineData("https://github.com/orgs/bitpatch/projects/4/views/2?layout=board&pane=issue#top")]
    [InlineData("https://github.com/orgs/bitpatch/projects/4?query=is%3Aopen")]
    public void TheAddressOfAnyPageOfAProjectIsTheAddressOfItsBoard(string answer) =>
        Assert.Equal("https://github.com/orgs/bitpatch/projects/4", BoardSuggestions.AddressOf(answer));

    [Theory]
    [InlineData("", "")]
    [InlineData(" toobusy ", "toobusy")]
    [InlineData("https://github.com/orgs/bitpatch/projects/", "https://github.com/orgs/bitpatch/projects")]
    [InlineData("https://github.com/orgs/bitpatch/projects/four/views/1", "https://github.com/orgs/bitpatch/projects/four/views/1")]
    public void WhatIsNotSuchAnAddressStaysAsItIs(string answer, string expected) => Assert.Equal(expected, BoardSuggestions.AddressOf(answer));

    [Fact]
    public void NothingTypedGivesThemAll() => Assert.Equal(Known, BoardSuggestions.Matching(Known, "  "));

    [Fact]
    public void ATitleThatStartsWithTheTextComesBeforeOneThatHasItInside() =>
        Assert.Equal([Toobusy, Dialog], BoardSuggestions.Matching(Known, "TOO"));

    [Fact]
    public void TheOwnerInTheAddressFitsToo()
    {
        Assert.Equal([Fatgard], BoardSuggestions.Matching(Known, "denis"));
        Assert.Equal([Dialog, Toobusy], BoardSuggestions.Matching(Known, "orgs/"));
    }

    [Fact]
    public void APastedAddressFitsItsBoardFirst() =>
        Assert.Equal(Toobusy, BoardSuggestions.Matching(Known, "https://github.com/orgs/bitpatch/projects/4/views/1")[0]);

    [Fact]
    public void WhatFitsNothingGivesNothing() => Assert.Empty(BoardSuggestions.Matching(Known, "rocket"));
}
