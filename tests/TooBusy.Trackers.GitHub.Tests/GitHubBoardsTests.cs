using TooBusy.Core.Processes;
using TooBusy.Core.Setup;

namespace TooBusy.Trackers.GitHub.Tests;

public class GitHubBoardsTests
{
    const string Toobusy = "https://github.com/orgs/bitpatch/projects/4";
    const string Fatgard = "https://github.com/users/denis/projects/3";

    readonly FakeProcesses processes = new();

    [Fact]
    public async Task TheLinesOfTheAnswerAreTheOwnersAndTheBoardsWithTheLinkedOnesFirst()
    {
        processes.Answers.Enqueue(Answered($"owner\tuser\tdenis\nowner\torg\tbitpatch\nboard\t{Fatgard}\tFatgard\nboard\t{Toobusy}\tToobusy\r\nlinked\t{Toobusy}\tToobusy\n\n"));

        var read = await ReadAsync("bitpatch/toobusy");

        Assert.Equal([new SetupBoard(Toobusy, "Toobusy", true), new SetupBoard(Fatgard, "Fatgard", false)], read.Boards);
        Assert.Equal([new SetupOwner("denis", false), new SetupOwner("bitpatch", true)], read.Owners);
    }

    [Fact]
    public async Task TheToolIsAskedOnceForItAll()
    {
        processes.Answers.Enqueue(Answered(""));

        await ReadAsync("bitpatch/toobusy");

        var (command, arguments) = Assert.Single(processes.Asked);
        Assert.Equal("gh", command);
        Assert.Equal(["api", "graphql", "-f", "owner=bitpatch", "-f", "name=toobusy", "-f"], arguments.Take(7));
        Assert.StartsWith("query=query($owner: String!, $name: String!) { viewer { login projectsV2(", arguments[7], StringComparison.Ordinal);
        Assert.Contains("organizations(", arguments[7], StringComparison.Ordinal);
        Assert.Contains("repository(owner: $owner, name: $name)", arguments[7], StringComparison.Ordinal);
        Assert.Equal("--jq", arguments[8]);
        Assert.StartsWith(".data | (.viewer | ", arguments[9], StringComparison.Ordinal);
    }

    [Fact]
    public async Task WithoutARepositoryOnlyTheBoardsOfTheUserAreAskedFor()
    {
        processes.Answers.Enqueue(Answered($"owner\tuser\tdenis\nboard\t{Fatgard}\tFatgard\n"));

        var read = await ReadAsync(null);

        Assert.Equal([new SetupBoard(Fatgard, "Fatgard", false)], read.Boards);
        Assert.StartsWith("query={ viewer {", Assert.Single(processes.Asked).Arguments[3], StringComparison.Ordinal);
    }

    [Fact]
    public async Task ARepositoryThatCannotBeReadDoesNotHideTheBoardsOfTheUser()
    {
        processes.Answers.Enqueue(new ProcessResult(ProcessStatus.Exited, 1, "{\"data\":{\"repository\":null}}", "gh: Could not resolve to a Repository"));
        processes.Answers.Enqueue(Answered($"owner\tuser\tdenis\nboard\t{Fatgard}\tFatgard\n"));

        var read = await ReadAsync("example/project");

        Assert.Equal([new SetupBoard(Fatgard, "Fatgard", false)], read.Boards);
        Assert.Equal(2, processes.Asked.Count);
    }

    [Fact]
    public async Task ATitleMayHaveTabsAndALineThatIsNoneOfTheKindsIsLeftOut()
    {
        processes.Answers.Enqueue(Answered($"A new release of gh is available\nboard\tnot-an-address\tBroken\nowner\trobot\thal\nboard\t{Fatgard}\tFat\tgard\n"));

        var read = await ReadAsync(null);

        Assert.Equal([new SetupBoard(Fatgard, "Fat\tgard", false)], read.Boards);
        Assert.Empty(read.Owners);
    }

    [Theory]
    [InlineData(ProcessStatus.Exited, 1)]
    [InlineData(ProcessStatus.NotFound, 0)]
    [InlineData(ProcessStatus.TimedOut, 0)]
    public async Task WhenTheToolFailsThereAreNoBoards(ProcessStatus status, int exitCode)
    {
        processes.Answers.Enqueue(new ProcessResult(status, exitCode, $"board\t{Fatgard}\tFatgard\n", "gh: your token lacks the read:project scope"));

        Assert.Equal(new SetupBoards([], []), await ReadAsync(null), (left, right) => left!.Boards.Count == right!.Boards.Count && left.Owners.Count == right.Owners.Count);
    }

    Task<SetupBoards> ReadAsync(string? repository) => new GitHubBoards(processes).ReadAsync(repository, TestContext.Current.CancellationToken);

    static ProcessResult Answered(string output) => new(ProcessStatus.Exited, 0, output, "");

    // Gives the answers it is told to, one for each command, and remembers what it was asked to run.
    sealed class FakeProcesses : IProcessRunner
    {
        public Queue<ProcessResult> Answers { get; } = new();

        public List<(string Command, IReadOnlyList<string> Arguments)> Asked { get; } = [];

        public Task<ProcessResult> RunAsync(string command, IReadOnlyList<string> arguments, TimeSpan timeout, CancellationToken cancellationToken)
        {
            Asked.Add((command, arguments));
            return Task.FromResult(Answers.Count > 0 ? Answers.Dequeue() : new ProcessResult(ProcessStatus.NotFound, 0, "", ""));
        }
    }
}
