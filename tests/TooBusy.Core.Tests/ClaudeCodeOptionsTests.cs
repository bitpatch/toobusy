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

    [Theory]
    [InlineData("auto", "auto")]
    [InlineData("DontAsk", "dontAsk")]
    [InlineData(" dontask ", "dontAsk")]
    public void APermissionModeIsFoundWhateverTheCaseOfItsLetters(string text, string mode)
    {
        Assert.Equal(mode, ClaudeCodeOptions.FindPermissionMode(text));
    }

    // Claude Code has these modes too, and a session cannot be started in any of them.
    [Theory]
    [InlineData("")]
    [InlineData("plan")]
    [InlineData("manual")]
    [InlineData("acceptEdits")]
    [InlineData("bypassPermissions")]
    public void AModeASessionCannotBeStartedInIsNotFound(string text)
    {
        Assert.Null(ClaudeCodeOptions.FindPermissionMode(text));
        Assert.NotNull(ClaudeCodeOptions.RefusePermissionMode(text));
    }

    [Fact]
    public void TheModeOfASessionThatNamesNoneIsOneOfTheModes()
    {
        Assert.Contains(ClaudeCodeOptions.DefaultPermissionMode, ClaudeCodeOptions.PermissionModes);
        Assert.All(ClaudeCodeOptions.PermissionModes, mode => Assert.Null(ClaudeCodeOptions.RefusePermissionMode(mode)));
    }

    [Fact]
    public void TheProposedEffortIsOneOfTheLevels()
    {
        Assert.Contains(ClaudeCodeOptions.ProposedEffort, ClaudeCodeOptions.Efforts);
    }
}
