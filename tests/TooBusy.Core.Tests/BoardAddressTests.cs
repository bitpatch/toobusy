using TooBusy.Core.Settings;

namespace TooBusy.Core.Tests;

public class BoardAddressTests
{
    [Fact]
    public void ABoardIsOfAnOrganisationOrOfAUserByItsNumber()
    {
        Assert.Equal(new BoardAddress(true, "bitpatch", 3), BoardAddress.Parse("https://github.com/orgs/bitpatch/projects/3"));
        Assert.Equal(new BoardAddress(false, "denis-kondratev", 12), BoardAddress.Parse("https://github.com/users/denis-kondratev/projects/12"));
    }

    // The settings, the setup and the tracker take the same addresses: a number with a sign, a space or a line break
    // around it, or one too large to be a number, is no board for any of them.
    [Theory]
    [InlineData("")]
    [InlineData("https://github.com/orgs/acme/projects/-3")]
    [InlineData("https://github.com/orgs/acme/projects/+3")]
    [InlineData("https://github.com/orgs/acme/projects/ 3")]
    [InlineData("https://github.com/orgs/acme/projects/3 ")]
    [InlineData("https://github.com/orgs/acme/projects/3\n")]
    [InlineData("https://github.com/orgs/acme/projects/99999999999")]
    [InlineData("https://github.com/orgs/acme/projects/3/")]
    [InlineData("https://github.com/orgs/acme/projects/3?pane=info")]
    [InlineData("http://github.com/orgs/acme/projects/3")]
    [InlineData("https://example.com/orgs/acme/projects/3")]
    [InlineData("https://github.com/teams/acme/projects/3")]
    [InlineData("https://github.com/acme/rocket")]
    public void AnythingElseIsNotABoard(string text)
    {
        Assert.Null(BoardAddress.Parse(text));
        Assert.False(SettingsValidator.IsBoard(text));
    }
}
