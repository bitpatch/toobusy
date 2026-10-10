using System.Globalization;
using System.Text;
using TooBusy.Core.Processes;
using TooBusy.Core.Queue;

namespace TooBusy.Trackers.GitHub;

// The tasks of a project as issues of its GitHub repository, with their statuses on the board of the project when
// it has one, through the GitHub command-line tool. The tool itself turns what GitHub answers into lines of
// tab-separated fields.
public sealed class GitHubTasks(IProcessRunner processes, string repository, string? board) : ITaskTracker
{
    // The queue reads this many tasks; a project with more open ones is told so, not read in part.
    const int Page = 100;

    const string Issues = """
        query($owner: String!, $name: String!, $milestone: String) { repository(owner: $owner, name: $name) { issues(states: OPEN, first: 100, orderBy: {field: CREATED_AT, direction: ASC}, filterBy: {milestoneNumber: $milestone}) { totalCount nodes { number title url labels(first: 50) { nodes { name } } blockedBy(first: 50) { nodes { number state } } subIssues(first: 50) { nodes { number state } } projectItems(first: 20) { nodes { project { url } fieldValueByName(name: "Status") { ... on ProjectV2ItemFieldSingleSelectValue { name } } } } } } } }
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
        // is the fourth field, empty for a task that is not there.
        var status = board is null
            ? "\"\""
            : $"([.projectItems.nodes[] | select((.project.url | ascii_downcase) == {Quoted(board.ToLowerInvariant())})] | if length == 0 then \"\" else (.[0].fieldValueByName.name // \"No status\") end)";
        var lines = $".data.repository.issues | \"total\\t\\(.totalCount)\", (.nodes[] | [.number, .title, .url, {status}, "
            + "([.blockedBy.nodes[] | select(.state == \"OPEN\") | .number | tostring] | join(\",\")), "
            + "([.subIssues.nodes[] | select(.state == \"OPEN\") | .number | tostring] | join(\",\"))] + [.labels.nodes[].name] | @tsv)";
        var answer = await AskAsync(
            ["api", "graphql", "-f", $"owner={owner}", "-f", $"name={name}", .. filter, "-f", $"query={Issues.Trim()}", "--jq", lines],
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
            else if (fields.Length >= 6 && int.TryParse(fields[0], NumberStyles.None, CultureInfo.InvariantCulture, out var number))
            {
                var named = Unescaped(fields[3]);
                tasks.Add(new QueueTask(number, Unescaped(fields[1]), fields[2], [.. fields.Skip(6).Select(Unescaped)], StatusOf(named), named, Numbers(fields[4]), Numbers(fields[5])));
            }
        }

        return tasks;
    }

    public async Task<TaskText> ReadTextAsync(int number, CancellationToken cancellationToken)
    {
        var answer = await AskAsync(
            ["issue", "view", Text(number), "--repo", repository, "--json", "body,comments", "--jq", "([.body] | @tsv), (.comments[] | [.author.login, .body] | @tsv)"],
            $"#{number} cannot be read.",
            cancellationToken);

        // The first line is the description, which may be empty; a line for each comment follows.
        var lines = answer.Split('\n');
        return new TaskText(
            Unescaped(lines[0].TrimEnd('\r')),
            [.. lines.Skip(1).Select(line => line.TrimEnd('\r').Split('\t', 2)).Where(fields => fields.Length == 2).Select(fields => new TaskComment(fields[0], Unescaped(fields[1])))]);
    }

    public async Task SetStatusAsync(int number, BoardStatus status, CancellationToken cancellationToken)
    {
        if (board is null || BoardParts(board) is not var (kind, login, project))
            return;

        var (owner, name) = Parts();
        var failed = $"#{number} could not be moved to {NameOf(status)} on the board.";
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
        if (rows.FirstOrDefault(row => row is ["option", _, var called] && StatusOf(called) == status)?[1] is not { } option)
            throw new TrackerException($"{failed} The board has no status “{NameOf(status)}”.");

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

    public Task AddLabelAsync(int number, string label, CancellationToken cancellationToken) =>
        AskAsync(["issue", "edit", Text(number), "--repo", repository, "--add-label", label], $"The label {label} could not be put on #{number}.", cancellationToken);

    public Task RemoveLabelAsync(int number, string label, CancellationToken cancellationToken) =>
        AskAsync(["issue", "edit", Text(number), "--repo", repository, "--remove-label", label], $"The label {label} could not be taken off #{number}.", cancellationToken);

    public Task CommentAsync(int number, string text, CancellationToken cancellationToken) =>
        AskAsync(["issue", "comment", Text(number), "--repo", repository, "--body", text], $"The comment could not be written to #{number}.", cancellationToken);

    public Task CloseAsync(int number, CancellationToken cancellationToken) =>
        AskAsync(["issue", "close", Text(number), "--repo", repository], $"#{number} could not be closed.", cancellationToken);

    public async Task<int> CreateAsync(NewTask task, CancellationToken cancellationToken)
    {
        var failed = $"The task “{task.Title}” could not be made.";
        var answer = await AskAsync(
            [
                "issue", "create", "--repo", repository, "--title", task.Title, "--body", task.Description,
                .. task.Labels.SelectMany(label => (string[])["--label", label]),
                .. task.Milestone is null ? [] : (string[])["--milestone", task.Milestone],
            ],
            failed,
            cancellationToken);

        // The tool answers with the address of the issue, which ends with its number.
        if (!int.TryParse(answer.Trim().Split('/')[^1], NumberStyles.None, CultureInfo.InvariantCulture, out var number))
            throw new TrackerException(failed);

        await SetStatusAsync(number, BoardStatus.Todo, cancellationToken);
        return number;
    }

    // What the tool prints when it does what it is asked; otherwise the tracker says that it did not.
    async Task<string> AskAsync(string[] arguments, string failed, CancellationToken cancellationToken)
    {
        var result = await processes.RunAsync("gh", arguments, Patience, cancellationToken);
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

    // A status by what a board calls it: GitHub names the statuses of a new board Todo, In Progress and Done, and
    // boards differ in the case of the letters and in the space.
    static BoardStatus StatusOf(string name) => new string([.. name.Where(char.IsLetter).Select(char.ToLowerInvariant)]) switch
    {
        "" => BoardStatus.Missing,
        "todo" => BoardStatus.Todo,
        "inprogress" => BoardStatus.InProgress,
        "done" => BoardStatus.Done,
        _ => BoardStatus.Other,
    };

    static string NameOf(BoardStatus status) => status switch
    {
        BoardStatus.Todo => "Todo",
        BoardStatus.InProgress => "In Progress",
        BoardStatus.Done => "Done",
        _ => status.ToString(),
    };

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
