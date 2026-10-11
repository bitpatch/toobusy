using TooBusy.Core.Doctor;
using TooBusy.Core.Processes;
using TooBusy.Core.Queue;
using TooBusy.Core.Setup;

namespace TooBusy.Trackers.GitHub;

// What the setup reads from GitHub and what it changes there, through the GitHub command-line tool.
public sealed class GitHubSetup(IProcessRunner processes) : ISetupTracker
{
    const string Scope = "Your GitHub login cannot work with projects. Run `gh auth refresh -s project` and try again.";

    static readonly TimeSpan Patience = TimeSpan.FromSeconds(15);

    public async Task<string?> RefuseBoardAsync(string board, CancellationToken cancellationToken) =>
        Parts(board) is null ? "That is not the address of a project."
        : await CheckBoardAsync(board, cancellationToken) switch
        {
            BoardAccess.Readable => null,
            BoardAccess.NoAnswer => "GitHub did not answer, so the project could not be checked.",
            BoardAccess.LacksScope => Scope,
            _ => "The project cannot be read: it does not exist, or you have no access to it.",
        };

    // Whether the board can be read with the scope it needs, and when not, what stands in the way.
    public async Task<BoardAccess> CheckBoardAsync(string board, CancellationToken cancellationToken)
    {
        if (Parts(board) is not var (kind, login, number))
            return BoardAccess.Unreadable;

        var result = await AskAsync(
            [
                "-f", $"login={login}", "-F", $"number={number}",
                "-f", $"query=query($login: String!, $number: Int!) {{ {kind}(login: $login) {{ projectV2(number: $number) {{ id }} }} }}",
                "--jq", $".data.{kind}.projectV2.id // empty",
            ],
            cancellationToken);
        return result switch
        {
            { Status: not ProcessStatus.Exited } => BoardAccess.NoAnswer,
            { ExitCode: 0 } when result.Output.Trim().Length > 0 => BoardAccess.Readable,
            _ when LacksScope(result) => BoardAccess.LacksScope,
            _ => BoardAccess.Unreadable,
        };
    }

    // Whether the repository is there for the login to read.
    public async Task<bool> CanReadRepositoryAsync(string repository, CancellationToken cancellationToken) =>
        await processes.RunAsync("gh", ["api", $"repos/{repository}", "--jq", ".full_name"], Patience, cancellationToken)
            is { Status: ProcessStatus.Exited, ExitCode: 0 };

    // A repository whose labels cannot be read has none to offer.
    public async Task<IReadOnlyList<string>> ReadLabelsAsync(string repository, CancellationToken cancellationToken) =>
        await FindLabelsAsync(repository, cancellationToken) ?? [];

    // The labels of the repository; null when they cannot be read.
    public async Task<IReadOnlyList<string>?> FindLabelsAsync(string repository, CancellationToken cancellationToken)
    {
        var result = await processes.RunAsync(
            "gh", ["label", "list", "--repo", repository, "--limit", "1000", "--json", "name", "--jq", ".[].name"], Patience, cancellationToken);
        return result is { Status: ProcessStatus.Exited, ExitCode: 0 }
            ? [.. result.Output.Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(line => line.TrimEnd('\r')).Where(name => name.Length > 0)]
            : null;
    }

    public async Task<string> CreateBoardAsync(SetupOwner owner, string title, CancellationToken cancellationToken)
    {
        var kind = owner.Organisation ? "organization" : "user";
        var id = await ValueAsync(
            [
                "-f", $"login={owner.Login}",
                "-f", $"query=query($login: String!) {{ {kind}(login: $login) {{ id }} }}",
                "--jq", $".data.{kind}.id // empty",
            ],
            $"{owner.Login} cannot be read on GitHub.",
            cancellationToken);
        return await ValueAsync(
            [
                "-f", $"owner={id}", "-f", $"title={title}",
                "-f", "query=mutation($owner: ID!, $title: String!) { createProjectV2(input: { ownerId: $owner, title: $title }) { projectV2 { url } } }",
                "--jq", ".data.createProjectV2.projectV2.url // empty",
            ],
            $"The project could not be made for {owner.Login}.",
            cancellationToken);
    }

    public async Task LinkBoardAsync(string board, string repository, CancellationToken cancellationToken)
    {
        var failed = $"The project could not be linked to {repository}.";
        if (Parts(board) is not var (kind, login, number) || repository.Split('/') is not [var owner, var name])
            throw new TrackerException(failed);

        var ids = await ValueAsync(
            [
                "-f", $"login={login}", "-F", $"number={number}", "-f", $"owner={owner}", "-f", $"name={name}",
                "-f", $"query=query($login: String!, $number: Int!, $owner: String!, $name: String!) {{ {kind}(login: $login) {{ projectV2(number: $number) {{ id }} }} repository(owner: $owner, name: $name) {{ id }} }}",
                "--jq", $".data | \"\\(.{kind}.projectV2.id)\\t\\(.repository.id)\"",
            ],
            failed,
            cancellationToken);
        if (ids.Split('\t') is not [var project, var target] || project == "null" || target == "null")
            throw new TrackerException(failed);

        await ValueAsync(
            [
                "-f", $"project={project}", "-f", $"repository={target}",
                "-f", "query=mutation($project: ID!, $repository: ID!) { linkProjectV2ToRepository(input: { projectId: $project, repositoryId: $repository }) { repository { id } } }",
                "--jq", ".data.linkProjectV2ToRepository.repository.id // empty",
            ],
            failed,
            cancellationToken);
    }

    public async Task CreateLabelAsync(string repository, string name, CancellationToken cancellationToken)
    {
        var result = await processes.RunAsync("gh", ["label", "create", name, "--repo", repository], Patience, cancellationToken);
        if (result is { Status: ProcessStatus.Exited, ExitCode: 0 } || result.Error.Contains("already exists", StringComparison.OrdinalIgnoreCase))
            return;

        throw new TrackerException($"The label “{name}” could not be made in {repository}." + (result.Status == ProcessStatus.Exited ? "" : " GitHub did not answer."));
    }

    // The one value a request is made for; without it the request failed, and the tracker says so.
    async Task<string> ValueAsync(string[] request, string failed, CancellationToken cancellationToken)
    {
        var result = await AskAsync(request, cancellationToken);
        if (result is { Status: ProcessStatus.Exited, ExitCode: 0 } && result.Output.Trim() is { Length: > 0 } value)
            return value;

        throw new TrackerException(LacksScope(result) ? Scope : result.Status == ProcessStatus.Exited ? failed : failed + " GitHub did not answer.");
    }

    Task<ProcessResult> AskAsync(string[] request, CancellationToken cancellationToken) =>
        processes.RunAsync("gh", ["api", "graphql", .. request], Patience, cancellationToken);

    // GitHub names the scopes a token lacks in the error of the request.
    static bool LacksScope(ProcessResult result) =>
        result.Error.Contains("scope", StringComparison.OrdinalIgnoreCase) || result.Output.Contains("INSUFFICIENT_SCOPES", StringComparison.Ordinal);

    // `https://github.com/orgs/<org>/projects/<n>` or `https://github.com/users/<user>/projects/<n>` in its parts.
    static (string Kind, string Login, int Number)? Parts(string board) =>
        Uri.TryCreate(board, UriKind.Absolute, out var address)
        && address.AbsolutePath.Trim('/').Split('/') is [var kind and ("orgs" or "users"), var login, "projects", var number]
        && int.TryParse(number, out var parsed)
            ? (kind == "orgs" ? "organization" : "user", login, parsed)
            : null;
}
