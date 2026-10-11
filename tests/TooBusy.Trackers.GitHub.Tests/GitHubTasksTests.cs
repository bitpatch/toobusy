using TooBusy.Core.Processes;
using TooBusy.Core.Queue;

namespace TooBusy.Trackers.GitHub.Tests;

public class GitHubTasksTests
{
    const string Board = "https://github.com/orgs/Acme/projects/7";

    readonly FakeProcesses processes = new();

    [Fact]
    public async Task TheTasksOfAMilestoneAreAskedForByItsNumber()
    {
        processes.Answers.Enqueue(FakeProcesses.Answered("1\tv0.1.0\n4\tv0.2.0\n"));
        processes.Answers.Enqueue(FakeProcesses.Answered("total\t0\n"));

        Assert.Empty(await Tasks().ReadOpenAsync("v0.2.0", TestContext.Current.CancellationToken));

        Assert.Equal(["api", "repos/acme/rocket/milestones?state=open&per_page=100", "--jq", """.[] | "\(.number)\t\(.title)" """.Trim()], processes.Asked[0].Arguments);
        var arguments = processes.Asked[1].Arguments;
        Assert.Equal(["api", "graphql", "-f", "owner=acme", "-f", "name=rocket", "-f", "milestone=4", "-f"], arguments.Take(9));
        Assert.StartsWith("query=query($owner: String!, $name: String!, $milestone: String) { repository(owner: $owner, name: $name) { issues(states: OPEN, first: 100,", arguments[9], StringComparison.Ordinal);
        Assert.Contains("filterBy: {milestoneNumber: $milestone}", arguments[9], StringComparison.Ordinal);
        Assert.Equal("--jq", arguments[10]);
    }

    [Fact]
    public async Task WithoutAMilestoneEveryOpenTaskIsAskedFor()
    {
        processes.Answers.Enqueue(FakeProcesses.Answered("total\t0\n"));

        await Tasks().ReadOpenAsync(null, TestContext.Current.CancellationToken);

        Assert.Equal(["api", "graphql", "-f", "owner=acme", "-f", "name=rocket", "-f"], Assert.Single(processes.Asked).Arguments.Take(7));
    }

    [Fact]
    public async Task AMilestoneThatIsNotOpenAnyMoreIsSaid()
    {
        processes.Answers.Enqueue(FakeProcesses.Answered("1\tv0.1.0\n"));

        var gone = await Assert.ThrowsAsync<TrackerException>(() => Tasks().ReadOpenAsync("v0.2.0", TestContext.Current.CancellationToken));

        Assert.Equal("The milestone “v0.2.0” is not open any more.", gone.Message);
    }

    [Fact]
    public async Task ATaskIsALineOfTheAnswer()
    {
        processes.Answers.Enqueue(FakeProcesses.Answered(
            "total\t3\n"
            + "12\tAdd\\tthe \\\\ thing\thttps://github.com/acme/rocket/issues/12\tIn progress\t4,9\t13\tfeature\tgood first issue\n"
            + "13\tA part\thttps://github.com/acme/rocket/issues/13\tTodo\t\t\r\n"
            + "14\tNot there\thttps://github.com/acme/rocket/issues/14\t\t\t\tbug\n"));

        var tasks = await Tasks().ReadOpenAsync(null, TestContext.Current.CancellationToken);

        Assert.Equal(3, tasks.Count);
        Assert.Equal((12, "Add\tthe \\ thing", "https://github.com/acme/rocket/issues/12"), (tasks[0].Number, tasks[0].Title, tasks[0].Url));
        Assert.Equal(["feature", "good first issue"], tasks[0].Labels);
        Assert.Equal((BoardStatus.InProgress, "In progress"), (tasks[0].Status, tasks[0].StatusName));
        Assert.Equal([4, 9], tasks[0].BlockedBy);
        Assert.Equal([13], tasks[0].Parts);
        Assert.Equal(BoardStatus.Todo, tasks[1].Status);
        Assert.Empty(tasks[1].Labels);
        Assert.Equal(BoardStatus.Missing, tasks[2].Status);
    }

    [Theory]
    [InlineData("Todo", BoardStatus.Todo)]
    [InlineData("to do", BoardStatus.Todo)]
    [InlineData("In Progress", BoardStatus.InProgress)]
    [InlineData("Done", BoardStatus.Done)]
    [InlineData("No status", BoardStatus.Other)]
    [InlineData("Review", BoardStatus.Other)]
    public async Task AStatusIsKnownByWhatTheBoardCallsIt(string name, BoardStatus status)
    {
        processes.Answers.Enqueue(FakeProcesses.Answered($"total\t1\n1\tOne\thttps://github.com/acme/rocket/issues/1\t{name}\t\t\n"));

        Assert.Equal(status, Assert.Single(await Tasks().ReadOpenAsync(null, TestContext.Current.CancellationToken)).Status);
    }

    [Fact]
    public async Task TheStatusIsThatOfTheBoardOfTheProject()
    {
        processes.Answers.Enqueue(FakeProcesses.Answered("total\t0\n"));
        processes.Answers.Enqueue(FakeProcesses.Answered("total\t0\n"));

        await Tasks().ReadOpenAsync(null, TestContext.Current.CancellationToken);
        await Tasks(board: null).ReadOpenAsync(null, TestContext.Current.CancellationToken);

        Assert.Contains("select((.project.url | ascii_downcase) == \"https://github.com/orgs/acme/projects/7\")", processes.Asked[0].Arguments[^1], StringComparison.Ordinal);
        Assert.DoesNotContain("projectItems", processes.Asked[1].Arguments[^1], StringComparison.Ordinal);
    }

    [Fact]
    public async Task MoreOpenTasksThanTheQueueReadsAreAnError()
    {
        processes.Answers.Enqueue(FakeProcesses.Answered("1\tv0.2.0\n"));
        processes.Answers.Enqueue(FakeProcesses.Answered("total\t140\n"));
        processes.Answers.Enqueue(FakeProcesses.Answered("total\t101\n"));

        var milestone = await Assert.ThrowsAsync<TrackerException>(() => Tasks().ReadOpenAsync("v0.2.0", TestContext.Current.CancellationToken));
        var whole = await Assert.ThrowsAsync<TrackerException>(() => Tasks().ReadOpenAsync(null, TestContext.Current.CancellationToken));

        Assert.Equal("The milestone “v0.2.0” has 140 open tasks, and the queue reads only the first 100.", milestone.Message);
        Assert.Equal("acme/rocket has 101 open tasks, and the queue reads only the first 100.", whole.Message);
    }

    [Fact]
    public async Task TasksThatCannotBeReadSayWhy()
    {
        processes.Answers.Enqueue(FakeProcesses.Failed("gh: Not Found (HTTP 404)\nmore"));
        processes.Answers.Enqueue(new ProcessResult(ProcessStatus.TimedOut, 0, "", ""));
        processes.Answers.Enqueue(new ProcessResult(ProcessStatus.NotFound, 0, "", ""));

        var refused = await Assert.ThrowsAsync<TrackerException>(() => Tasks().ReadOpenAsync(null, TestContext.Current.CancellationToken));
        var silent = await Assert.ThrowsAsync<TrackerException>(() => Tasks().ReadOpenAsync(null, TestContext.Current.CancellationToken));
        var missing = await Assert.ThrowsAsync<TrackerException>(() => Tasks().ReadOpenAsync(null, TestContext.Current.CancellationToken));

        Assert.Equal("The tasks of acme/rocket cannot be read. gh: Not Found (HTTP 404)", refused.Message);
        Assert.Equal("The tasks of acme/rocket cannot be read. GitHub did not answer.", silent.Message);
        Assert.Equal("The tasks of acme/rocket cannot be read. The GitHub command-line tool `gh` is not installed.", missing.Message);
    }

    [Fact]
    public async Task TheDescriptionOfATaskIsReadWithItsLines()
    {
        processes.Answers.Enqueue(FakeProcesses.Answered("Do it.\\n\\n- one\\n- two\n"));

        var description = await Tasks().ReadDescriptionAsync(12, TestContext.Current.CancellationToken);

        Assert.Equal("Do it.\n\n- one\n- two", description);
        Assert.Equal(["issue", "view", "12", "--repo", "acme/rocket", "--json", "body", "--jq", "[.body] | @tsv"], Assert.Single(processes.Asked).Arguments);
    }

    [Fact]
    public async Task ATaskWithoutADescriptionHasAnEmptyOne()
    {
        processes.Answers.Enqueue(FakeProcesses.Answered("\n"));

        Assert.Equal("", await Tasks().ReadDescriptionAsync(12, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task ATaskIsMovedOnTheBoardByTheNameOfTheStatus()
    {
        processes.Answers.Enqueue(FakeProcesses.Answered("issue\tI_1\nproject\tPVT_1\nfield\tF_1\noption\tO_1\tTodo\noption\tO_2\tIn progress\nitem\tPVTI_9\tPVT_other\nitem\tPVTI_1\tPVT_1\n"));
        processes.Answers.Enqueue(FakeProcesses.Answered("PVTI_1\n"));

        await Tasks().SetStatusAsync(12, BoardStatus.InProgress, TestContext.Current.CancellationToken);

        Assert.Equal(2, processes.Asked.Count);
        var read = processes.Asked[0].Arguments;
        Assert.Equal(["api", "graphql", "-f", "owner=acme", "-f", "name=rocket", "-F", "number=12", "-f", "login=Acme", "-F", "project=7", "-f"], read.Take(13));
        Assert.Contains("organization(login: $login) { projectV2(number: $project)", read[13], StringComparison.Ordinal);
        var moved = processes.Asked[1].Arguments;
        Assert.Equal(["api", "graphql", "-f", "project=PVT_1", "-f", "item=PVTI_1", "-f", "field=F_1", "-f", "option=O_2", "-f"], moved.Take(11));
        Assert.Contains("updateProjectV2ItemFieldValue", moved[11], StringComparison.Ordinal);
    }

    [Fact]
    public async Task ATaskThatIsNotOnTheBoardIsPutThereFirst()
    {
        processes.Answers.Enqueue(FakeProcesses.Answered("issue\tI_1\nproject\tPVT_1\nfield\tF_1\noption\tO_1\tTodo\n"));
        processes.Answers.Enqueue(FakeProcesses.Answered("PVTI_5\n"));
        processes.Answers.Enqueue(FakeProcesses.Answered("PVTI_5\n"));

        await Tasks().SetStatusAsync(12, BoardStatus.Todo, TestContext.Current.CancellationToken);

        Assert.Equal(["api", "graphql", "-f", "project=PVT_1", "-f", "content=I_1", "-f"], processes.Asked[1].Arguments.Take(7));
        Assert.Contains("addProjectV2ItemById", processes.Asked[1].Arguments[7], StringComparison.Ordinal);
        Assert.Contains("item=PVTI_5", processes.Asked[2].Arguments);
    }

    [Fact]
    public async Task ABoardWithoutTheStatusSaysSo()
    {
        processes.Answers.Enqueue(FakeProcesses.Answered("issue\tI_1\nproject\tPVT_1\nfield\tF_1\noption\tO_1\tBacklog\n"));

        var refused = await Assert.ThrowsAsync<TrackerException>(() => Tasks().SetStatusAsync(12, BoardStatus.Done, TestContext.Current.CancellationToken));

        Assert.Equal("#12 could not be moved to Done on the board. The board has no status “Done”.", refused.Message);
    }

    [Fact]
    public async Task ABoardThatCannotBeReadSaysSo()
    {
        processes.Answers.Enqueue(FakeProcesses.Answered("issue\tI_1\nproject\tnull\nfield\tnull\n"));

        var refused = await Assert.ThrowsAsync<TrackerException>(() => Tasks().SetStatusAsync(12, BoardStatus.Done, TestContext.Current.CancellationToken));

        Assert.Equal("#12 could not be moved to Done on the board.", refused.Message);
    }

    [Fact]
    public async Task WithoutABoardNothingIsMoved()
    {
        await Tasks(board: null).SetStatusAsync(12, BoardStatus.InProgress, TestContext.Current.CancellationToken);

        Assert.Empty(processes.Asked);
    }

    [Fact]
    public async Task TheBoardOfAUserIsAskedOfTheUser()
    {
        processes.Answers.Enqueue(FakeProcesses.Answered("issue\tI_1\nproject\tPVT_1\nfield\tF_1\noption\tO_1\tTodo\nitem\tPVTI_1\tPVT_1\n"));
        processes.Answers.Enqueue(FakeProcesses.Answered("PVTI_1\n"));

        await Tasks("https://github.com/users/ann/projects/2").SetStatusAsync(12, BoardStatus.Todo, TestContext.Current.CancellationToken);

        Assert.Contains("user(login: $login) { projectV2(number: $project)", processes.Asked[0].Arguments[13], StringComparison.Ordinal);
    }

    [Fact]
    public async Task LabelsCommentsAndClosingAreCommandsOfTheTool()
    {
        foreach (var _ in Enumerable.Range(0, 4))
            processes.Answers.Enqueue(FakeProcesses.Answered(""));

        await Tasks().AddLabelAsync(12, "needs owner", TestContext.Current.CancellationToken);
        await Tasks().RemoveLabelAsync(12, "interrupted", TestContext.Current.CancellationToken);
        await Tasks().CommentAsync(12, "Done.\n\nTwo lines.", TestContext.Current.CancellationToken);
        await Tasks().CloseAsync(12, TestContext.Current.CancellationToken);

        Assert.Equal(
            [
                ["issue", "edit", "12", "--repo", "acme/rocket", "--add-label", "needs owner"],
                ["issue", "edit", "12", "--repo", "acme/rocket", "--remove-label", "interrupted"],
                ["issue", "comment", "12", "--repo", "acme/rocket", "--body", "Done.\n\nTwo lines."],
                ["issue", "close", "12", "--repo", "acme/rocket"],
            ],
            processes.Asked.Select(asked => asked.Arguments));
    }

    [Fact]
    public async Task AChangeThatIsRefusedSaysWhatWasNotDone()
    {
        processes.Answers.Enqueue(FakeProcesses.Failed("could not add label: 'needs owner' not found"));

        var refused = await Assert.ThrowsAsync<TrackerException>(() => Tasks().AddLabelAsync(12, "needs owner", TestContext.Current.CancellationToken));

        Assert.Equal("The label needs owner could not be put on #12. could not add label: 'needs owner' not found", refused.Message);
    }

    [Fact]
    public async Task ANewTaskIsMadeWithItsLabelsAndItsMilestoneAndPutOnTheBoard()
    {
        processes.Answers.Enqueue(FakeProcesses.Answered("https://github.com/acme/rocket/issues/31\n"));
        processes.Answers.Enqueue(FakeProcesses.Answered("issue\tI_31\nproject\tPVT_1\nfield\tF_1\noption\tO_1\tTodo\n"));
        processes.Answers.Enqueue(FakeProcesses.Answered("PVTI_31\n"));
        processes.Answers.Enqueue(FakeProcesses.Answered("PVTI_31\n"));

        var number = await Tasks().CreateAsync(new NewTask("The rest", "What is left.", ["needs owner", "feature"], "v0.2.0"), TestContext.Current.CancellationToken);

        Assert.Equal(31, number);
        Assert.Equal(
            ["issue", "create", "--repo", "acme/rocket", "--title", "The rest", "--body", "What is left.", "--label", "needs owner", "--label", "feature", "--milestone", "v0.2.0"],
            processes.Asked[0].Arguments);
        Assert.Contains("number=31", processes.Asked[1].Arguments);
        Assert.Contains("option=O_1", processes.Asked[3].Arguments);
    }

    [Fact]
    public async Task ANewTaskWithoutAMilestoneNamesNone()
    {
        processes.Answers.Enqueue(FakeProcesses.Answered("https://github.com/acme/rocket/issues/31\n"));

        await Tasks(board: null).CreateAsync(new NewTask("The rest", "What is left.", [], null), TestContext.Current.CancellationToken);

        Assert.Equal(["issue", "create", "--repo", "acme/rocket", "--title", "The rest", "--body", "What is left."], Assert.Single(processes.Asked).Arguments);
    }

    [Fact]
    public async Task AnAnswerThatNamesNoTaskIsARefusal()
    {
        processes.Answers.Enqueue(FakeProcesses.Answered("Creating issue in acme/rocket\n"));

        var refused = await Assert.ThrowsAsync<TrackerException>(() => Tasks().CreateAsync(new NewTask("The rest", "", [], null), TestContext.Current.CancellationToken));

        Assert.Equal("The task “The rest” could not be made.", refused.Message);
    }

    GitHubTasks Tasks(string? board = Board) => new(processes, "acme/rocket", board);
}
