using TooBusy.Core.Processes;
using TooBusy.Core.Setup;

namespace TooBusy.Trackers.GitHub.Tests;

public class GitHubSetupTests
{
    const string Rocket = "https://github.com/orgs/acme/projects/7";
    const string Scope = "Your GitHub login cannot work with projects. Run `gh auth refresh -s project` and try again.";

    readonly FakeProcesses processes = new();

    [Fact]
    public async Task TheLabelsAreTheLinesOfTheAnswer()
    {
        processes.Answers.Enqueue(FakeProcesses.Answered("bug\ngood first issue\r\n\n"));

        var labels = await Setup().ReadLabelsAsync("acme/rocket", TestContext.Current.CancellationToken);

        Assert.Equal(["bug", "good first issue"], labels);
        var (command, arguments) = Assert.Single(processes.Asked);
        Assert.Equal("gh", command);
        Assert.Equal(["label", "list", "--repo", "acme/rocket", "--limit", "1000", "--json", "name", "--jq", ".[].name"], arguments);
    }

    [Fact]
    public async Task LabelsThatCannotBeReadAreNone()
    {
        processes.Answers.Enqueue(FakeProcesses.Failed("gh: Not Found"));

        Assert.Empty(await Setup().ReadLabelsAsync("acme/rocket", TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task ABoardThatCanBeReadIsNotRefused()
    {
        processes.Answers.Enqueue(FakeProcesses.Answered("PVT_1\n"));

        Assert.Null(await Setup().RefuseBoardAsync(Rocket, TestContext.Current.CancellationToken));

        var arguments = Assert.Single(processes.Asked).Arguments;
        Assert.Equal(["api", "graphql", "-f", "login=acme", "-F", "number=7", "-f"], arguments.Take(7));
        Assert.Equal("query=query($login: String!, $number: Int!) { organization(login: $login) { projectV2(number: $number) { id } } }", arguments[7]);
        Assert.Equal(["--jq", ".data.organization.projectV2.id // empty"], arguments.Skip(8));
    }

    [Fact]
    public async Task TheBoardOfAUserIsAskedOfTheUser()
    {
        processes.Answers.Enqueue(FakeProcesses.Answered("PVT_1\n"));

        await Setup().RefuseBoardAsync("https://github.com/users/ann/projects/2", TestContext.Current.CancellationToken);

        Assert.Contains("user(login: $login)", Assert.Single(processes.Asked).Arguments[7], StringComparison.Ordinal);
    }

    [Fact]
    public async Task ABoardIsRefusedForTheReasonTheToolGives()
    {
        processes.Answers.Enqueue(FakeProcesses.Failed("gh: Could not resolve to a ProjectV2 with the number 7."));
        processes.Answers.Enqueue(FakeProcesses.Failed("gh: Your token has not been granted the required scopes to execute this query. The 'projectV2' field requires one of the following scopes: ['read:project']"));
        processes.Answers.Enqueue(new ProcessResult(ProcessStatus.TimedOut, 0, "", ""));
        processes.Answers.Enqueue(FakeProcesses.Answered("\n"));

        Assert.Equal("The project cannot be read: it does not exist, or you have no access to it.", await Setup().RefuseBoardAsync(Rocket, TestContext.Current.CancellationToken));
        Assert.Equal(Scope, await Setup().RefuseBoardAsync(Rocket, TestContext.Current.CancellationToken));
        Assert.Equal("GitHub did not answer, so the project could not be checked.", await Setup().RefuseBoardAsync(Rocket, TestContext.Current.CancellationToken));
        Assert.Equal("The project cannot be read: it does not exist, or you have no access to it.", await Setup().RefuseBoardAsync(Rocket, TestContext.Current.CancellationToken));
        Assert.Equal("That is not the address of a project.", await Setup().RefuseBoardAsync("https://github.com/acme/rocket", TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task ABoardIsMadeForItsOwnerAndItsAddressIsGiven()
    {
        processes.Answers.Enqueue(FakeProcesses.Answered("O_1\n"));
        processes.Answers.Enqueue(FakeProcesses.Answered(Rocket + "\n"));

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
        processes.Answers.Enqueue(FakeProcesses.Answered("U_1\n"));
        processes.Answers.Enqueue(FakeProcesses.Failed("gh: Your token has not been granted the required scopes"));

        var refused = await Assert.ThrowsAsync<TrackerException>(() => Setup().CreateBoardAsync(new SetupOwner("ann", false), "Rocket", TestContext.Current.CancellationToken));

        Assert.Equal(Scope, refused.Message);
        Assert.Contains("user(login: $login)", processes.Asked[0].Arguments[5], StringComparison.Ordinal);
    }

    [Fact]
    public async Task AnOwnerThatCannotBeReadStopsTheMaking()
    {
        processes.Answers.Enqueue(FakeProcesses.Failed("gh: Could not resolve to an Organization"));

        var refused = await Assert.ThrowsAsync<TrackerException>(() => Setup().CreateBoardAsync(new SetupOwner("acme", true), "Rocket", TestContext.Current.CancellationToken));

        Assert.Equal("acme cannot be read on GitHub.", refused.Message);
        Assert.Single(processes.Asked);
    }

    [Fact]
    public async Task ABoardIsLinkedToTheRepositoryByTheirIds()
    {
        processes.Answers.Enqueue(FakeProcesses.Answered("PVT_1\tR_1\n"));
        processes.Answers.Enqueue(FakeProcesses.Answered("R_1\n"));

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
        processes.Answers.Enqueue(FakeProcesses.Answered("PVT_1\tnull\n"));

        var missing = await Assert.ThrowsAsync<TrackerException>(() => Setup().LinkBoardAsync(Rocket, "acme/rocket", TestContext.Current.CancellationToken));
        Assert.Equal("The project could not be linked to acme/rocket.", missing.Message);
        Assert.Single(processes.Asked);

        processes.Answers.Enqueue(FakeProcesses.Answered("PVT_1\tR_1\n"));
        processes.Answers.Enqueue(new ProcessResult(ProcessStatus.TimedOut, 0, "", ""));

        var silent = await Assert.ThrowsAsync<TrackerException>(() => Setup().LinkBoardAsync(Rocket, "acme/rocket", TestContext.Current.CancellationToken));
        Assert.Equal("The project could not be linked to acme/rocket. GitHub did not answer.", silent.Message);
    }

    [Theory]
    [InlineData(ProcessStatus.Exited, 0, GitHubCliState.Ready)]
    [InlineData(ProcessStatus.Exited, 1, GitHubCliState.LoggedOut)]
    [InlineData(ProcessStatus.TimedOut, 0, GitHubCliState.LoggedOut)]
    [InlineData(ProcessStatus.NotFound, 0, GitHubCliState.Missing)]
    public async Task TheToolIsReadyWhenItIsThereAndLoggedIn(ProcessStatus status, int exitCode, GitHubCliState expected)
    {
        processes.Answers.Enqueue(new ProcessResult(status, exitCode, "", ""));

        Assert.Equal(expected, await new GitHubCli(processes).CheckAsync(TestContext.Current.CancellationToken));
        Assert.Equal(["auth", "status", "--hostname", "github.com"], Assert.Single(processes.Asked).Arguments);
    }

    GitHubSetup Setup() => new(processes);
}
