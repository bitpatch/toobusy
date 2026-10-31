using System.Globalization;
using TooBusy.Core.Processes;
using TooBusy.Core.Queue;

namespace TooBusy.Trackers.GitHub;

// The open milestones of a repository, as the GitHub command-line tool gives them. The tool itself turns the answer
// into lines of tab-separated fields: the number, the title, the due date when there is one, and the number of open
// issues. It is the one reading of the milestones: the queue finds the number of its milestone by it too.
public sealed class GitHubMilestones(IProcessRunner processes) : IMilestones
{
    const string Lines = """.[] | [.number, .title, .due_on // "", .open_issues] | @tsv""";

    // A screen waits for the list, so a slow answer is given up.
    static readonly TimeSpan Patience = TimeSpan.FromSeconds(5);

    public async Task<IReadOnlyList<Milestone>?> ReadOpenAsync(string repository, CancellationToken cancellationToken)
    {
        var result = await processes.RunAsync("gh", Request(repository), Patience, cancellationToken);
        return result is { Status: ProcessStatus.Exited, ExitCode: 0 } ? [.. Read(result.Output).Select(open => open.Milestone)] : null;
    }

    // What the tool is asked for the open milestones of the repository.
    internal static string[] Request(string repository) =>
        ["api", $"repos/{repository}/milestones?state=open&per_page=100", "--jq", Lines];

    // The milestones in what the tool printed, each with the number GitHub knows it by; a line that is not one is
    // passed over.
    internal static IReadOnlyList<(int Number, Milestone Milestone)> Read(string output)
    {
        var milestones = new List<(int, Milestone)>();
        foreach (var fields in Tsv.Lines(output).Select(line => line.Split('\t')))
        {
            if (fields is not [var number, { Length: > 0 } title, var due, var open] || !int.TryParse(number, NumberStyles.None, CultureInfo.InvariantCulture, out var known))
                continue;

            milestones.Add((known, new Milestone(
                Tsv.Unescaped(title),
                due.Length >= 10 && DateOnly.TryParseExact(due[..10], "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var day) ? day : null,
                int.TryParse(open, NumberStyles.None, CultureInfo.InvariantCulture, out var tasks) ? tasks : 0)));
        }

        return milestones;
    }
}
