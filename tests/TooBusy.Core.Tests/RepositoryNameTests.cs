using TooBusy.Core.Setup;

namespace TooBusy.Core.Tests;

public class RepositoryNameTests
{
    [Theory]
    [InlineData("https://github.com/bitpatch/toobusy.git\n")]
    [InlineData("https://github.com/bitpatch/toobusy")]
    [InlineData("https://github.com/bitpatch/toobusy/")]
    [InlineData("HTTPS://GitHub.com/bitpatch/toobusy.git")]
    [InlineData("git@github.com:bitpatch/toobusy.git")]
    [InlineData("ssh://git@github.com/bitpatch/toobusy.git")]
    public void ARemoteOnGitHubNamesItsRepository(string address) => Assert.Equal("bitpatch/toobusy", RepositoryName.OfRemote(address));

    [Theory]
    [InlineData("")]
    [InlineData("https://gitlab.com/bitpatch/toobusy.git")]
    [InlineData("git@example.com:bitpatch/toobusy.git")]
    [InlineData("/srv/git/toobusy.git")]
    [InlineData("https://github.com/bitpatch")]
    [InlineData("https://github.com/bitpatch/toobusy/extra")]
    public void AnyOtherRemoteNamesNone(string address) => Assert.Null(RepositoryName.OfRemote(address));
}
