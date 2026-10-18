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

    // One request asks for a lot, and the setup waits for it under its dots, where the user can go back at any time:
    // it has the patience of the other requests of the setup.
    static readonly TimeSpan Patience = TimeSpan.FromSeconds(15);

    public async Task<SetupBoards> ReadAsync(string? repository, CancellationToken cancellationToken)
    {
        // A repository that cannot be read fails the whole request, so it is asked again without it. A GitHub that did
        // not answer in time is not asked again: the user would wait twice as long for the same.
        ProcessResult? answer = null;
        if (repository?.Split('/') is [var owner, var name])
        {
            answer = await AskAsync(
                ["-f", $"owner={owner}", "-f", $"name={name}", "-f", $"query=query($owner: String!, $name: String!) {{ {Viewer} repository(owner: $owner, name: $name) {{ {Boards} }} }}"],
                cancellationToken);
            if (answer.Status == ProcessStatus.TimedOut)
                return SetupBoards.NoAnswer;
        }

        if (answer is not { Status: ProcessStatus.Exited, ExitCode: 0 })
            answer = await AskAsync(["-f", $"query={{ {Viewer} }}"], cancellationToken);
        if (answer.Status == ProcessStatus.TimedOut)
            return SetupBoards.NoAnswer;

        // A tool that is missing or fails gives none.
        var lines = answer is { Status: ProcessStatus.Exited, ExitCode: 0 } ? answer.Output.Split('\n', StringSplitOptions.RemoveEmptyEntries) : [];

        var linked = Fields(lines, "linked", SettingsValidator.IsBoard).Select(fields => new SetupBoard(fields[1], fields[2], Linked: true)).ToList();
        var others = Fields(lines, "board", SettingsValidator.IsBoard)
            .Where(fields => linked.All(board => board.Address != fields[1]))
            .Select(fields => new SetupBoard(fields[1], fields[2], Linked: false));
        var owners = Fields(lines, "owner", kind => kind is "user" or "org").Select(fields => new SetupOwner(fields[2], fields[1] == "org"));
        return new SetupBoards([.. linked, .. others], [.. owners]);
    }

    Task<ProcessResult> AskAsync(string[] request, CancellationToken cancellationToken) =>
        processes.RunAsync("gh", ["api", "graphql", .. request, "--jq", Lines.Trim()], Patience, cancellationToken);

    static IEnumerable<string[]> Fields(string[] lines, string kind, Func<string, bool> accept) => lines
        .Select(line => line.TrimEnd('\r').Split('\t', 3))
        .Where(fields => fields.Length == 3 && fields[0] == kind && accept(fields[1]));
}
