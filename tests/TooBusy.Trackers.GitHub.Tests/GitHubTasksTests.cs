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
        Assert.Contains("nodes { number title url milestone { title } labels(first: 50)", arguments[9], StringComparison.Ordinal);
        Assert.Contains("(.milestone.title // \"\")] + [.labels.nodes[].name] | @tsv", arguments[11], StringComparison.Ordinal);
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
        Assert.False(gone.MayPass);
    }

    // As the choice of the user is checked: the title as it is written first, then the only one that differs in case.
    [Theory]
    [InlineData("1\tV0.2.0\n4\tv0.3.0\n", "1")]
    [InlineData("1\tV0.2.0\n4\tv0.2.0\n", "4")]
    public async Task AMilestoneIsFoundWhateverTheCaseOfItsTitle(string open, string number)
    {
        processes.Answers.Enqueue(FakeProcesses.Answered(open));
        processes.Answers.Enqueue(FakeProcesses.Answered("total\t0\n"));

        await Tasks().ReadOpenAsync("v0.2.0", TestContext.Current.CancellationToken);

        Assert.Contains($"milestone={number}", processes.Asked[1].Arguments);
    }

    [Fact]
    public async Task TwoMilestonesThatDifferOnlyInCaseAreNotGuessedBetween()
    {
        processes.Answers.Enqueue(FakeProcesses.Answered("1\tNext\n4\tNEXT\n"));

        var gone = await Assert.ThrowsAsync<TrackerException>(() => Tasks().ReadOpenAsync("next", TestContext.Current.CancellationToken));

        Assert.Equal("The milestone “next” is not open any more.", gone.Message);
        Assert.Single(processes.Asked);
    }

    [Fact]
    public async Task ATaskIsALineOfTheAnswer()
    {
        processes.Answers.Enqueue(FakeProcesses.Answered(
            "total\t3\n"
            + "12\tAdd\\tthe \\\\ thing\thttps://github.com/acme/rocket/issues/12\tIn progress\t4,9\t13\tv0.2.0 \\\\ next\tfeature\tgood first issue\n"
            + "13\tA part\thttps://github.com/acme/rocket/issues/13\tTodo\t\t\t\r\n"
            + "14\tNot there\thttps://github.com/acme/rocket/issues/14\t\t\t\t\tbug\n"));

        var tasks = await Tasks().ReadOpenAsync(null, TestContext.Current.CancellationToken);

        Assert.Equal(3, tasks.Count);
        Assert.Equal((12, "Add\tthe \\ thing", "https://github.com/acme/rocket/issues/12"), (tasks[0].Number, tasks[0].Title, tasks[0].Url));
        Assert.Equal(["feature", "good first issue"], tasks[0].Labels);
        Assert.Equal((BoardStatus.InProgress, "In progress"), (tasks[0].Status, tasks[0].StatusName));
        Assert.Equal([4, 9], tasks[0].BlockedBy);
        Assert.Equal([13], tasks[0].Parts);
        Assert.Equal(["v0.2.0 \\ next", null, null], tasks.Select(task => task.Milestone));
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
        processes.Answers.Enqueue(FakeProcesses.Answered($"total\t1\n1\tOne\thttps://github.com/acme/rocket/issues/1\t{name}\t\t\t\n"));

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
    public async Task TheBoardsOfATaskAreAskedForOnlyInAProjectWithABoard()
    {
        processes.Answers.Enqueue(FakeProcesses.Answered("total\t0\n"));
        processes.Answers.Enqueue(FakeProcesses.Answered("total\t0\n"));

        await Tasks().ReadOpenAsync(null, TestContext.Current.CancellationToken);
        await Tasks(board: null).ReadOpenAsync(null, TestContext.Current.CancellationToken);

        var with = processes.Asked[0].Arguments[^3];
        var without = processes.Asked[1].Arguments[^3];
        Assert.EndsWith(
            "subIssues(first: 50) { nodes { number state } } projectItems(first: 20) { nodes { project { url } fieldValueByName(name: \"Status\") { ... on ProjectV2ItemFieldSingleSelectValue { name } } } } } } } }",
            with,
            StringComparison.Ordinal);
        Assert.Equal(
            "query=query($owner: String!, $name: String!, $milestone: String) { repository(owner: $owner, name: $name) { issues(states: OPEN, first: 100, orderBy: {field: CREATED_AT, direction: ASC}, filterBy: {milestoneNumber: $milestone}) { totalCount nodes { number title url milestone { title } labels(first: 50) { nodes { name } } blockedBy(first: 50) { nodes { number state } } subIssues(first: 50) { nodes { number state } } } } } }",
            without);
        Assert.Equal(["--jq"], [processes.Asked[1].Arguments[^2]]);
        Assert.DoesNotContain("project", string.Join(' ', processes.Asked[1].Arguments), StringComparison.OrdinalIgnoreCase);
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

    // GitHub may answer with no issues and no error, and the tool prints `null` for their count.
    [Fact]
    public async Task ACountThatIsNotANumberIsAnErrorThatStays()
    {
        processes.Answers.Enqueue(FakeProcesses.Answered("total\tnull\n"));

        var unread = await Assert.ThrowsAsync<TrackerException>(() => Tasks().ReadOpenAsync(null, TestContext.Current.CancellationToken));

        Assert.Equal(("The open tasks of acme/rocket cannot be read: the count is `null`.", false), (unread.Message, unread.MayPass));
    }

    // A line that is short of a field, or whose blockers or parts are not numbers, would leave a task out of the queue.
    [Fact]
    public async Task LinesThatCannotBeReadAsTasksAreAnErrorThatStays()
    {
        const string Readable = "12\tAdd\thttps://github.com/acme/rocket/issues/12\tTodo\t\t\t\n";
        processes.Answers.Enqueue(FakeProcesses.Answered($"total\t2\n{Readable}13\tShort\thttps://github.com/acme/rocket/issues/13\tTodo\t\t\n"));
        processes.Answers.Enqueue(FakeProcesses.Answered($"total\t3\n{Readable}13\tA\thttps://github.com/acme/rocket/issues/13\tTodo\t4,x\t\t\n14\tB\thttps://github.com/acme/rocket/issues/14\tTodo\t\tnull\t\n"));

        var one = await Assert.ThrowsAsync<TrackerException>(() => Tasks().ReadOpenAsync(null, TestContext.Current.CancellationToken));
        var two = await Assert.ThrowsAsync<TrackerException>(() => Tasks().ReadOpenAsync(null, TestContext.Current.CancellationToken));

        Assert.Equal(("The open tasks of acme/rocket cannot be read: `gh` printed a line that cannot be read as a task.", false), (one.Message, one.MayPass));
        Assert.Equal("The open tasks of acme/rocket cannot be read: `gh` printed 2 lines that cannot be read as tasks.", two.Message);
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
        Assert.Equal((false, true, false), (refused.MayPass, silent.MayPass, missing.MayPass));
    }

    // A failure that may pass is one where GitHub was away for a moment: the same call may answer when it is made
    // again. One that stays is told as it is.
    [Theory]
    [InlineData("gh: Server Error (HTTP 500)", true)]
    [InlineData("gh: HTTP 502", true)]
    [InlineData("HTTP 503: Service Unavailable (https://api.github.com/graphql)", true)]
    [InlineData("gh: Too Many Requests (HTTP 429)", true)]
    [InlineData("gh: API rate limit exceeded for user ID 1. (HTTP 403)", true)]
    [InlineData("gh: You have exceeded a secondary rate limit. Please wait a few minutes before you try again. (HTTP 403)", true)]
    [InlineData("gh: API rate limit exceeded for user ID 1.", true)]
    [InlineData("error connecting to api.github.com\ncheck your internet connection or https://githubstatus.com", true)]
    [InlineData("Post \"https://api.github.com/graphql\": dial tcp 140.82.112.6:443: connect: connection refused", true)]
    [InlineData("Post \"https://api.github.com/graphql\": read tcp 10.0.0.2:52144->140.82.112.6:443: read: connection reset by peer", true)]
    [InlineData("Post \"https://api.github.com/graphql\": net/http: TLS handshake timeout", true)]
    [InlineData("gh: Something went wrong while executing your query. This may be the result of a timeout, or it could be a GitHub bug.", true)]
    [InlineData("gh: Not Found (HTTP 404)", false)]
    [InlineData("gh: Bad credentials (HTTP 401)", false)]
    [InlineData("gh: Resource not accessible by integration (HTTP 403)", false)]
    [InlineData("gh: Validation Failed (HTTP 422)", false)]
    [InlineData("GraphQL: Could not resolve to an Issue with the number of 12. (repository.issue)", false)]
    [InlineData("gh: Your token has not been granted the required scopes to execute this query. The 'id' field requires one of the following scopes: ['read:project'], but your token has only been granted the: ['repo'] scopes.", false)]
    [InlineData("could not add label: 'needs owner' not found", false)]
    [InlineData("To get started with GitHub CLI, please run:  gh auth login", false)]
    public async Task AFailureThatMayPassIsToldApartFromOneThatStays(string error, bool mayPass)
    {
        processes.Answers.Enqueue(FakeProcesses.Failed(error));

        var failed = await Assert.ThrowsAsync<TrackerException>(() => Tasks().AssignAsync(12, TestContext.Current.CancellationToken));

        Assert.Equal(mayPass, failed.MayPass);
        Assert.Equal($"#12 could not be assigned to the current user. {error.Split('\n')[0]}", failed.Message);
    }

    [Fact]
    public async Task ACallThatDoesNotAnswerInItsTimeMayPassAndAToolThatIsMissingStays()
    {
        processes.Answers.Enqueue(new ProcessResult(ProcessStatus.TimedOut, 0, "", ""));
        processes.Answers.Enqueue(new ProcessResult(ProcessStatus.NotFound, 0, "", ""));

        var silent = await Assert.ThrowsAsync<TrackerException>(() => Tasks().CloseAsync(12, TestContext.Current.CancellationToken));
        var missing = await Assert.ThrowsAsync<TrackerException>(() => Tasks().CloseAsync(12, TestContext.Current.CancellationToken));

        Assert.Equal(("#12 could not be closed. GitHub did not answer.", true), (silent.Message, silent.MayPass));
        Assert.Equal(("#12 could not be closed. The GitHub command-line tool `gh` is not installed.", false), (missing.Message, missing.MayPass));
    }

    [Fact]
    public async Task AClosedTaskIsAskedForByItsNumber()
    {
        processes.Answers.Enqueue(FakeProcesses.Answered("12\tAdd\\tthe thing\thttps://github.com/acme/rocket/issues/12\tDone\tv0.2.0\tfeature\tgood first issue\n"));

        var task = await Tasks().ReadClosedAsync(12, TestContext.Current.CancellationToken);

        Assert.Equal((12, "Add\tthe thing", "https://github.com/acme/rocket/issues/12"), (task!.Number, task.Title, task.Url));
        Assert.Equal((BoardStatus.Done, "Done", "v0.2.0"), (task.Status, task.StatusName, task.Milestone));
        Assert.Equal(["feature", "good first issue"], task.Labels);
        Assert.Empty(task.BlockedBy);
        Assert.Empty(task.Parts);
        Assert.Equal(
            [
                "api", "graphql", "-f", "owner=acme", "-f", "name=rocket", "-F", "number=12",
                "-f", "query=query($owner: String!, $name: String!, $number: Int!) { repository(owner: $owner, name: $name) { issue(number: $number) { state number title url milestone { title } labels(first: 50) { nodes { name } } projectItems(first: 20) { nodes { project { url } fieldValueByName(name: \"Status\") { ... on ProjectV2ItemFieldSingleSelectValue { name } } } } } } }",
                "--jq", ".data.repository.issue | select(.state == \"CLOSED\") | [.number, .title, .url, ([.projectItems.nodes[] | select((.project.url | ascii_downcase) == \"https://github.com/orgs/acme/projects/7\")] | if length == 0 then \"\" else (.[0].fieldValueByName.name // \"No status\") end), (.milestone.title // \"\")] + [.labels.nodes[].name] | @tsv",
            ],
            Assert.Single(processes.Asked).Arguments);
    }

    [Fact]
    public async Task WithoutABoardNoBoardOfAClosedTaskIsAskedFor()
    {
        processes.Answers.Enqueue(FakeProcesses.Answered("12\tAdd the thing\thttps://github.com/acme/rocket/issues/12\t\t\n"));

        var task = await Tasks(board: null).ReadClosedAsync(12, TestContext.Current.CancellationToken);

        Assert.Equal((BoardStatus.Missing, null), (task!.Status, task.Milestone));
        Assert.Empty(task.Labels);
        Assert.DoesNotContain("project", string.Join(' ', processes.Asked[0].Arguments), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ATaskThatIsOpenOrIsNoTaskIsNotAClosedOne()
    {
        // The filter gives no line for an issue that is open, and GitHub refuses a number that is not of an issue.
        processes.Answers.Enqueue(FakeProcesses.Answered(""));
        processes.Answers.Enqueue(FakeProcesses.Failed("gh: Could not resolve to an Issue with the number of 12."));

        Assert.Null(await Tasks().ReadClosedAsync(12, TestContext.Current.CancellationToken));
        Assert.Null(await Tasks().ReadClosedAsync(12, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task AClosedTaskThatCannotBeReadSaysWhy()
    {
        processes.Answers.Enqueue(FakeProcesses.Failed("gh: Bad credentials (HTTP 401)"));
        processes.Answers.Enqueue(new ProcessResult(ProcessStatus.TimedOut, 0, "", ""));

        var refused = await Assert.ThrowsAsync<TrackerException>(() => Tasks().ReadClosedAsync(12, TestContext.Current.CancellationToken));
        var silent = await Assert.ThrowsAsync<TrackerException>(() => Tasks().ReadClosedAsync(12, TestContext.Current.CancellationToken));

        Assert.Equal("#12 cannot be read. gh: Bad credentials (HTTP 401)", refused.Message);
        Assert.Equal("#12 cannot be read. GitHub did not answer.", silent.Message);
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
    public async Task ATaskIsMovedToTheStatusTheSettingsName()
    {
        processes.Answers.Enqueue(FakeProcesses.Answered("issue\tI_1\nproject\tPVT_1\nfield\tF_1\noption\tO_1\tReady\noption\tO_2\tDone\noption\tO_3\tSHIPPED\nitem\tPVTI_1\tPVT_1\n"));
        processes.Answers.Enqueue(FakeProcesses.Answered("PVTI_1\n"));

        await Named().SetStatusAsync(12, BoardStatus.Done, TestContext.Current.CancellationToken);

        Assert.Contains("option=O_3", processes.Asked[1].Arguments);
    }

    [Fact]
    public async Task ABoardWithoutTheStatusOfTheSettingsSaysSoByItsName()
    {
        processes.Answers.Enqueue(FakeProcesses.Answered("issue\tI_1\nproject\tPVT_1\nfield\tF_1\noption\tO_1\tTodo\noption\tO_2\tIn Progress\n"));

        var refused = await Assert.ThrowsAsync<TrackerException>(() => Named().SetStatusAsync(12, BoardStatus.InProgress, TestContext.Current.CancellationToken));

        Assert.Equal("#12 could not be moved to Doing on the board. The board has no status “Doing”.", refused.Message);
        Assert.False(refused.MayPass);
    }

    [Fact]
    public async Task TheQueueKnowsAStatusByTheNameOfTheSettings()
    {
        processes.Answers.Enqueue(FakeProcesses.Answered("total\t3\n1\tOne\thttps://x/1\tready\t\t\t\n2\tTwo\thttps://x/2\tTodo\t\t\t\n3\tThree\thttps://x/3\tDoing\t\t\t\n"));

        var tasks = await Named().ReadOpenAsync(null, TestContext.Current.CancellationToken);

        Assert.Equal([(BoardStatus.Todo, "ready"), (BoardStatus.Other, "Todo"), (BoardStatus.InProgress, "Doing")], tasks.Select(task => (task.Status, task.StatusName)));
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
    public async Task ATaskIsAssignedToTheUserTheToolIsLoggedInAs()
    {
        processes.Answers.Enqueue(FakeProcesses.Answered(""));

        await Tasks().AssignAsync(12, TestContext.Current.CancellationToken);

        Assert.Equal([["issue", "edit", "12", "--repo", "acme/rocket", "--add-assignee", "@me"]], processes.Asked.Select(asked => asked.Arguments));
    }

    [Fact]
    public async Task WithoutABoardATaskIsAssignedInTheSameWay()
    {
        processes.Answers.Enqueue(FakeProcesses.Answered(""));

        await new GitHubTasks(processes, "acme/rocket", null).AssignAsync(12, TestContext.Current.CancellationToken);

        Assert.Equal([["issue", "edit", "12", "--repo", "acme/rocket", "--add-assignee", "@me"]], processes.Asked.Select(asked => asked.Arguments));
    }

    [Fact]
    public async Task AnAssignmentThatIsRefusedSaysSo()
    {
        processes.Answers.Enqueue(FakeProcesses.Failed("failed to update https://github.com/acme/rocket/issues/12: 'rocketeer' not found"));

        var refused = await Assert.ThrowsAsync<TrackerException>(() => Tasks().AssignAsync(12, TestContext.Current.CancellationToken));

        Assert.Equal("#12 could not be assigned to the current user. failed to update https://github.com/acme/rocket/issues/12: 'rocketeer' not found", refused.Message);
    }

    [Fact]
    public async Task AChangeThatIsRefusedSaysWhatWasNotDone()
    {
        processes.Answers.Enqueue(FakeProcesses.Failed("could not add label: 'needs owner' not found"));

        var refused = await Assert.ThrowsAsync<TrackerException>(() => Tasks().AddLabelAsync(12, "needs owner", TestContext.Current.CancellationToken));

        Assert.Equal("The label needs owner could not be put on #12. could not add label: 'needs owner' not found", refused.Message);
    }

    GitHubTasks Tasks(string? board = Board) => new(processes, "acme/rocket", board);

    // The tasks of a project whose settings call the statuses of its board otherwise than GitHub does.
    GitHubTasks Named() => new(processes, "acme/rocket", Board, new BoardStatuses("Ready", "Doing", "Shipped"));
}
