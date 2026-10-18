namespace TooBusy.Assistants.ClaudeCode.Tests;

public sealed class ClaudeCodeProtocolTests
{
    [Theory]
    [InlineData("2.1.303 (Claude Code)", false)]
    [InlineData("2.1.304 (Claude Code)", true)]
    [InlineData("2.2.0 (Claude Code)", true)]
    [InlineData("3.0 (Claude Code)", true)]
    [InlineData("2.1.99 (Claude Code)", false)]
    [InlineData("1.0.128 (Claude Code)", false)]
    [InlineData("Claude Code", false)]
    [InlineData("", false)]
    public void AVersionIsNewerThanTheOneOfTheProtocolByItsNumbers(string said, bool newer)
    {
        Assert.Equal("2.1.303", ClaudeCodeProtocol.Version);
        Assert.Equal(newer, ClaudeCodeProtocol.Newer(said));
    }
}
