using TooBusy.Core.Processes;
using TooBusy.Infrastructure.Git;

namespace TooBusy.Infrastructure.Tests;

public class GitOriginTests
{
    [Fact]
    public async Task TheOriginOnGitHubNamesTheRepository()
    {
        var processes = new ScriptedProcesses(new ProcessResult(ProcessStatus.Exited, 0, "git@github.com:bitpatch/toobusy.git\n", ""));

        Assert.Equal("bitpatch/toobusy", await new GitOrigin(processes).ReadAsync("/work/toobusy", TestContext.Current.CancellationToken));
        var (command, arguments, _, _, _) = Assert.Single(processes.Asked);
        Assert.Equal("git", command);
        Assert.Equal(["-C", "/work/toobusy", "remote", "get-url", "origin"], arguments);
    }

    [Theory]
    [InlineData(ProcessStatus.Exited, 0, "https://gitlab.com/bitpatch/toobusy.git\n")]
    [InlineData(ProcessStatus.Exited, 2, "")]
    [InlineData(ProcessStatus.NotFound, 0, "")]
    [InlineData(ProcessStatus.TimedOut, 0, "")]
    public async Task WithoutAnOriginOnGitHubThereIsNoRepository(ProcessStatus status, int exitCode, string output)
    {
        var processes = new ScriptedProcesses(new ProcessResult(status, exitCode, output, "error: No such remote 'origin'"));

        Assert.Null(await new GitOrigin(processes).ReadAsync("/work/toobusy", TestContext.Current.CancellationToken));
    }
}
