using System.Globalization;
using System.Text;
using TooBusy.Core.Processes;
using TooBusy.Core.Queue;

namespace TooBusy.Trackers.GitHub;

// The tasks of a project as issues of its GitHub repository, with their statuses on the board of the project when
// it has one, through the GitHub command-line tool. The tool itself turns what GitHub answers into lines of
// tab-separated fields. `statuses` are what the board calls the statuses a run knows, as the settings name them;
// without them they are called as GitHub calls them.
public sealed class GitHubTasks(IProcessRunner processes, string repository, string? board, BoardStatuses? statuses = null) : ITaskTracker
{
    readonly BoardStatuses statuses = statuses ?? BoardStatuses.Default;

    // The queue reads this many tasks; a project with more open ones is told so, not read in part.
    const int Page = 100;

    // The statuses of a task on its boards. GitHub gives them only to a token with the `project` scope, so a
    // project without a board does not ask for them.
    const string Statuses = """
        projectItems(first: 20) { nodes { project { url } fieldValueByName(name: "Status") { ... on ProjectV2ItemFieldSingleSelectValue { name } } } }
        """;

    static readonly TimeSpan Patience = TimeSpan.FromSeconds(30);

    public async Task<IReadOnlyList<QueueTask>> ReadOpenAsync(string? milestone, CancellationToken cancellationToken)
    {
        var (owner, name) = Parts();
        string[] filter = [];
        if (milestone is not null)
        {
            // GitHub filters issues by the number of a milestone, not by its title.
            var listed = await AskAsync(
                ["api", $"repos/{repository}/milestones?state=open&per_page=100", "--jq", """.[] | "\(.number)\t\(.title)" """.Trim()],
                $"The milestones of {repository} cannot be read.",
                cancellationToken);
            var number = Lines(listed).Select(line => line.Split('\t', 2)).FirstOrDefault(fields => fields.Length == 2 && fields[1] == milestone)?[0]
                ?? throw new TrackerException($"The milestone “{milestone}” is not open any more.");
            filter = ["-f", $"milestone={number}"];
        }

        // A task is on the board when one of its project items is of that board; what the board calls its status
        // is the fourth field, empty for a task that is not there. The seventh is the title of its milestone, empty
        // for a task without one, and the labels follow it.
        var lines = $".data.repository.issues | \"total\\t\\(.totalCount)\", (.nodes[] | [.number, .title, .url, {Status()}, "
            + "([.blockedBy.nodes[] | select(.state == \"OPEN\") | .number | tostring] | join(\",\")), "
            + "([.subIssues.nodes[] | select(.state == \"OPEN\") | .number | tostring] | join(\",\")), "
            + "(.milestone.title // \"\")] + [.labels.nodes[].name] | @tsv)";
        var answer = await AskAsync(
            ["api", "graphql", "-f", $"owner={owner}", "-f", $"name={name}", .. filter, "-f", $"query={Issues()}", "--jq", lines],
            $"The tasks of {repository} cannot be read.",
            cancellationToken);

        var tasks = new List<QueueTask>();
        foreach (var fields in Lines(answer).Select(line => line.Split('\t')))
        {
            if (fields is ["total", var total])
            {
                if (int.Parse(total, CultureInfo.InvariantCulture) > Page)
                    throw new TrackerException($"{(milestone is null ? repository : $"The milestone “{milestone}”")} has {total} open tasks, and the queue reads only the first {Page}.");
            }
            else if (fields.Length >= 7 && int.TryParse(fields[0], NumberStyles.None, CultureInfo.InvariantCulture, out var number))
            {
                var named = Unescaped(fields[3]);
                var of = Unescaped(fields[6]);
                tasks.Add(new QueueTask(number, Unescaped(fields[1]), fields[2], [.. fields.Skip(7).Select(Unescaped)], statuses.Of(named), named, Numbers(fields[4]), Numbers(fields[5]), of.Length == 0 ? null : of));
            }
        }

        return tasks;
    }

    // A closed task is asked for by its number. The fields of its line are those of a task of the queue, without what
    // it waits for; a task that is open gives no line, and GitHub refuses a number that is not of an issue.
    public async Task<QueueTask?> ReadClosedAsync(int number, CancellationToken cancellationToken)
    {
        var (owner, name) = Parts();
        var result = await processes.RunAsync(
            "gh",
            [
                "api", "graphql", "-f", $"owner={owner}", "-f", $"name={name}", "-F", $"number={Text(number)}",
                "-f", $"query=query($owner: String!, $name: String!, $number: Int!) {{ repository(owner: $owner, name: $name) {{ issue(number: $number) {{ state number title url milestone {{ title }} labels(first: 50) {{ nodes {{ name }} }}{(board is null ? "" : " " + Statuses.Trim())} }} }} }}",
                "--jq", $".data.repository.issue | select(.state == \"CLOSED\") | [.number, .title, .url, {Status()}, (.milestone.title // \"\")] + [.labels.nodes[].name] | @tsv",
            ],
            Patience,
            cancellationToken);
        if (result is { Status: ProcessStatus.Exited, ExitCode: not 0 } && result.Error.Contains("Could not resolve to an Issue", StringComparison.OrdinalIgnoreCase))
            return null;

        var fields = Lines(Answer(result, $"#{number} cannot be read.")).FirstOrDefault()?.Split('\t');
        if (fields is not { Length: >= 5 } || !int.TryParse(fields[0], NumberStyles.None, CultureInfo.InvariantCulture, out var read))
            return null;

        var named = Unescaped(fields[3]);
        var of = Unescaped(fields[4]);
        return new QueueTask(read, Unescaped(fields[1]), fields[2], [.. fields.Skip(5).Select(Unescaped)], statuses.Of(named), named, [], [], of.Length == 0 ? null : of);
    }

    // What the board of the project calls the status of a task, as the filter of the tool reads it from the task:
    // empty for a task that is not there, and in a project without a board.
    string Status() => board is null
        ? "\"\""
        : $"([.projectItems.nodes[] | select((.project.url | ascii_downcase) == {Quoted(board.ToLowerInvariant())})] | if length == 0 then \"\" else (.[0].fieldValueByName.name // \"No status\") end)";

    // The query of the queue, which asks for the boards of a task only when the project has one.
    string Issues() =>
        "query($owner: String!, $name: String!, $milestone: String) { repository(owner: $owner, name: $name) { issues(states: OPEN, first: 100, orderBy: {field: CREATED_AT, direction: ASC}, filterBy: {milestoneNumber: $milestone}) { totalCount nodes { number title url milestone { title } labels(first: 50) { nodes { name } } blockedBy(first: 50) { nodes { number state } } subIssues(first: 50) { nodes { number state } }"
        + (board is null ? "" : " " + Statuses.Trim())
        + " } } } }";

    public async Task SetStatusAsync(int number, BoardStatus status, CancellationToken cancellationToken)
    {
        if (board is null || BoardParts(board) is not var (kind, login, project))
            return;

        var (owner, name) = Parts();
        var failed = $"#{number} could not be moved to {statuses.NameOf(status)} on the board.";
        var read = await AskAsync(
            [
                "api", "graphql", "-f", $"owner={owner}", "-f", $"name={name}", "-F", $"number={Text(number)}", "-f", $"login={login}", "-F", $"project={Text(project)}",
                "-f", $"query=query($owner: String!, $name: String!, $number: Int!, $login: String!, $project: Int!) {{ repository(owner: $owner, name: $name) {{ issue(number: $number) {{ id projectItems(first: 20) {{ nodes {{ id project {{ id }} }} }} }} }} {kind}(login: $login) {{ projectV2(number: $project) {{ id field(name: \"Status\") {{ ... on ProjectV2SingleSelectField {{ id options {{ id name }} }} }} }} }} }}",
                "--jq", $".data | \"issue\\t\\(.repository.issue.id)\", \"project\\t\\(.{kind}.projectV2.id)\", \"field\\t\\(.{kind}.projectV2.field.id)\", (.{kind}.projectV2.field.options[]? | \"option\\t\\(.id)\\t\\(.name)\"), (.repository.issue.projectItems.nodes[] | \"item\\t\\(.id)\\t\\(.project.id)\")",
            ],
            failed,
            cancellationToken);

        var rows = Lines(read).Select(line => line.Split('\t')).ToList();
        string? One(string kind) => rows.FirstOrDefault(row => row[0] == kind && row.Length > 1 && row[1] != "null")?[1];
        if (One("issue") is not { } issue || One("project") is not { } projectId || One("field") is not { } field)
            throw new TrackerException(failed);
        if (rows.FirstOrDefault(row => row is ["option", _, var called] && statuses.Of(called) == status)?[1] is not { } option)
            throw new TrackerException($"{failed} The board has no status “{statuses.NameOf(status)}”.");

        var item = rows.FirstOrDefault(row => row is ["item", _, var of] && of == projectId)?[1]
            ?? (await AskAsync(
                [
                    "api", "graphql", "-f", $"project={projectId}", "-f", $"content={issue}",
                    "-f", "query=mutation($project: ID!, $content: ID!) { addProjectV2ItemById(input: { projectId: $project, contentId: $content }) { item { id } } }",
                    "--jq", ".data.addProjectV2ItemById.item.id // empty",
                ],
                failed,
                cancellationToken)).Trim();
        if (item.Length == 0)
            throw new TrackerException(failed);

        await AskAsync(
            [
                "api", "graphql", "-f", $"project={projectId}", "-f", $"item={item}", "-f", $"field={field}", "-f", $"option={option}",
                "-f", "query=mutation($project: ID!, $item: ID!, $field: ID!, $option: String!) { updateProjectV2ItemFieldValue(input: { projectId: $project, itemId: $item, fieldId: $field, value: { singleSelectOptionId: $option } }) { projectV2Item { id } } }",
                "--jq", ".data.updateProjectV2ItemFieldValue.projectV2Item.id // empty",
            ],
            failed,
            cancellationToken);
    }

    // The tool itself knows who it is logged in as, and calls them `@me`.
    public Task AssignAsync(int number, CancellationToken cancellationToken) =>
        AskAsync(["issue", "edit", Text(number), "--repo", repository, "--add-assignee", "@me"], $"#{number} could not be assigned to the current user.", cancellationToken);

    public Task AddLabelAsync(int number, string label, CancellationToken cancellationToken) =>
        AskAsync(["issue", "edit", Text(number), "--repo", repository, "--add-label", label], $"The label {label} could not be put on #{number}.", cancellationToken);

    public Task RemoveLabelAsync(int number, string label, CancellationToken cancellationToken) =>
        AskAsync(["issue", "edit", Text(number), "--repo", repository, "--remove-label", label], $"The label {label} could not be taken off #{number}.", cancellationToken);

    public Task CommentAsync(int number, string text, CancellationToken cancellationToken) =>
        AskAsync(["issue", "comment", Text(number), "--repo", repository, "--body", text], $"The comment could not be written to #{number}.", cancellationToken);

    public Task CloseAsync(int number, CancellationToken cancellationToken) =>
        AskAsync(["issue", "close", Text(number), "--repo", repository], $"#{number} could not be closed.", cancellationToken);

    // What the tool prints when it does what it is asked; otherwise the tracker says that it did not.
    async Task<string> AskAsync(string[] arguments, string failed, CancellationToken cancellationToken) =>
        Answer(await processes.RunAsync("gh", arguments, Patience, cancellationToken), failed);

    static string Answer(ProcessResult result, string failed)
    {
        if (result is { Status: ProcessStatus.Exited, ExitCode: 0 })
            return result.Output;

        var said = result.Error.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).FirstOrDefault();
        throw new TrackerException(result.Status switch
        {
            ProcessStatus.NotFound => $"{failed} The GitHub command-line tool `gh` is not installed.",
            ProcessStatus.TimedOut => $"{failed} GitHub did not answer.",
            _ => said is null ? failed : $"{failed} {said}",
        });
    }

    (string Owner, string Name) Parts() =>
        repository.Split('/') is [var owner, var name] ? (owner, name) : throw new TrackerException($"{repository} is not a GitHub repository.");

    static IEnumerable<string> Lines(string text) =>
        text.Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(line => line.TrimEnd('\r')).Where(line => line.Length > 0);

    static List<int> Numbers(string text) =>
        [.. text.Split(',', StringSplitOptions.RemoveEmptyEntries).Select(number => int.Parse(number, CultureInfo.InvariantCulture))];

    static string Text(int number) => number.ToString(CultureInfo.InvariantCulture);

    // `https://github.com/orgs/<org>/projects/<n>` or `https://github.com/users/<user>/projects/<n>` in its parts.
    static (string Kind, string Login, int Number)? BoardParts(string board) =>
        Uri.TryCreate(board, UriKind.Absolute, out var address)
        && address.AbsolutePath.Trim('/').Split('/') is [var kind and ("orgs" or "users"), var login, "projects", var number]
        && int.TryParse(number, NumberStyles.None, CultureInfo.InvariantCulture, out var parsed)
            ? (kind == "orgs" ? "organization" : "user", login, parsed)
            : null;

    // A text as the filter of the tool writes one.
    static string Quoted(string text)
    {
        var quoted = new StringBuilder("\"");
        foreach (var character in text)
            quoted.Append(character is '"' or '\\' ? "\\" + character : character.ToString());
        return quoted.Append('"').ToString();
    }

    // A field as it was before the tool wrote it into a line: it writes a tab, a line break and a backslash as two
    // characters each.
    static string Unescaped(string field)
    {
        if (!field.Contains('\\', StringComparison.Ordinal))
            return field;

        var text = new StringBuilder(field.Length);
        for (var index = 0; index < field.Length; index++)
        {
            if (field[index] != '\\' || index == field.Length - 1)
            {
                text.Append(field[index]);
                continue;
            }

            text.Append(field[++index] switch
            {
                't' => '\t',
                'n' => '\n',
                'r' => '\r',
                var other => other,
            });
        }

        return text.ToString();
    }
}
