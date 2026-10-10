using TooBusy.Core.Processes;
using TooBusy.Core.Settings;
using TooBusy.Core.Setup;

namespace TooBusy.Trackers.GitHub;

// The open GitHub Projects boards of the user and of their organisations, and those linked to a repository, as the
// GitHub command-line tool gives them. One request asks for it all, and the tool itself turns the answer into lines
// of tab-separated fields: `owner`, its kind and its login; `board` or `linked`, an address and a title.
public sealed class GitHubBoards(IProcessRunner processes) : ISetupBoards
{
    const string Boards = "projectsV2(first: 50) { nodes { title url closed } }";

    const string Viewer = $"viewer {{ login {Boards} organizations(first: 50) {{ nodes {{ login {Boards} }} }} }}";

    const string Lines = """
        .data | (.viewer | "owner\tuser\t\(.login)", (.organizations.nodes[]? | select(. != null) | "owner\torg\t\(.login)"), ([.projectsV2.nodes[]?, .organizations.nodes[]?.projectsV2.nodes[]?] | .[] | select(. != null and .closed != true) | "board\t\(.url)\t\(.title)")), (.repository.projectsV2.nodes[]? | select(. != null and .closed != true) | "linked\t\(.url)\t\(.title)")
        """;

    // The question waits for the list, so a slow answer is given up.
    static readonly TimeSpan Patience = TimeSpan.FromSeconds(3);

    public async Task<SetupBoards> ReadAsync(string? repository, CancellationToken cancellationToken)
    {
        // A repository that cannot be read fails the whole request, so it is asked again without it.
        var lines = repository?.Split('/') is [var owner, var name]
            ? await AskAsync(
                ["-f", $"owner={owner}", "-f", $"name={name}", "-f", $"query=query($owner: String!, $name: String!) {{ {Viewer} repository(owner: $owner, name: $name) {{ {Boards} }} }}"],
                cancellationToken)
            : null;
        lines ??= await AskAsync(["-f", $"query={{ {Viewer} }}"], cancellationToken) ?? [];

        var linked = Fields(lines, "linked", SettingsValidator.IsBoard).Select(fields => new SetupBoard(fields[1], fields[2], Linked: true)).ToList();
        var others = Fields(lines, "board", SettingsValidator.IsBoard)
            .Where(fields => linked.All(board => board.Address != fields[1]))
            .Select(fields => new SetupBoard(fields[1], fields[2], Linked: false));
        var owners = Fields(lines, "owner", kind => kind is "user" or "org").Select(fields => new SetupOwner(fields[2], fields[1] == "org"));
        return new SetupBoards([.. linked, .. others], [.. owners]);
    }

    // The lines of the answer; null when the tool is missing, fails or is too slow.
    async Task<string[]?> AskAsync(string[] request, CancellationToken cancellationToken)
    {
        var result = await processes.RunAsync("gh", ["api", "graphql", .. request, "--jq", Lines.Trim()], Patience, cancellationToken);
        return result is { Status: ProcessStatus.Exited, ExitCode: 0 } ? result.Output.Split('\n', StringSplitOptions.RemoveEmptyEntries) : null;
    }

    static IEnumerable<string[]> Fields(string[] lines, string kind, Func<string, bool> accept) => lines
        .Select(line => line.TrimEnd('\r').Split('\t', 3))
        .Where(fields => fields.Length == 3 && fields[0] == kind && accept(fields[1]));
}
