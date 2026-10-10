using TooBusy.Core.Processes;
using TooBusy.Infrastructure.Git;

namespace TooBusy.Infrastructure.Tests;

public class GitOriginTests
{
    [Fact]
    public async Task TheOriginOnGitHubNamesTheRepository()
    {
        var processes = new FakeProcesses(new ProcessResult(ProcessStatus.Exited, 0, "git@github.com:bitpatch/toobusy.git\n", ""));

        Assert.Equal("bitpatch/toobusy", await new GitOrigin(processes).ReadAsync("/work/toobusy", TestContext.Current.CancellationToken));
        Assert.Equal("git", processes.Command);
        Assert.Equal(["-C", "/work/toobusy", "remote", "get-url", "origin"], processes.Arguments);
    }

    [Theory]
    [InlineData(ProcessStatus.Exited, 0, "https://gitlab.com/bitpatch/toobusy.git\n")]
    [InlineData(ProcessStatus.Exited, 2, "")]
    [InlineData(ProcessStatus.NotFound, 0, "")]
    [InlineData(ProcessStatus.TimedOut, 0, "")]
    public async Task WithoutAnOriginOnGitHubThereIsNoRepository(ProcessStatus status, int exitCode, string output)
    {
        var processes = new FakeProcesses(new ProcessResult(status, exitCode, output, "error: No such remote 'origin'"));

        Assert.Null(await new GitOrigin(processes).ReadAsync("/work/toobusy", TestContext.Current.CancellationToken));
    }

    // Gives the result it is told to and remembers what it was asked to run.
    sealed class FakeProcesses(ProcessResult result) : IProcessRunner
    {
        public string? Command { get; private set; }

        public IReadOnlyList<string> Arguments { get; private set; } = [];

        public Task<ProcessResult> RunAsync(string command, IReadOnlyList<string> arguments, TimeSpan timeout, CancellationToken cancellationToken)
        {
            Command = command;
            Arguments = arguments;
            return Task.FromResult(result);
        }
    }
}
