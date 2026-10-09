using System.Text.RegularExpressions;

namespace TooBusy.Core.Settings;

// The validation rules of the settings. They look at the file alone: whether the repository, the board,
// the labels and the milestone exist is checked elsewhere, against the tracker.
public static partial class SettingsValidator
{
    public static SettingsValidation Validate(SettingsDocument document)
    {
        var run = new Run(document);
        var settings = run.Read();
        return run.Errors.Count == 0
            ? new SettingsValidation(settings, [])
            : new SettingsValidation(null, [.. run.Errors.OrderBy(error => error.Line ?? int.MaxValue)]);
    }

    public static bool IsRepository(string value) => RepositoryPattern().IsMatch(value);

    public static bool IsBoard(string value) => BoardPattern().IsMatch(value);

    [GeneratedRegex(@"^[A-Za-z0-9-]+/[A-Za-z0-9._-]+$")]
    private static partial Regex RepositoryPattern();

    [GeneratedRegex(@"^https://github\.com/(orgs|users)/[A-Za-z0-9-]+/projects/[0-9]+$")]
    private static partial Regex BoardPattern();

    sealed class Run(SettingsDocument document)
    {
        static readonly string[] Sections = ["tracker", "queue", "queue.milestone", "queue.labels", "assistant"];

        readonly Dictionary<string, SettingsEntry> entries = [];

        public List<SettingsError> Errors { get; } = [];

        public ProjectSettings Read()
        {
            Index();
            ReadVersion();

            var trackerType = OneOf(SettingsKeys.TrackerType, [SettingsKeys.GitHubTracker]);
            var repository = String(SettingsKeys.TrackerRepository, required: true);
            if (repository is not null && !IsRepository(repository.Value.Text))
                Refuse(repository.Value.Entry, "must be a repository as `owner/name`");
            var board = String(SettingsKeys.TrackerBoard, required: false);
            if (board is not null && !IsBoard(board.Value.Text))
            {
                Refuse(board.Value.Entry,
                    "must be the address of a GitHub Projects board: `https://github.com/orgs/<org>/projects/<n>` or `https://github.com/users/<user>/projects/<n>`");
            }

            var milestone = ReadMilestone();
            var labels = ReadLabels();
            var assistantType = OneOf(SettingsKeys.AssistantType, [SettingsKeys.ClaudeCodeAssistant]);

            return new ProjectSettings(
                new TrackerSettings(trackerType ?? "", repository?.Text ?? "", board?.Text),
                new QueueSettings(milestone, labels),
                new AssistantSettings(assistantType ?? ""));
        }

        // Everything the file has and the settings do not know is refused here, so that the rules below
        // meet known keys only. A key inside an unknown section is covered by the error of its section.
        void Index()
        {
            var unknownSections = new List<string>();
            foreach (var section in document.Sections)
            {
                if (Sections.Contains(section.Name) || unknownSections.Contains(section.Name))
                    continue;
                unknownSections.Add(section.Name);
                Errors.Add(new SettingsError(section.Name, section.Line, "unknown section"));
            }

            foreach (var entry in document.Entries)
            {
                if (unknownSections.Exists(section => entry.Key.StartsWith(section + ".", StringComparison.Ordinal)))
                    continue;
                if (!SettingsKeys.All.Contains(entry.Key))
                    Refuse(entry, "unknown key");
                else if (!entries.TryAdd(entry.Key, entry))
                    Refuse(entry, "is set twice");
            }
        }

        void ReadVersion()
        {
            if (Find(SettingsKeys.Version, required: true) is not { } entry)
                return;
            if (entry.Value is not long version)
                Refuse(entry, "must be a whole number");
            else if (version > ProjectSettings.SchemaVersion)
                Refuse(entry, $"the file was made by a newer toobusy: its version is {version}, and this one reads version {ProjectSettings.SchemaVersion}");
            else if (version != ProjectSettings.SchemaVersion)
                Refuse(entry, $"must be {ProjectSettings.SchemaVersion}");
        }

        MilestoneSettings ReadMilestone()
        {
            var title = String(SettingsKeys.MilestoneTitle, required: false);
            MilestoneRule? rule = null;
            if (String(SettingsKeys.MilestoneRule, required: true) is { } named)
            {
                rule = SettingsKeys.RuleNamed(named.Text);
                if (rule is null)
                    Refuse(named.Entry, "must be one of `lowest-version`, `earliest-due`, `fixed`, `none`");
            }

            if (title is not null && string.IsNullOrWhiteSpace(title.Value.Text))
                Refuse(title.Value.Entry, "must not be empty");
            else if (title is not null && rule is not null and not MilestoneRule.Fixed)
                Refuse(title.Value.Entry, $"is for the `fixed` rule only; the rule is `{SettingsKeys.NameOf(rule.Value)}`");
            else if (title is null && rule is MilestoneRule.Fixed && !entries.ContainsKey(SettingsKeys.MilestoneTitle))
                Missing(SettingsKeys.MilestoneTitle, "is missing; the `fixed` rule needs the title of its milestone");

            return new MilestoneSettings(rule ?? MilestoneRule.None, title?.Text);
        }

        LabelSettings ReadLabels()
        {
            var blocking = Labels(SettingsKeys.BlockingLabels);
            var take = Labels(SettingsKeys.TakeLabels);
            if (blocking is not null && take is not null)
            {
                foreach (var label in take.Intersect(blocking, StringComparer.OrdinalIgnoreCase))
                    Refuse(entries[SettingsKeys.TakeLabels], $"the label `{label}` is in `{SettingsKeys.BlockingLabels}` too");
            }

            return new LabelSettings(blocking ?? [], take ?? []);
        }

        string[]? Labels(string key)
        {
            if (Find(key, required: true) is not { } entry)
                return null;
            if (entry.Value is not IReadOnlyList<object> items || items.Any(item => item is not string))
            {
                Refuse(entry, "must be a list of label names");
                return null;
            }

            var labels = items.Cast<string>().ToArray();
            if (labels.Any(string.IsNullOrWhiteSpace))
            {
                Refuse(entry, "a label name must not be empty");
                return null;
            }

            return labels;
        }

        string? OneOf(string key, string[] allowed)
        {
            if (String(key, required: true) is not { } value)
                return null;
            if (allowed.Contains(value.Text))
                return value.Text;
            Refuse(value.Entry, $"must be {string.Join(" or ", allowed.Select(name => $"`{name}`"))}");
            return null;
        }

        (SettingsEntry Entry, string Text)? String(string key, bool required)
        {
            if (Find(key, required) is not { } entry)
                return null;
            if (entry.Value is string text)
                return (entry, text);
            Refuse(entry, "must be a string");
            return null;
        }

        SettingsEntry? Find(string key, bool required)
        {
            if (entries.TryGetValue(key, out var entry))
                return entry;
            if (required)
                Missing(key, "is missing");
            return null;
        }

        void Refuse(SettingsEntry entry, string message) => Errors.Add(new SettingsError(entry.Key, entry.Line, message));

        // A missing key has no line of its own: it gets the line of its section when the file has that section.
        void Missing(string key, string message)
        {
            var section = key.Contains('.', StringComparison.Ordinal) ? key[..key.LastIndexOf('.')] : null;
            var line = document.Sections.FirstOrDefault(candidate => candidate.Name == section)?.Line;
            Errors.Add(new SettingsError(key, line, message));
        }
    }
}

// Settings is null exactly when there are errors.
public sealed record SettingsValidation(ProjectSettings? Settings, IReadOnlyList<SettingsError> Errors);
