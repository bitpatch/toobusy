using TooBusy.Core.Processes;
using TooBusy.Core.Queue;

namespace TooBusy.Trackers.GitHub.Tests;

public class GitHubMilestonesTests
{
    readonly FakeProcesses processes = new();

    [Fact]
    public async Task TheLinesOfTheAnswerAreTheMilestones()
    {
        processes.Answers.Enqueue(FakeProcesses.Answered("v0.3.0\t2030-01-15T08:00:00Z\t12\nBacklog\t\t0\r\n\n"));

        var open = await ReadAsync();

        Assert.Equal([new Milestone("v0.3.0", new DateOnly(2030, 1, 15), 12), new Milestone("Backlog", null, 0)], open);
    }

    [Fact]
    public async Task TheToolIsAskedForTheOpenMilestonesOfTheRepository()
    {
        processes.Answers.Enqueue(FakeProcesses.Answered(""));

        Assert.Empty((await ReadAsync())!);

        var (command, arguments) = Assert.Single(processes.Asked);
        Assert.Equal("gh", command);
        Assert.Equal(["api", "repos/acme/rocket/milestones?state=open&per_page=100", "--jq"], arguments.Take(3));
        Assert.Equal(""".[] | "\(.title)\t\(.due_on // "")\t\(.open_issues)" """.Trim(), arguments[3]);
    }

    [Fact]
    public async Task ALineThatIsNotAMilestoneIsLeftOut()
    {
        processes.Answers.Enqueue(FakeProcesses.Answered("A new release of gh is available\nv1\tsoon\tmany\n"));

        Assert.Equal([new Milestone("v1", null, 0)], await ReadAsync());
    }

    [Theory]
    [InlineData(ProcessStatus.Exited, 1)]
    [InlineData(ProcessStatus.NotFound, 0)]
    [InlineData(ProcessStatus.TimedOut, 0)]
    public async Task WhenTheToolFailsTheMilestonesAreNotKnown(ProcessStatus status, int exitCode)
    {
        processes.Answers.Enqueue(new ProcessResult(status, exitCode, "v1\t\t1\n", "gh: Not Found"));

        Assert.Null(await ReadAsync());
    }

    Task<IReadOnlyList<Milestone>?> ReadAsync() => new GitHubMilestones(processes).ReadOpenAsync("acme/rocket", TestContext.Current.CancellationToken);
}
