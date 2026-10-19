using TooBusy.Core.Processes;
using TooBusy.Core.Queue;

namespace TooBusy.Trackers.GitHub.Tests;

public class GitHubMilestonesTests
{
    readonly ScriptedProcesses processes = new();

    [Fact]
    public async Task TheLinesOfTheAnswerAreTheMilestones()
    {
        processes.Answers.Enqueue(ScriptedProcesses.Answered("3\tv0.3.0\t2030-01-15T08:00:00Z\t12\n1\tBacklog\t\t0\r\n\n"));

        var open = await ReadAsync();

        Assert.Equal([new Milestone("v0.3.0", new DateOnly(2030, 1, 15), 12), new Milestone("Backlog", null, 0)], open);
    }

    [Fact]
    public async Task TheToolIsAskedForTheOpenMilestonesOfTheRepository()
    {
        processes.Answers.Enqueue(ScriptedProcesses.Answered(""));

        Assert.Empty((await ReadAsync())!);

        var (command, arguments, _) = Assert.Single(processes.Asked);
        Assert.Equal("gh", command);
        Assert.Equal(["api", "repos/acme/rocket/milestones?state=open&per_page=100", "--jq"], arguments.Take(3));
        Assert.Equal(""".[] | [.number, .title, .due_on // "", .open_issues] | @tsv""", arguments[3]);
    }

    [Fact]
    public async Task ALineThatIsNotAMilestoneIsLeftOut()
    {
        processes.Answers.Enqueue(ScriptedProcesses.Answered("A new release of gh is available\nv0\t\t\t1\n2\tv1\tsoon\tmany\n3\t\t\t1\n"));

        Assert.Equal([new Milestone("v1", null, 0)], await ReadAsync());
    }

    // The tool writes a tab, a line break and a backslash of a title as two characters each, so a title with them is
    // one field of its line, read as it is written on GitHub.
    [Fact]
    public async Task ATitleIsReadWithTheCharactersTheToolEscapes()
    {
        processes.Answers.Enqueue(ScriptedProcesses.Answered("5\tv0.2.0\\tnext \\\\ later\t\t1\n"));

        Assert.Equal([new Milestone("v0.2.0\tnext \\ later", null, 1)], await ReadAsync());
    }

    [Theory]
    [InlineData(ProcessStatus.Exited, 1)]
    [InlineData(ProcessStatus.NotFound, 0)]
    [InlineData(ProcessStatus.TimedOut, 0)]
    public async Task WhenTheToolFailsTheMilestonesAreNotKnown(ProcessStatus status, int exitCode)
    {
        processes.Answers.Enqueue(new ProcessResult(status, exitCode, "1\tv1\t\t1\n", "gh: Not Found"));

        Assert.Null(await ReadAsync());
    }

    Task<IReadOnlyList<Milestone>?> ReadAsync() => new GitHubMilestones(processes).ReadOpenAsync("acme/rocket", TestContext.Current.CancellationToken);
}
