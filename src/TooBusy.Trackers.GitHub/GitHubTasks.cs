using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using TooBusy.Core.Processes;
using TooBusy.Core.Queue;
using TooBusy.Core.Settings;

namespace TooBusy.Trackers.GitHub;

// The tasks of a project as issues of its GitHub repository, with their statuses on the board of the project when
// it has one, through the GitHub command-line tool. The tool itself turns what GitHub answers into lines of
// tab-separated fields. `statuses` are what the board calls the statuses a run knows, as the settings name them;
// without them they are called as GitHub calls them.
public sealed partial class GitHubTasks(IProcessRunner processes, string repository, string? board, BoardStatuses? statuses = null) : ITaskTracker
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

    // The longest comment that is given to the tool as an argument, in characters.
    const int LongestBody = 8 * 1024;

    // The milestone the queue was read for, by the title it was asked for, with its title as GitHub writes it and the
    // number GitHub filters the issues by. It is found once, when the queue of that title is read first.
    (string Asked, string Title, int Number)? found;

    public async Task<IReadOnlyList<QueueTask>> ReadOpenAsync(string? milestone, CancellationToken cancellationToken)
    {
        var (owner, name) = Parts();
        string[] filter = [];
        if (milestone is not null)
        {
            var (_, _, number) = found is { } known && known.Asked == milestone ? known : await FindAsync(milestone, cancellationToken);
            filter = ["-f", $"milestone={Text(number)}", "-F", $"number={Text(number)}"];
        }

        // The milestone of the queue comes first, by its number: its state and its title, both empty when GitHub has
        // no milestone of that number any more. A task is on the board when one of its project items is of that
        // board; what the board calls its status is the fourth field, empty for a task that is not there. The seventh
        // is the title of its milestone, empty for a task without one, and the labels follow it.
        var lines = ".data.repository | "
            + (milestone is null ? "" : "([\"milestone\", .milestone.state // \"\", .milestone.title // \"\"] | @tsv), ")
            + $"(.issues | \"total\\t\\(.totalCount)\", (.nodes[] | [.number, .title, .url, {Status()}, "
            + "([.blockedBy.nodes[] | select(.state == \"OPEN\") | .number | tostring] | join(\",\")), "
            + "([.subIssues.nodes[] | select(.state == \"OPEN\") | .number | tostring] | join(\",\")), "
            + "(.milestone.title // \"\")] + [.labels.nodes[].name] | @tsv))";
        var answer = await AskAsync(
            ["api", "graphql", "-f", $"owner={owner}", "-f", $"name={name}", .. filter, "-f", $"query={Issues(milestone is not null)}", "--jq", lines],
            $"The tasks of {repository} cannot be read.",
            cancellationToken);

        // A line that cannot be read is not passed over: a queue without a task that nobody knows is missing is worse
        // than a run that stops and says what it could not read, as when the tool prints otherwise than it did.
        var tasks = new List<QueueTask>();
        var unread = 0;
        var confirmed = milestone is null;
        foreach (var fields in Tsv.Lines(answer).Select(line => line.Split('\t')))
        {
            // A milestone that was closed, renamed or deleted since it was found is not open any more for the queue,
            // as it is not for the choice of the user.
            if (fields is ["milestone", var state, var title])
            {
                if (state != "OPEN" || Tsv.Unescaped(title) != found?.Title)
                    throw Gone(milestone!);
                confirmed = true;
            }
            else if (fields is ["total", var total])
            {
                if (!int.TryParse(total, NumberStyles.None, CultureInfo.InvariantCulture, out var count))
                    throw new TrackerException($"The open tasks of {repository} cannot be read: the count is `{total}`.");
                if (count > Page)
                    throw new TrackerException($"{(milestone is null ? repository : $"The milestone “{milestone}”")} has {total} open tasks, and the queue reads only the first {Page}.");
            }
            else if (fields.Length >= 7 && int.TryParse(fields[0], NumberStyles.None, CultureInfo.InvariantCulture, out var number)
                && Numbers(fields[4]) is { } blockedBy && Numbers(fields[5]) is { } parts)
            {
                var named = Tsv.Unescaped(fields[3]);
                var of = Tsv.Unescaped(fields[6]);
                tasks.Add(new QueueTask(number, Tsv.Unescaped(fields[1]), fields[2], [.. fields.Skip(7).Select(Tsv.Unescaped)], statuses.Of(named), named, blockedBy, parts, of.Length == 0 ? null : of));
            }
            else
            {
                unread++;
            }
        }

        if (!confirmed)
            throw Gone(milestone!);
        if (unread > 0)
            throw new TrackerException($"The open tasks of {repository} cannot be read: `gh` printed {(unread == 1 ? "a line" : $"{Text(unread)} lines")} that cannot be read as {(unread == 1 ? "a task" : "tasks")}.");

        return tasks;
    }

    // The open milestone of the title, by the one reading of the milestones and as the choice of the user is checked:
    // as it is written, or, when no title is written so, the only one that differs in the case of its letters.
    async Task<(string Asked, string Title, int Number)> FindAsync(string milestone, CancellationToken cancellationToken)
    {
        var open = GitHubMilestones.Read(await AskAsync(GitHubMilestones.Request(repository), $"The milestones of {repository} cannot be read.", cancellationToken));
        if (MilestoneStanding.Find([.. open.Select(listed => listed.Milestone)], milestone) is not { } chosen)
            throw Gone(milestone);

        found = (milestone, chosen.Title, open.First(listed => ReferenceEquals(listed.Milestone, chosen)).Number);
        return found.Value;
    }

    // A milestone that is not open any more is found anew when it is asked for again.
    TrackerException Gone(string milestone)
    {
        found = null;
        return new TrackerException($"The milestone “{milestone}” is not open any more.");
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

        var fields = Tsv.Lines(Answer(result, $"#{number} cannot be read.")).FirstOrDefault()?.Split('\t');
        if (fields is not { Length: >= 5 } || !int.TryParse(fields[0], NumberStyles.None, CultureInfo.InvariantCulture, out var read))
            return null;

        var named = Tsv.Unescaped(fields[3]);
        var of = Tsv.Unescaped(fields[4]);
        return new QueueTask(read, Tsv.Unescaped(fields[1]), fields[2], [.. fields.Skip(5).Select(Tsv.Unescaped)], statuses.Of(named), named, [], [], of.Length == 0 ? null : of);
    }

    // What the board of the project calls the status of a task, as the filter of the tool reads it from the task:
    // empty for a task that is not there, and in a project without a board.
    string Status() => board is null
        ? "\"\""
        : $"([.projectItems.nodes[] | select((.project.url | ascii_downcase) == {Quoted(board.ToLowerInvariant())})] | if length == 0 then \"\" else (.[0].fieldValueByName.name // \"No status\") end)";

    // The query of the queue, which asks for its milestone by its number when it is of one, and for the boards of a
    // task only when the project has one.
    string Issues(bool milestone) =>
        "query($owner: String!, $name: String!, $milestone: String" + (milestone ? ", $number: Int!" : "") + ") { repository(owner: $owner, name: $name) { "
        + (milestone ? "milestone(number: $number) { state title } " : "")
        + "issues(states: OPEN, first: 100, orderBy: {field: CREATED_AT, direction: ASC}, filterBy: {milestoneNumber: $milestone}) { totalCount nodes { number title url milestone { title } labels(first: 50) { nodes { name } } blockedBy(first: 50) { nodes { number state } } subIssues(first: 50) { nodes { number state } }"
        + (board is null ? "" : " " + Statuses.Trim())
        + " } } } }";

    public async Task SetStatusAsync(int number, BoardStatus status, CancellationToken cancellationToken)
    {
        if (board is null || BoardAddress.Parse(board) is not var (organisation, login, project))
            return;

        var kind = GitHubCli.Owner(organisation);

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

        var rows = Tsv.Lines(read).Select(line => line.Split('\t')).ToList();
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
        AskAsync(["issue", "edit", Text(number), "--repo", repository, "--add-label", Listed(label)], $"The label {label} could not be put on #{number}.", cancellationToken);

    public Task RemoveLabelAsync(int number, string label, CancellationToken cancellationToken) =>
        AskAsync(["issue", "edit", Text(number), "--repo", repository, "--remove-label", Listed(label)], $"The label {label} could not be taken off #{number}.", cancellationToken);

    // A label as the tool reads it among the labels of an option, which it takes as a line of comma-separated values:
    // a name with a comma or a quote is quoted as such a value is, so that it stays one name.
    static string Listed(string label) =>
        label.Contains(',', StringComparison.Ordinal) || label.Contains('"', StringComparison.Ordinal) ? "\"" + label.Replace("\"", "\"\"", StringComparison.Ordinal) + "\"" : label;

    // A long comment, as the report of a session may be, goes to the tool in a file: the system limits an argument, as
    // Linux does to 128 KiB. The file is made as the system makes its temporary files, for the user alone, and is
    // deleted once the tool is done with it.
    public async Task CommentAsync(int number, string text, CancellationToken cancellationToken)
    {
        var failed = $"The comment could not be written to #{number}.";
        if (text.Length <= LongestBody)
        {
            await AskAsync(["issue", "comment", Text(number), "--repo", repository, "--body", text], failed, cancellationToken);
            return;
        }

        var file = "";
        try
        {
            try
            {
                file = Path.GetTempFileName();
                File.WriteAllText(file, text);
            }
            catch (Exception unwritten) when (unwritten is IOException or UnauthorizedAccessException)
            {
                throw new TrackerException($"{failed} {unwritten.Message}");
            }

            await AskAsync(["issue", "comment", Text(number), "--repo", repository, "--body-file", file], failed, cancellationToken);
        }
        finally
        {
            Delete(file);
        }
    }

    public Task CloseAsync(int number, CancellationToken cancellationToken) =>
        AskAsync(["issue", "close", Text(number), "--repo", repository], $"#{number} could not be closed.", cancellationToken);

    // What the tool prints when it does what it is asked; otherwise the tracker says that it did not, and whether
    // that may pass.
    async Task<string> AskAsync(string[] arguments, string failed, CancellationToken cancellationToken) =>
        Answer(await processes.RunAsync("gh", arguments, Patience, cancellationToken), failed);

    static string Answer(ProcessResult result, string failed)
    {
        if (result is { Status: ProcessStatus.Exited, ExitCode: 0 })
            return result.Output;

        var said = result.Error.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).FirstOrDefault();
        throw new TrackerException(
            result.Status != ProcessStatus.Exited ? $"{failed} {GitHubCli.Unanswered(result)}" : said is null ? failed : $"{failed} {said}",
            result.Status == ProcessStatus.TimedOut || (result.Status == ProcessStatus.Exited && Passing(result.Error)));
    }

    // Whether what the tool said of a failure tells one that may pass. GitHub answered with a status of the 500s, a
    // 429, or a 403 that names a rate limit; a rate limit of a query has no status. Or the tool did not reach GitHub.
    // Every other status, as of a task that does not exist or a token without the scope, stays, and so does an error
    // of a query, as of a label the repository does not have, and a login that is missing. What the tool quotes is
    // what it was given, as the name of a label, and is not read: a label called `timeout` is no timeout.
    static bool Passing(string said)
    {
        var error = Quotation().Replace(said, "");
        var limited = error.Contains("rate limit", StringComparison.OrdinalIgnoreCase);
        if (HttpStatus().Match(error) is { Success: true } status)
            return int.Parse(status.Groups[1].ValueSpan, CultureInfo.InvariantCulture) switch
            {
                >= 500 or 429 => true,
                403 => limited,
                _ => false,
            };

        return limited || Unreached.Any(words => error.Contains(words, StringComparison.OrdinalIgnoreCase));
    }

    // What the tool says when it does not reach GitHub: the name of the host is not found, the connection is refused
    // or cut, or it takes too long.
    static readonly string[] Unreached =
    [
        "error connecting to", "no such host", "connection refused", "connection reset", "network is unreachable",
        "timeout", "timed out", "deadline exceeded", "unexpected EOF",
    ];

    // The status GitHub answered with, as the tool says it: `(HTTP 502)`, or `HTTP 502: Bad Gateway`.
    [GeneratedRegex(@"\bHTTP ([1-5][0-9]{2})\b")]
    private static partial Regex HttpStatus();

    // A text between quotes, as `'needs owner'` or `"https://api.github.com/graphql"`; the apostrophe of a word, as of
    // `couldn't`, opens none.
    [GeneratedRegex(@"(?<!\w)'[^'\n]*'|""[^""\n]*""")]
    private static partial Regex Quotation();

    (string Owner, string Name) Parts() =>
        repository.Split('/') is [var owner, var name] ? (owner, name) : throw new TrackerException($"{repository} is not a GitHub repository.");

    // The numbers of a field, as `4,9`; null when one of them is not a number.
    static List<int>? Numbers(string text)
    {
        var numbers = new List<int>();
        foreach (var part in text.Split(',', StringSplitOptions.RemoveEmptyEntries))
        {
            if (!int.TryParse(part, NumberStyles.None, CultureInfo.InvariantCulture, out var number))
                return null;
            numbers.Add(number);
        }

        return numbers;
    }

    static string Text(int number) => number.ToString(CultureInfo.InvariantCulture);

    // A temporary file that cannot be deleted is left where the system keeps them.
    static void Delete(string file)
    {
        try
        {
            if (file.Length > 0)
                File.Delete(file);
        }
        catch (Exception kept) when (kept is IOException or UnauthorizedAccessException)
        {
        }
    }

    // A text as the filter of the tool writes one.
    static string Quoted(string text)
    {
        var quoted = new StringBuilder("\"");
        foreach (var character in text)
            quoted.Append(character is '"' or '\\' ? "\\" + character : character.ToString());
        return quoted.Append('"').ToString();
    }
}
