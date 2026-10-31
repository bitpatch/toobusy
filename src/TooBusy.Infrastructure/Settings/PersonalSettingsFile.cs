using System.Globalization;
using System.Text;
using Tomlyn.Parsing;
using Tomlyn.Syntax;
using TooBusy.Core.Assistant;
using TooBusy.Core.Queue;
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
    const string Sessions = "sessions";

    // The choices a table holds, in the order they are written.
    static readonly string[] Keys = [Milestone, Model, Effort, Sessions];

    // A toobusy that another one holds the lock of the file against waits a moment, as many times as this, two seconds
    // in all: saving a choice takes a few milliseconds.
    const int Waits = 100;
    static readonly TimeSpan Moment = TimeSpan.FromMilliseconds(20);

    public string Path { get; } = System.IO.Path.Combine(folder, "projects.toml");

    // The folder of the user's own settings: `$XDG_CONFIG_HOME/toobusy`, and `~/.config/toobusy` otherwise. Null when
    // no such folder is known.
    public static string? FindFolder(Func<string, string?> variable, string home)
    {
        var settings = variable("XDG_CONFIG_HOME") is { Length: > 0 } given ? given
            : home.Length > 0 ? System.IO.Path.Combine(home, ".config")
            : null;
        return settings is null ? null : System.IO.Path.Combine(settings, "toobusy");
    }

    public MilestoneChoice? LoadMilestone() => Load(Milestone) is { } title ? new MilestoneChoice(title.Length == 0 ? null : title) : null;

    public void SaveMilestone(MilestoneChoice choice) => Save(Milestone, choice.Title ?? "");

    public ModelChoice? LoadModel() => Load(Model) is { } name ? new ModelChoice(name.Length == 0 ? null : name) : null;

    public void SaveModel(ModelChoice choice) => Save(Model, choice.Name ?? "");

    // An effort that is not a level of the assistant counts as one that is not chosen.
    public string? LoadEffort() => Load(Effort) is { } effort ? ClaudeCodeOptions.FindEffort(effort) : null;

    public void SaveEffort(string effort) => Save(Effort, effort);

    // A number that is not 1 or more counts as one that is not chosen.
    public int? LoadSessions() => int.TryParse(Load(Sessions), NumberStyles.None, CultureInfo.InvariantCulture, out var sessions) && sessions >= 1 ? sessions : null;

    public void SaveSessions(int sessions) => Save(Sessions, sessions.ToString(CultureInfo.InvariantCulture));

    string? Load(string key) => Read().TryGetValue(projectRoot, out var choices) ? choices.GetValueOrDefault(key) : null;

    // The file is read and written anew while its lock is held, and put in place whole, never half written: it keeps
    // the choices of every project, and a toobusy may be stopped at any moment.
    void Save(string key, string value)
    {
        Directory.CreateDirectory(folder);
        using var held = Hold();
        var projects = Read();
        if (!projects.TryGetValue(projectRoot, out var choices))
            projects[projectRoot] = choices = [];
        choices[key] = value;

        var text = new StringBuilder("# What you chose for each project you run toobusy in. toobusy writes this file.\n");
        text.Append("# An empty milestone means working without one; an empty model means the assistant's own.\n");
        foreach (var (root, chosen) in projects.OrderBy(project => project.Key, StringComparer.Ordinal))
        {
            text.Append("\n[").Append(SettingsToml.Quoted(root)).Append("]\n");
            foreach (var name in Keys.Where(chosen.ContainsKey))
                text.Append(name).Append(" = ").Append(SettingsToml.Quoted(chosen[name])).Append('\n');
        }

        var written = Path + ".tmp";
        File.WriteAllText(written, text.ToString());
        File.Move(written, Path, overwrite: true);
    }

    // The lock of the file, `projects.lock` beside it, which one process holds at a time and which the operating system
    // takes back from a process that ended: two toobusy that save a choice at once do it one after the other, and the
    // second keeps what the first saved. The file is never taken down, as one that is taken down and made anew can be
    // held by two processes at once.
    FileStream Hold()
    {
        for (var waited = 0; ; waited++)
        {
            try
            {
                return new FileStream(System.IO.Path.Combine(folder, "projects.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
            }
            catch (IOException) when (waited < Waits)
            {
                Thread.Sleep(Moment);
            }
        }
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

                // A choice that is not a text counts as one that is not there.
                if (pair.Value is StringValueSyntax { Value: { } value })
                    chosen.TryAdd(key, value);
            }

            if (root is not null && chosen.Count > 0)
                projects[root] = chosen;
        }

        return projects;
    }
}
