using System.Globalization;
using System.Text;
using Tomlyn.Parsing;
using Tomlyn.Syntax;
using TooBusy.Core.Assistant;
using TooBusy.Core.Queue;
using TooBusy.Core.Run;
using TooBusy.Core.Settings;

namespace TooBusy.Infrastructure.Settings;

// What one user chose for the project at the given root, kept outside the project: in `projects.toml` of the user's
// own toobusy folder, a table for each project under the path of its root. toobusy writes the file whole; a file it
// cannot read counts as one that says nothing, and is replaced when a choice is saved.
public sealed class PersonalSettingsFile(string folder, string projectRoot) : IPersonalSettings
{
    const string Milestone = "milestone";
    const string Model = "model";
    const string Effort = "effort";

    // The share of the weekly limit, the only choice that is a number.
    const string Limit = "limit";

    // The choices a table holds, in the order they are written.
    static readonly string[] Keys = [Milestone, Model, Effort, Limit];

    public string Path { get; } = System.IO.Path.Combine(folder, "projects.toml");

    // The folder of the user's own settings: `$XDG_CONFIG_HOME/toobusy`, `%APPDATA%\toobusy` on Windows, and
    // `~/.config/toobusy` otherwise. Null when no such folder is known.
    public static string? FindFolder(Func<string, string?> variable, string home, bool windows)
    {
        var settings = variable("XDG_CONFIG_HOME") is { Length: > 0 } given ? given
            : windows ? variable("APPDATA")
            : home.Length > 0 ? System.IO.Path.Combine(home, ".config")
            : null;
        return string.IsNullOrEmpty(settings) ? null : System.IO.Path.Combine(settings, "toobusy");
    }

    public MilestoneChoice? LoadMilestone() => Load(Milestone) is { } title ? new MilestoneChoice(title.Length == 0 ? null : title) : null;

    public void SaveMilestone(MilestoneChoice choice) => Save(Milestone, choice.Title ?? "");

    public ModelChoice? LoadModel() => Load(Model) is { } name ? new ModelChoice(name.Length == 0 ? null : name) : null;

    public void SaveModel(ModelChoice choice) => Save(Model, choice.Name ?? "");

    // An effort that is not a level of the assistant counts as one that is not chosen.
    public string? LoadEffort() => Load(Effort) is { } effort ? ClaudeCodeOptions.FindEffort(effort) : null;

    public void SaveEffort(string effort) => Save(Effort, effort);

    // A share that is not a share of anything counts as one that is not chosen.
    public int? LoadShare() => int.TryParse(Load(Limit), NumberStyles.None, CultureInfo.InvariantCulture, out var share) && UsageShare.Is(share) ? share : null;

    public void SaveShare(int share) => Save(Limit, share.ToString(CultureInfo.InvariantCulture));

    string? Load(string key) => Read().TryGetValue(projectRoot, out var choices) ? choices.GetValueOrDefault(key) : null;

    void Save(string key, string value)
    {
        var projects = Read();
        if (!projects.TryGetValue(projectRoot, out var choices))
            projects[projectRoot] = choices = [];
        choices[key] = value;

        var text = new StringBuilder("# What you chose for each project you run toobusy in. toobusy writes this file.\n");
        text.Append("# An empty milestone means working without one; an empty model means the assistant's own.\n");
        text.Append("# The limit is how much of the weekly limit of usage a run may use, in percent.\n");
        foreach (var (root, chosen) in projects.OrderBy(project => project.Key, StringComparer.Ordinal))
        {
            text.Append("\n[").Append(SettingsToml.Quoted(root)).Append("]\n");
            foreach (var name in Keys.Where(chosen.ContainsKey))
                text.Append(name).Append(" = ").Append(name == Limit ? chosen[name] : SettingsToml.Quoted(chosen[name])).Append('\n');
        }

        Directory.CreateDirectory(folder);
        File.WriteAllText(Path, text.ToString());
    }

    // What is chosen for every project the file knows: the texts of the choices under their keys. A project with no
    // choice is left out.
    Dictionary<string, Dictionary<string, string>> Read()
    {
        var projects = new Dictionary<string, Dictionary<string, string>>(StringComparer.Ordinal);
        if (!File.Exists(Path))
            return projects;

        var syntax = SyntaxParser.Parse(File.ReadAllText(Path), Path);
        if (syntax.HasErrors)
            return projects;

        foreach (var table in syntax.Tables.Where(table => table is not TableArraySyntax && table.Name is { DotKeys.ChildrenCount: 0 }))
        {
            var root = table.Name!.Key switch
            {
                StringValueSyntax quoted => quoted.Value,
                BareKeySyntax bare => bare.Key?.Text,
                _ => null,
            };
            var chosen = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var pair in table.Items)
            {
                if (pair is not { Key: { DotKeys.ChildrenCount: 0, Key: BareKeySyntax { Key.Text: { } key } } } || !Keys.Contains(key))
                    continue;

                // A choice of the wrong kind counts as one that is not there.
                var value = pair.Value switch
                {
                    StringValueSyntax { Value: { } text } when key != Limit => text,
                    IntegerValueSyntax number when key == Limit => number.Value.ToString(CultureInfo.InvariantCulture),
                    _ => null,
                };
                if (value is not null)
                    chosen.TryAdd(key, value);
            }

            if (root is not null && chosen.Count > 0)
                projects[root] = chosen;
        }

        return projects;
    }
}
