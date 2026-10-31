using TooBusy.Core.Processes;

namespace TooBusy.Assistants.ClaudeCode.Tests;

public class ClaudeLoginTests
{
    [Theory]
    [InlineData(ProcessStatus.Exited, 0, """{ "loggedIn": true, "authMethod": "claude.ai" }""", true)]
    [InlineData(ProcessStatus.Exited, 1, """{ "loggedIn": false, "authMethod": "none" }""", false)]
    [InlineData(ProcessStatus.Exited, 0, """{ "loggedIn": false }""", false)]
    [InlineData(ProcessStatus.Exited, 0, "Logged in as ann", true)]
    [InlineData(ProcessStatus.Exited, 1, "error: unknown command 'auth'", false)]
    [InlineData(ProcessStatus.TimedOut, 0, "", false)]
    [InlineData(ProcessStatus.NotFound, 0, "", false)]
    public async Task ClaudeCodeIsLoggedInWhenItsStatusSaysSo(ProcessStatus status, int exitCode, string answer, bool expected)
    {
        var claude = new FakeClaude { Answer = _ => new ProcessResult(status, exitCode, answer, "") };

        Assert.Equal(expected, await new ClaudeLogin(claude).CheckAsync(TestContext.Current.CancellationToken));
        Assert.Equal(["auth", "status"], Assert.Single(claude.Asked));
    }
}
