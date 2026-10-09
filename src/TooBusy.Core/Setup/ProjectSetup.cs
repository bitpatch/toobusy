using TooBusy.Core.Queue;
using TooBusy.Core.Settings;

namespace TooBusy.Core.Setup;

public enum SetupOutcome
{
    Written,
    NothingToChange,
    Declined,
}

// The steps of `toobusy init`: it asks what the settings need, shows what would change in the file, and saves it.
// With settings that exist every question proposes the current value, so that accepting them all changes nothing.
public sealed class ProjectSetup(ISetupDialog dialog, ISetupEnvironment environment, ISetupTracker tracker, ISettingsStore store)
{
    public async Task<SetupOutcome> RunAsync(CancellationToken cancellationToken = default)
    {
        var machine = await environment.InspectAsync(cancellationToken);
        foreach (var problem in machine.Problems)
        {
            dialog.Say(SetupTone.Failure, problem.Text);
            dialog.Say(SetupTone.Muted, $"fix: {problem.Fix}");
        }

        var verified = machine.TrackerReachable;
        if (!verified)
            dialog.Say(SetupTone.Warning, "GitHub cannot be read from here: the answers are taken as typed and nothing is verified.");

        var loaded = store.Load();
        if (loaded is { Settings: null })
        {
            dialog.Say(SetupTone.Warning, "The settings that exist do not validate:");
            foreach (var error in loaded.Errors)
                dialog.Say(SetupTone.Failure, error.Describe(store.DisplayPath));
        }

        var current = loaded?.Settings;
        dialog.Answered("Tracker", "GitHub");
        var repository = await AskRepositoryAsync(current?.Tracker.Repository ?? machine.OriginRepository ?? "", verified, cancellationToken);
        var board = await AskBoardAsync(current?.Tracker.Board ?? "", verified, cancellationToken);
        var milestone = await AskMilestoneAsync(current?.Queue.Milestone, repository, verified, cancellationToken);
        var labels = await AskLabelsAsync(current?.Queue.Labels, repository, verified, cancellationToken);
        dialog.Answered("Assistant", "Claude Code");

        var settings = new ProjectSettings(
            new TrackerSettings(SettingsKeys.GitHubTracker, repository, board),
            new QueueSettings(milestone, labels),
            new AssistantSettings(SettingsKeys.ClaudeCodeAssistant));
        return Conclude(settings);
    }

    async Task<string> AskRepositoryAsync(string proposed, bool verified, CancellationToken cancellationToken)
    {
        while (true)
        {
            var answer = RepositoryOf(dialog.Ask("Repository", "owner/name or a GitHub URL", proposed, "", text =>
                SettingsValidator.IsRepository(RepositoryOf(text)) ? null : "this is neither `owner/name` nor a GitHub URL"));
            if (!verified || await tracker.RefuseRepositoryAsync(answer, cancellationToken) is not { } reason)
                return answer;
            dialog.Say(SetupTone.Failure, reason);
            proposed = answer;
        }
    }

    async Task<string?> AskBoardAsync(string proposed, bool verified, CancellationToken cancellationToken)
    {
        while (true)
        {
            var answer = dialog.Ask("Board", "the URL of a GitHub Projects board; empty for none", proposed, "none", text =>
                text.Trim().Length == 0 || SettingsValidator.IsBoard(text.Trim()) ? null : "this is not the URL of a GitHub Projects board").Trim();
            if (answer.Length == 0)
                return null;
            if (!verified || await tracker.RefuseBoardAsync(answer, cancellationToken) is not { } reason)
                return answer;
            dialog.Say(SetupTone.Failure, reason);
            proposed = answer;
        }
    }

    async Task<MilestoneSettings> AskMilestoneAsync(MilestoneSettings? current, string repository, bool verified, CancellationToken cancellationToken)
    {
        IReadOnlyList<Milestone> open = verified ? await tracker.ReadOpenMilestonesAsync(repository, cancellationToken) : [];
        string Now(MilestoneRule rule) => !verified
            ? ""
            : MilestoneRules.Choose(new MilestoneSettings(rule, null), open) is { } milestone ? $" → {milestone.Title}" : " → no open milestone fits";

        MilestoneRule[] rules = [MilestoneRule.LowestVersion, MilestoneRule.EarliestDue, MilestoneRule.Fixed, MilestoneRule.None];
        SetupOption[] options =
        [
            new(SettingsKeys.NameOf(MilestoneRule.LowestVersion), "the open milestone with the lowest version" + Now(MilestoneRule.LowestVersion)),
            new(SettingsKeys.NameOf(MilestoneRule.EarliestDue), "the open milestone that is due first" + Now(MilestoneRule.EarliestDue)),
            new(SettingsKeys.NameOf(MilestoneRule.Fixed), "one milestone that you name"),
            new(SettingsKeys.NameOf(MilestoneRule.None), "no milestone: tasks of the whole repository"),
        ];

        var proposed = current?.Rule
            ?? (open.Any(milestone => MilestoneRules.VersionOf(milestone.Title) is not null) ? MilestoneRule.LowestVersion
                : open.Any(milestone => milestone.Due is not null) ? MilestoneRule.EarliestDue
                : MilestoneRule.None);
        var rule = rules[dialog.Choose("Milestone rule", options, Array.IndexOf(rules, proposed))];
        if (rule != MilestoneRule.Fixed)
            return new MilestoneSettings(rule, null);

        if (open.Count == 0)
        {
            var typed = dialog.Ask("Milestone", "the title of the milestone", current?.Title ?? "", "", text =>
                string.IsNullOrWhiteSpace(text) ? "the `fixed` rule needs the title of its milestone" : null);
            return new MilestoneSettings(rule, typed.Trim());
        }

        var titles = open.Select(milestone => milestone.Title).ToList();
        var chosen = dialog.Choose(
            "Milestone",
            [.. open.Select(milestone => new SetupOption(milestone.Title, milestone.Due is { } due ? $"due {due:yyyy-MM-dd}" : ""))],
            Math.Max(0, titles.IndexOf(current?.Title ?? "")));
        return new MilestoneSettings(rule, titles[chosen]);
    }

    async Task<LabelSettings> AskLabelsAsync(LabelSettings? current, string repository, bool verified, CancellationToken cancellationToken)
    {
        if (!verified)
        {
            var typedBlocking = Names(dialog.Ask("Blocking labels", "names separated by commas; a task with any of them is never taken",
                string.Join(", ", current?.Blocking ?? []), "none", _ => null));
            var typedTake = Names(dialog.Ask("Labels to take", "names separated by commas; empty means any task",
                string.Join(", ", current?.Take ?? []), "any task", text =>
                    Names(text).Intersect(typedBlocking, StringComparer.OrdinalIgnoreCase).FirstOrDefault() is { } shared ? $"`{shared}` is a blocking label" : null));
            return new LabelSettings(typedBlocking, typedTake);
        }

        // Labels of the settings stay among the choices even when the repository lost them:
        // accepting what is proposed must not change the file.
        var known = await tracker.ReadLabelsAsync(repository, cancellationToken);
        var all = known.Concat(current?.Blocking ?? []).Concat(current?.Take ?? []).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        var blocking = Pick("Blocking labels", all, current?.Blocking ?? [], "none");
        var rest = all.Except(blocking, StringComparer.OrdinalIgnoreCase).ToList();
        var take = Pick("Labels to take", rest, current?.Take ?? [], "any task");
        return new LabelSettings(blocking, take);
    }

    List<string> Pick(string label, List<string> options, IReadOnlyList<string> current, string whenNone)
    {
        var proposed = Enumerable.Range(0, options.Count).Where(index => current.Contains(options[index], StringComparer.OrdinalIgnoreCase)).ToList();
        return [.. dialog.ChooseMany(label, options, proposed, whenNone).Select(index => options[index])];
    }

    SetupOutcome Conclude(ProjectSettings settings)
    {
        var preview = store.Preview(settings);
        if (preview.Before == preview.After)
        {
            dialog.Say(SetupTone.Plain, "Nothing to change");
            return SetupOutcome.NothingToChange;
        }

        dialog.Say(SetupTone.Plain, "");
        dialog.Say(SetupTone.Plain, preview.Before.Length == 0 ? $"New file {store.DisplayPath}:" : $"Changes in {store.DisplayPath}:");
        foreach (var line in LineDiff.Changes(preview.Before, preview.After))
            dialog.Say(line.Added ? SetupTone.Added : SetupTone.Removed, $"{(line.Added ? '+' : '-')} {line.Text}".TrimEnd());
        dialog.Say(SetupTone.Plain, "");

        if (!dialog.Confirm("Write the settings?"))
            return SetupOutcome.Declined;
        store.Save(settings);
        return SetupOutcome.Written;
    }

    // `owner/name` out of what people paste: the address of the repository's page or of its clone.
    static string RepositoryOf(string answer)
    {
        var text = answer.Trim();
        foreach (var prefix in (string[])["https://github.com/", "http://github.com/", "ssh://git@github.com/", "git@github.com:"])
        {
            if (text.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                text = text[prefix.Length..];
        }

        text = text.TrimEnd('/');
        return text.EndsWith(".git", StringComparison.Ordinal) ? text[..^4] : text;
    }

    static List<string> Names(string text) =>
        [.. text.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries).Distinct(StringComparer.OrdinalIgnoreCase)];
}
