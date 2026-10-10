using System.Text;
using Tomlyn.Parsing;
using Tomlyn.Syntax;
using TooBusy.Core.Queue;

namespace TooBusy.Infrastructure.Settings;

// What one user chose for the project at the given root, kept outside the project: in `projects.toml` of the user's
// own toobusy folder, a table for each project under the path of its root. toobusy writes the file whole; a file it
// cannot read counts as one that says nothing, and is replaced when a choice is saved.
public sealed class PersonalSettingsFile(string folder, string projectRoot) : IPersonalSettings
{
    const string Milestone = "milestone";

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

    public MilestoneChoice? LoadMilestone() =>
        Read().TryGetValue(projectRoot, out var title) ? new MilestoneChoice(title.Length == 0 ? null : title) : null;

    public void SaveMilestone(MilestoneChoice choice)
    {
        var projects = Read();
        projects[projectRoot] = choice.Title ?? "";

        var text = new StringBuilder("# What you chose for each project you run toobusy in. toobusy writes this file.\n");
        text.Append("# An empty milestone means working without one.\n");
        foreach (var (root, title) in projects.OrderBy(project => project.Key, StringComparer.Ordinal))
            text.Append("\n[").Append(SettingsToml.Quoted(root)).Append("]\n").Append(Milestone).Append(" = ").Append(SettingsToml.Quoted(title)).Append('\n');

        Directory.CreateDirectory(folder);
        File.WriteAllText(Path, text.ToString());
    }

    // The title of the milestone of every project the file knows; an empty title is working without a milestone.
    Dictionary<string, string> Read()
    {
        var projects = new Dictionary<string, string>(StringComparer.Ordinal);
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
            var title = table.Items
                .Where(pair => pair.Key is { DotKeys.ChildrenCount: 0, Key: BareKeySyntax { Key.Text: Milestone } })
                .Select(pair => (pair.Value as StringValueSyntax)?.Value)
                .FirstOrDefault();
            if (root is not null && title is not null)
                projects[root] = title;
        }

        return projects;
    }
}
