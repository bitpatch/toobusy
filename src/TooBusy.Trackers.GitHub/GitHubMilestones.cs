using System.Globalization;
using TooBusy.Core.Processes;
using TooBusy.Core.Queue;

namespace TooBusy.Trackers.GitHub;

// The open milestones of a repository, as the GitHub command-line tool gives them. The tool itself turns the answer
// into lines of tab-separated fields: the title, the due date when there is one, and the number of open issues.
public sealed class GitHubMilestones(IProcessRunner processes) : IMilestones
{
    const string Lines = """.[] | "\(.title)\t\(.due_on // "")\t\(.open_issues)" """;

    // A screen waits for the list, so a slow answer is given up.
    static readonly TimeSpan Patience = TimeSpan.FromSeconds(5);

    public async Task<IReadOnlyList<Milestone>?> ReadOpenAsync(string repository, CancellationToken cancellationToken)
    {
        var result = await processes.RunAsync(
            "gh", ["api", $"repos/{repository}/milestones?state=open&per_page=100", "--jq", Lines.Trim()], Patience, cancellationToken);
        if (result is not { Status: ProcessStatus.Exited, ExitCode: 0 })
            return null;

        return
        [
            .. result.Output.Split('\n', StringSplitOptions.RemoveEmptyEntries)
                .Select(line => line.TrimEnd('\r').Split('\t'))
                .Where(fields => fields.Length == 3 && fields[0].Length > 0)
                .Select(fields => new Milestone(
                    fields[0],
                    fields[1].Length >= 10 && DateOnly.TryParseExact(fields[1][..10], "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var due) ? due : null,
                    int.TryParse(fields[2], NumberStyles.None, CultureInfo.InvariantCulture, out var open) ? open : 0)),
        ];
    }
}
