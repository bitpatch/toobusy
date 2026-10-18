using System.Text.RegularExpressions;
using TooBusy.Core.Queue;

namespace TooBusy.Core.Settings;

// The validation rules of the settings. They look at the file alone: whether the board,
// and the labels exist is checked elsewhere, against the tracker.
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

    public static bool IsBoard(string value) => BoardPattern().IsMatch(value);

    // Null when the text is the name of a skill, as a session is started with it; otherwise why it is not.
    public static string? RefuseSkill(string value) => SkillPattern().IsMatch(value)
        ? null
        : "must be the name of a skill, as `task-auto`: letters, digits, `-`, `_`, `.` and `:`, without the `/` before it";

    // Null when the text is the name of a branch as git takes one; otherwise why it is not.
    public static string? RefuseBranch(string value) => BranchPattern().IsMatch(value)
        ? null
        : "must be the name of a branch as git takes it, as `develop`: no space, none of `~^:?*[\\`, no `..` and no `@{`, and neither `/` nor `.` at either end";

    [GeneratedRegex(@"^https://github\.com/(orgs|users)/[A-Za-z0-9-]+/projects/[0-9]+$")]
    private static partial Regex BoardPattern();

    [GeneratedRegex(@"^[A-Za-z0-9][A-Za-z0-9_.:-]*$")]
    private static partial Regex SkillPattern();

    // The rules of `git check-ref-format --branch`: no character git keeps for itself, no part of the name that
    // starts with a dot or ends with `.lock`, and not `@` or `HEAD` alone.
    [GeneratedRegex(@"^(?![-/.])(?!(?:@|HEAD)$)(?!.*(?:\.\.|//|@\{|/\.|\.lock/|\.lock$))[^\x00-\x20\x7F~^:?*\[\\]+(?<![./])$")]
    private static partial Regex BranchPattern();

    sealed class Run(SettingsDocument document)
    {
        static readonly string[] Sections = ["tracker", "tracker.statuses", "queue", "queue.labels", "assistant", "session", "task", "workspace"];

        readonly Dictionary<string, SettingsEntry> entries = [];

        public List<SettingsError> Errors { get; } = [];

        public ProjectSettings Read()
        {
            Index();
            ReadVersion();

            var trackerType = OneOf(SettingsKeys.TrackerType, [SettingsKeys.GitHubTracker]);
            var board = String(SettingsKeys.TrackerBoard, required: false);
            if (board is not null && !IsBoard(board.Value.Text))
            {
                Refuse(board.Value.Entry,
                    "must be the address of a GitHub Projects board: `https://github.com/orgs/<org>/projects/<n>` or `https://github.com/users/<user>/projects/<n>`");
            }

            var statuses = ReadStatuses(board is not null);
            var labels = ReadLabels();
            var assistantType = OneOf(SettingsKeys.AssistantType, [SettingsKeys.ClaudeCodeAssistant]);
            var skill = String(SettingsKeys.SessionSkill, required: false);
            if (skill is not null && RefuseSkill(skill.Value.Text) is { } reason)
                Refuse(skill.Value.Entry, reason);
            var branch = String(SettingsKeys.WorkspaceBranch, required: false);
            if (branch is not null && RefuseBranch(branch.Value.Text) is { } wrong)
                Refuse(branch.Value.Entry, wrong);

            return new ProjectSettings(
                new TrackerSettings(trackerType ?? "", board?.Text) { Statuses = statuses },
                new QueueSettings(labels),
                new AssistantSettings(assistantType ?? ""))
            {
                Skill = skill?.Text,
                Commands = new TaskCommands(Command(SettingsKeys.TaskBefore), Command(SettingsKeys.TaskAfter)),
                Branch = branch?.Text,
            };
        }

        // A command is a line for the shell, and nothing of it is looked at but that there is one.
        string? Command(string key)
        {
            if (String(key, required: false) is not { } value)
                return null;
            if (!string.IsNullOrWhiteSpace(value.Text))
                return value.Text;
            Refuse(value.Entry, "a command must not be empty: leave the key out to run none");
            return null;
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

        // What the board calls the three statuses a run knows. A name that is not given is the one GitHub gives,
        // and two of the three must not be one status: a run would take a task from where it moves it to.
        BoardStatuses ReadStatuses(bool board)
        {
            string[] keys = [SettingsKeys.TodoStatus, SettingsKeys.InProgressStatus, SettingsKeys.DoneStatus];
            var given = BoardStatuses.Default.Names;
            var names = new string[keys.Length];
            for (var index = 0; index < keys.Length; index++)
            {
                names[index] = given[index];
                if (String(keys[index], required: false) is not { } value)
                    continue;
                if (string.IsNullOrWhiteSpace(value.Text))
                    Refuse(value.Entry, "the name of a status must not be empty: leave the key out for the name GitHub gives");
                else if (!board)
                    Refuse(value.Entry, $"names a status of a board, and `{SettingsKeys.TrackerBoard}` names no board");
                else
                    names[index] = value.Text;
            }

            for (var index = 1; index < keys.Length; index++)
            {
                if (Enumerable.Range(0, index).FirstOrDefault(before => BoardStatuses.Same(names[before], names[index]), -1) is >= 0 and var same)
                {
                    // The error stands at a key the file has: the later of the two, or the earlier one.
                    var message = $"`{keys[same]}` and `{keys[index]}` must not name one status: both are `{names[index]}`";
                    if (entries.TryGetValue(keys[index], out var later) || entries.TryGetValue(keys[same], out later))
                        Refuse(later, message);
                }
            }

            return new BoardStatuses(names[0], names[1], names[2]);
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

            // The labels a run puts on a task must not make it a task to take, and a task that was interrupted
            // must not be kept out of the next run.
            var owner = Label(SettingsKeys.OwnerLabel);
            var interrupted = Label(SettingsKeys.InterruptedLabel);
            if (owner is not null && Has(take, owner))
                Refuse(entries[SettingsKeys.OwnerLabel], $"the label `{owner}` is in `{SettingsKeys.TakeLabels}` too");
            if (interrupted is not null && Has(take, interrupted))
                Refuse(entries[SettingsKeys.InterruptedLabel], $"the label `{interrupted}` is in `{SettingsKeys.TakeLabels}` too");
            if (interrupted is not null && Has(blocking, interrupted))
                Refuse(entries[SettingsKeys.InterruptedLabel], $"the label `{interrupted}` is in `{SettingsKeys.BlockingLabels}` too");
            if (owner is not null && interrupted is not null && owner.Equals(interrupted, StringComparison.OrdinalIgnoreCase))
                Refuse(entries[SettingsKeys.InterruptedLabel], $"must not be the label of `{SettingsKeys.OwnerLabel}`");

            return new LabelSettings(blocking ?? [], take ?? [], owner ?? "", interrupted ?? "");
        }

        static bool Has(string[]? labels, string label) => labels is not null && labels.Contains(label, StringComparer.OrdinalIgnoreCase);

        string? Label(string key)
        {
            if (String(key, required: true) is not { } value)
                return null;
            if (!string.IsNullOrWhiteSpace(value.Text))
                return value.Text;
            Refuse(value.Entry, "a label name must not be empty");
            return null;
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
