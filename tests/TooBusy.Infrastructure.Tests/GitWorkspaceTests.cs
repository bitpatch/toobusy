using TooBusy.Core.Processes;
using TooBusy.Core.Run;
using TooBusy.Infrastructure.Git;

namespace TooBusy.Infrastructure.Tests;

public class GitWorkspaceTests
{
    [Fact]
    public async Task ACleanTreeThatIsPushedHasNothingToTellOf()
    {
        var processes = new OneAnswer(Exited("# branch.oid 1a2b3c\n# branch.head develop\n# branch.upstream origin/develop\n# branch.ab +0 -0\n"));

        var state = await new GitWorkspace(processes, "/work/rocket").ReadAsync(TestContext.Current.CancellationToken);

        Assert.Equal(new WorkspaceState(0, 0), state);
        Assert.Equal("git", processes.Command);
        Assert.Equal(["-C", "/work/rocket", "status", "--porcelain=v2", "--branch"], processes.Arguments);
    }

    [Fact]
    public async Task ChangedAndUnknownFilesAreChangesAndCommitsAheadAreNotPushed()
    {
        var processes = new OneAnswer(Exited(
            "# branch.oid 1a2b3c\r\n# branch.head develop\r\n# branch.upstream origin/develop\r\n# branch.ab +3 -1\r\n"
            + "1 .M N... 100644 100644 100644 1a2b 3c4d src/a.cs\r\n? notes.txt\r\n"));

        Assert.Equal(new WorkspaceState(2, 3), await new GitWorkspace(processes, "/work/rocket").ReadAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task ABranchWithoutAnUpstreamHasNothingThatIsNotPushed()
    {
        var processes = new OneAnswer(Exited("# branch.oid 1a2b3c\n# branch.head scratch\n"));

        Assert.Equal(new WorkspaceState(0, 0), await new GitWorkspace(processes, "/work/rocket").ReadAsync(TestContext.Current.CancellationToken));
    }

    [Theory]
    [InlineData(ProcessStatus.Exited, 128)]
    [InlineData(ProcessStatus.NotFound, 0)]
    [InlineData(ProcessStatus.TimedOut, 0)]
    public async Task ATreeThatGitCannotTellOfIsNotRead(ProcessStatus status, int exitCode)
    {
        var processes = new OneAnswer(new ProcessResult(status, exitCode, "", "fatal: not a git repository"));

        Assert.Null(await new GitWorkspace(processes, "/work/rocket").ReadAsync(TestContext.Current.CancellationToken));
    }

    static ProcessResult Exited(string output) => new(ProcessStatus.Exited, 0, output, "");

    sealed class OneAnswer(ProcessResult result) : IProcessRunner
    {
        public string? Command { get; private set; }

        public IReadOnlyList<string> Arguments { get; private set; } = [];

        public Task<ProcessResult> RunAsync(string command, IReadOnlyList<string> arguments, TimeSpan timeout, CancellationToken cancellationToken)
        {
            (Command, Arguments) = (command, arguments);
            return Task.FromResult(result);
        }

        public IProcessRunner Inside(string folder) => this;
    }
}
