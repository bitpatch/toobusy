using TooBusy.Core.Assistant;

namespace TooBusy.Core.Tests;

public sealed class ClaudeCodeOptionsTests
{
    [Theory]
    [InlineData("high", "high")]
    [InlineData("XHigh", "xhigh")]
    [InlineData(" max ", "max")]
    public void ALevelIsFoundWhateverTheCaseOfItsLetters(string text, string level)
    {
        Assert.Equal(level, ClaudeCodeOptions.FindEffort(text));
    }

    [Theory]
    [InlineData("")]
    [InlineData("ultra")]
    [InlineData("hig")]
    public void WhatIsNotALevelIsNotFound(string text)
    {
        Assert.Null(ClaudeCodeOptions.FindEffort(text));
    }

    [Fact]
    public void TheProposedEffortIsOneOfTheLevels()
    {
        Assert.Contains(ClaudeCodeOptions.ProposedEffort, ClaudeCodeOptions.Efforts);
    }
}
