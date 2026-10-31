using TooBusy.Core.Doctor;
using TooBusy.Core.Processes;
using TooBusy.Core.Queue;
using TooBusy.Core.Setup;

namespace TooBusy.Trackers.GitHub.Tests;

public class GitHubSetupTests
{
    const string Rocket = "https://github.com/orgs/acme/projects/7";
    const string Scope = "Your GitHub login cannot work with projects. Run `gh auth refresh -s project` and try again.";

    readonly ScriptedProcesses processes = new();

    [Fact]
    public async Task TheLabelsAreTheLinesOfTheAnswer()
    {
        processes.Answers.Enqueue(ScriptedProcesses.Answered("bug\ngood first issue\r\n\n"));

        var labels = await Setup().ReadLabelsAsync("acme/rocket", TestContext.Current.CancellationToken);

        Assert.Equal(["bug", "good first issue"], labels);
        var (command, arguments, _) = Assert.Single(processes.Asked);
        Assert.Equal("gh", command);
        Assert.Equal(["label", "list", "--repo", "acme/rocket", "--limit", "1000", "--json", "name", "--jq", ".[].name"], arguments);
    }

    [Fact]
    public async Task LabelsThatCannotBeReadAreNone()
    {
        processes.Answers.Enqueue(ScriptedProcesses.Failed("gh: Not Found"));

        Assert.Empty(await Setup().ReadLabelsAsync("acme/rocket", TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task LabelsThatCannotBeReadAreToldApartFromNoLabels()
    {
        processes.Answers.Enqueue(ScriptedProcesses.Answered(""));
        processes.Answers.Enqueue(ScriptedProcesses.Failed("gh: Not Found"));
        processes.Answers.Enqueue(new ProcessResult(ProcessStatus.TimedOut, 0, "", ""));

        Assert.Equal([], await Setup().FindLabelsAsync("acme/rocket", TestContext.Current.CancellationToken));
        Assert.Null(await Setup().FindLabelsAsync("acme/rocket", TestContext.Current.CancellationToken));
        Assert.Null(await Setup().FindLabelsAsync("acme/rocket", TestContext.Current.CancellationToken));
    }

    [Theory]
    [InlineData(ProcessStatus.Exited, 0, true)]
    [InlineData(ProcessStatus.Exited, 1, false)]
    [InlineData(ProcessStatus.TimedOut, 0, false)]
    [InlineData(ProcessStatus.NotFound, 0, false)]
    public async Task ARepositoryCanBeReadWhenGitHubGivesIt(ProcessStatus status, int exitCode, bool expected)
    {
        processes.Answers.Enqueue(new ProcessResult(status, exitCode, "acme/rocket\n", ""));

        Assert.Equal(expected, await Setup().CanReadRepositoryAsync("acme/rocket", TestContext.Current.CancellationToken));
        var (command, arguments, _) = Assert.Single(processes.Asked);
        Assert.Equal("gh", command);
        Assert.Equal(["api", "repos/acme/rocket", "--jq", ".full_name"], arguments);
    }

    [Fact]
    public async Task TheAccessToABoardIsWhatTheToolTells()
    {
        processes.Answers.Enqueue(ScriptedProcesses.Answered("PVT_1\n"));
        processes.Answers.Enqueue(ScriptedProcesses.Failed("gh: Could not resolve to a ProjectV2 with the number 7."));
        processes.Answers.Enqueue(ScriptedProcesses.Failed("gh: Your token has not been granted the required scopes to execute this query."));
        processes.Answers.Enqueue(new ProcessResult(ProcessStatus.TimedOut, 0, "", ""));

        Assert.Equal(BoardAccess.Readable, await Setup().CheckBoardAsync(Rocket, TestContext.Current.CancellationToken));
        Assert.Equal(BoardAccess.Unreadable, await Setup().CheckBoardAsync(Rocket, TestContext.Current.CancellationToken));
        Assert.Equal(BoardAccess.LacksScope, await Setup().CheckBoardAsync(Rocket, TestContext.Current.CancellationToken));
        Assert.Equal(BoardAccess.NoAnswer, await Setup().CheckBoardAsync(Rocket, TestContext.Current.CancellationToken));
        Assert.Equal(BoardAccess.Unreadable, await Setup().CheckBoardAsync("https://github.com/acme/rocket", TestContext.Current.CancellationToken));
        Assert.Equal(4, processes.Asked.Count);
    }

    [Fact]
    public async Task TheStatusesOfABoardAreTheOptionsOfItsStatusField()
    {
        processes.Answers.Enqueue(ScriptedProcesses.Answered("board\nstatus\tBacklog\r\nstatus\tIn Progress\nstatus\tDone\n"));

        var statuses = await Setup().ReadStatusesAsync(Rocket, TestContext.Current.CancellationToken);

        Assert.Equal(["Backlog", "In Progress", "Done"], statuses);
        var (command, arguments, _) = Assert.Single(processes.Asked);
        Assert.Equal("gh", command);
        Assert.Equal(["api", "graphql", "-f", "login=acme", "-F", "number=7", "-f"], arguments.Take(7));
        Assert.Equal(
            "query=query($login: String!, $number: Int!) { organization(login: $login) { projectV2(number: $number) { fields(first: 100) { nodes { ... on ProjectV2SingleSelectField { name options { name } } } } } } }",
            arguments[7]);
        Assert.Equal(
            ["--jq", """.data.organization.projectV2 | select(. != null) | "board", (.fields.nodes[]? | select(.name == "Status") | .options[]? | "status\t\(.name)")"""],
            arguments.Skip(8));
    }

    [Fact]
    public async Task TheStatusesOfTheBoardOfAUserAreAskedOfTheUser()
    {
        processes.Answers.Enqueue(ScriptedProcesses.Answered("board\nstatus\tTodo\n"));

        Assert.Equal(["Todo"], await Setup().ReadStatusesAsync("https://github.com/users/ann/projects/2", TestContext.Current.CancellationToken));

        var arguments = Assert.Single(processes.Asked).Arguments;
        Assert.Contains("user(login: $login)", arguments[7], StringComparison.Ordinal);
        Assert.StartsWith(".data.user.projectV2 ", arguments[9], StringComparison.Ordinal);
    }

    [Fact]
    public async Task ABoardWithoutTheStatusFieldHasNoStatusesAndOneThatCannotBeReadHasNoAnswer()
    {
        // The board is there and has no such field; then a board that is not there, a login without the scope, a
        // slow answer and an answer that names no board.
        processes.Answers.Enqueue(ScriptedProcesses.Answered("board\n"));
        processes.Answers.Enqueue(ScriptedProcesses.Failed("gh: Could not resolve to a ProjectV2 with the number 7."));
        processes.Answers.Enqueue(ScriptedProcesses.Failed("gh: Your token has not been granted the required scopes to execute this query."));
        processes.Answers.Enqueue(new ProcessResult(ProcessStatus.TimedOut, 0, "", ""));
        processes.Answers.Enqueue(ScriptedProcesses.Answered(""));

        Assert.Equal([], await Setup().ReadStatusesAsync(Rocket, TestContext.Current.CancellationToken));
        Assert.Null(await Setup().ReadStatusesAsync(Rocket, TestContext.Current.CancellationToken));
        Assert.Null(await Setup().ReadStatusesAsync(Rocket, TestContext.Current.CancellationToken));
        Assert.Null(await Setup().ReadStatusesAsync(Rocket, TestContext.Current.CancellationToken));
        Assert.Null(await Setup().ReadStatusesAsync(Rocket, TestContext.Current.CancellationToken));
        Assert.Null(await Setup().ReadStatusesAsync("https://github.com/acme/rocket", TestContext.Current.CancellationToken));
        Assert.Equal(5, processes.Asked.Count);
    }

    [Fact]
    public async Task ABoardThatCanBeReadIsNotRefused()
    {
        processes.Answers.Enqueue(ScriptedProcesses.Answered("PVT_1\n"));

        Assert.Null(await Setup().RefuseBoardAsync(Rocket, TestContext.Current.CancellationToken));

        var arguments = Assert.Single(processes.Asked).Arguments;
        Assert.Equal(["api", "graphql", "-f", "login=acme", "-F", "number=7", "-f"], arguments.Take(7));
        Assert.Equal("query=query($login: String!, $number: Int!) { organization(login: $login) { projectV2(number: $number) { id } } }", arguments[7]);
        Assert.Equal(["--jq", ".data.organization.projectV2.id // empty"], arguments.Skip(8));
    }

    [Fact]
    public async Task TheBoardOfAUserIsAskedOfTheUser()
    {
        processes.Answers.Enqueue(ScriptedProcesses.Answered("PVT_1\n"));

        await Setup().RefuseBoardAsync("https://github.com/users/ann/projects/2", TestContext.Current.CancellationToken);

        Assert.Contains("user(login: $login)", Assert.Single(processes.Asked).Arguments[7], StringComparison.Ordinal);
    }

    [Fact]
    public async Task ABoardIsRefusedForTheReasonTheToolGives()
    {
        processes.Answers.Enqueue(ScriptedProcesses.Failed("gh: Could not resolve to a ProjectV2 with the number 7."));
        processes.Answers.Enqueue(ScriptedProcesses.Failed("gh: Your token has not been granted the required scopes to execute this query. The 'projectV2' field requires one of the following scopes: ['read:project']"));
        processes.Answers.Enqueue(new ProcessResult(ProcessStatus.TimedOut, 0, "", ""));
        processes.Answers.Enqueue(ScriptedProcesses.Answered("\n"));

        Assert.Equal("The project cannot be read: it does not exist, or you have no access to it.", await Setup().RefuseBoardAsync(Rocket, TestContext.Current.CancellationToken));
        Assert.Equal(Scope, await Setup().RefuseBoardAsync(Rocket, TestContext.Current.CancellationToken));
        Assert.Equal("GitHub did not answer, so the project could not be checked.", await Setup().RefuseBoardAsync(Rocket, TestContext.Current.CancellationToken));
        Assert.Equal("The project cannot be read: it does not exist, or you have no access to it.", await Setup().RefuseBoardAsync(Rocket, TestContext.Current.CancellationToken));
        Assert.Equal("That is not the address of a project.", await Setup().RefuseBoardAsync("https://github.com/acme/rocket", TestContext.Current.CancellationToken));
    }

    // A `gh` that is not there or cannot be started is not a GitHub that did not answer.
    [Fact]
    public async Task ABoardThatCannotBeCheckedSaysWhyGitHubGaveNoAnswer()
    {
        processes.Answers.Enqueue(new ProcessResult(ProcessStatus.NotFound, 0, "", ""));
        processes.Answers.Enqueue(new ProcessResult(ProcessStatus.NotStarted, 0, "", "Permission denied"));

        Assert.Equal("The project could not be checked. The GitHub command-line tool `gh` is not installed.", await Setup().RefuseBoardAsync(Rocket, TestContext.Current.CancellationToken));
        Assert.Equal("The project could not be checked. The GitHub command-line tool `gh` could not be started: Permission denied.", await Setup().RefuseBoardAsync(Rocket, TestContext.Current.CancellationToken));
    }

    // A board is what the settings take as one, so a number with a sign is not asked for as a board.
    [Fact]
    public async Task AnAddressTheSettingsRefuseIsNotAskedFor()
    {
        const string signed = "https://github.com/orgs/acme/projects/-3";

        Assert.Equal("That is not the address of a project.", await Setup().RefuseBoardAsync(signed, TestContext.Current.CancellationToken));
        Assert.Equal(BoardAccess.Unreadable, await Setup().CheckBoardAsync(signed, TestContext.Current.CancellationToken));
        Assert.Null(await Setup().ReadStatusesAsync(signed, TestContext.Current.CancellationToken));
        Assert.Empty(processes.Asked);
    }

    [Fact]
    public async Task ABoardIsMadeForItsOwnerAndItsAddressIsGiven()
    {
        processes.Answers.Enqueue(ScriptedProcesses.Answered("O_1\n"));
        processes.Answers.Enqueue(ScriptedProcesses.Answered(Rocket + "\n"));

        var address = await Setup().CreateBoardAsync(new SetupOwner("acme", true), "Rocket \"one\"", TestContext.Current.CancellationToken);

        Assert.Equal(Rocket, address);
        Assert.Equal(
            ["api", "graphql", "-f", "login=acme", "-f", "query=query($login: String!) { organization(login: $login) { id } }", "--jq", ".data.organization.id // empty"],
            processes.Asked[0].Arguments);
        Assert.Equal(
            [
                "api", "graphql", "-f", "owner=O_1", "-f", "title=Rocket \"one\"",
                "-f", "query=mutation($owner: ID!, $title: String!) { createProjectV2(input: { ownerId: $owner, title: $title }) { projectV2 { url } } }",
                "--jq", ".data.createProjectV2.projectV2.url // empty",
            ],
            processes.Asked[1].Arguments);
    }

    [Fact]
    public async Task ABoardThatCannotBeMadeSaysWhy()
    {
        processes.Answers.Enqueue(ScriptedProcesses.Answered("U_1\n"));
        processes.Answers.Enqueue(ScriptedProcesses.Failed("gh: Your token has not been granted the required scopes"));

        var refused = await Assert.ThrowsAsync<TrackerException>(() => Setup().CreateBoardAsync(new SetupOwner("ann", false), "Rocket", TestContext.Current.CancellationToken));

        Assert.Equal(Scope, refused.Message);
        Assert.Contains("user(login: $login)", processes.Asked[0].Arguments[5], StringComparison.Ordinal);
    }

    [Fact]
    public async Task AnOwnerThatCannotBeReadStopsTheMaking()
    {
        processes.Answers.Enqueue(ScriptedProcesses.Failed("gh: Could not resolve to an Organization"));

        var refused = await Assert.ThrowsAsync<TrackerException>(() => Setup().CreateBoardAsync(new SetupOwner("acme", true), "Rocket", TestContext.Current.CancellationToken));

        Assert.Equal("acme cannot be read on GitHub.", refused.Message);
        Assert.Single(processes.Asked);
    }

    [Fact]
    public async Task ABoardIsLinkedToTheRepositoryByTheirIds()
    {
        processes.Answers.Enqueue(ScriptedProcesses.Answered("PVT_1\tR_1\n"));
        processes.Answers.Enqueue(ScriptedProcesses.Answered("R_1\n"));

        await Setup().LinkBoardAsync(Rocket, "acme/rocket", TestContext.Current.CancellationToken);

        Assert.Equal(["api", "graphql", "-f", "login=acme", "-F", "number=7", "-f", "owner=acme", "-f", "name=rocket", "-f"], processes.Asked[0].Arguments.Take(11));
        Assert.Equal(
            "query=query($login: String!, $number: Int!, $owner: String!, $name: String!) { organization(login: $login) { projectV2(number: $number) { id } } repository(owner: $owner, name: $name) { id } }",
            processes.Asked[0].Arguments[11]);
        Assert.Equal(".data | \"\\(.organization.projectV2.id)\\t\\(.repository.id)\"", processes.Asked[0].Arguments[13]);
        Assert.Equal(
            [
                "api", "graphql", "-f", "project=PVT_1", "-f", "repository=R_1",
                "-f", "query=mutation($project: ID!, $repository: ID!) { linkProjectV2ToRepository(input: { projectId: $project, repositoryId: $repository }) { repository { id } } }",
                "--jq", ".data.linkProjectV2ToRepository.repository.id // empty",
            ],
            processes.Asked[1].Arguments);
    }

    [Fact]
    public async Task ALinkThatCannotBeMadeSaysSo()
    {
        processes.Answers.Enqueue(ScriptedProcesses.Answered("PVT_1\tnull\n"));

        var missing = await Assert.ThrowsAsync<TrackerException>(() => Setup().LinkBoardAsync(Rocket, "acme/rocket", TestContext.Current.CancellationToken));
        Assert.Equal("The project could not be linked to acme/rocket.", missing.Message);
        Assert.Single(processes.Asked);

        processes.Answers.Enqueue(ScriptedProcesses.Answered("PVT_1\tR_1\n"));
        processes.Answers.Enqueue(new ProcessResult(ProcessStatus.TimedOut, 0, "", ""));

        var silent = await Assert.ThrowsAsync<TrackerException>(() => Setup().LinkBoardAsync(Rocket, "acme/rocket", TestContext.Current.CancellationToken));
        Assert.Equal("The project could not be linked to acme/rocket. GitHub did not answer.", silent.Message);
    }

    [Fact]
    public async Task ALabelIsMadeInTheRepository()
    {
        processes.Answers.Enqueue(ScriptedProcesses.Answered(""));

        await Setup().CreateLabelAsync("acme/rocket", "needs owner", TestContext.Current.CancellationToken);

        var (command, arguments, _) = Assert.Single(processes.Asked);
        Assert.Equal("gh", command);
        Assert.Equal(["label", "create", "needs owner", "--repo", "acme/rocket"], arguments);
    }

    [Fact]
    public async Task ALabelThatIsThereAlreadyIsLeftAsItIs()
    {
        processes.Answers.Enqueue(ScriptedProcesses.Failed("label with name \"manual\" already exists; use `--force` to update its color and description"));

        await Setup().CreateLabelAsync("acme/rocket", "manual", TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task ALabelThatCannotBeMadeSaysSo()
    {
        processes.Answers.Enqueue(ScriptedProcesses.Failed("HTTP 403: Resource not accessible"));
        processes.Answers.Enqueue(new ProcessResult(ProcessStatus.TimedOut, 0, "", ""));
        processes.Answers.Enqueue(new ProcessResult(ProcessStatus.NotStarted, 0, "", "Permission denied"));

        var refused = await Assert.ThrowsAsync<TrackerException>(() => Setup().CreateLabelAsync("acme/rocket", "manual", TestContext.Current.CancellationToken));
        var silent = await Assert.ThrowsAsync<TrackerException>(() => Setup().CreateLabelAsync("acme/rocket", "manual", TestContext.Current.CancellationToken));
        var unstarted = await Assert.ThrowsAsync<TrackerException>(() => Setup().CreateLabelAsync("acme/rocket", "manual", TestContext.Current.CancellationToken));

        Assert.Equal("The label “manual” could not be made in acme/rocket.", refused.Message);
        Assert.Equal("The label “manual” could not be made in acme/rocket. GitHub did not answer.", silent.Message);
        Assert.Equal("The label “manual” could not be made in acme/rocket. The GitHub command-line tool `gh` could not be started: Permission denied.", unstarted.Message);
    }

    [Theory]
    [InlineData(ProcessStatus.Exited, 0, GitHubCliState.Ready)]
    [InlineData(ProcessStatus.Exited, 1, GitHubCliState.LoggedOut)]
    [InlineData(ProcessStatus.TimedOut, 0, GitHubCliState.TimedOut)]
    [InlineData(ProcessStatus.NotFound, 0, GitHubCliState.Missing)]
    [InlineData(ProcessStatus.NotStarted, 0, GitHubCliState.Unstarted)]
    public async Task TheToolIsReadyWhenItIsThereAndLoggedIn(ProcessStatus status, int exitCode, GitHubCliState expected)
    {
        processes.Answers.Enqueue(new ProcessResult(status, exitCode, "", ""));

        Assert.Equal(expected, await new GitHubCli(processes).CheckAsync(TestContext.Current.CancellationToken));
        Assert.Equal(["auth", "status", "--hostname", "github.com"], Assert.Single(processes.Asked).Arguments);
    }

    // What `gh auth status` says when its own request to GitHub timed out, and when the token is bad.
    [Theory]
    [InlineData("github.com\n  X Timeout trying to log in to github.com account denis (keyring)\n  - Active account: true\n", GitHubCliState.TimedOut)]
    [InlineData("github.com\n  X Failed to log in to github.com using token (GH_TOKEN)\n  - Active account: true\n  - The token in GH_TOKEN is invalid.\n", GitHubCliState.LoggedOut)]
    public async Task AToolThatSaysItTimedOutIsNotLoggedOut(string error, GitHubCliState expected)
    {
        processes.Answers.Enqueue(new ProcessResult(ProcessStatus.Exited, 1, "", error));

        Assert.Equal(expected, await new GitHubCli(processes).CheckAsync(TestContext.Current.CancellationToken));
    }

    GitHubSetup Setup() => new(processes);
}
