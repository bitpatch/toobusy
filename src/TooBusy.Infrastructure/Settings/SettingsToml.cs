using System.Globalization;
using System.Text;
using Tomlyn.Parsing;
using Tomlyn.Syntax;
using TooBusy.Core.Settings;

namespace TooBusy.Infrastructure.Settings;

// The settings as TOML text. Reading goes through Tomlyn's syntax tree, which knows the place of every value;
// writing edits the text at those places, so that whatever it does not change stays as it was written.
public static class SettingsToml
{
    static readonly (string Name, string Comment)[] Sections =
    [
        ("tracker", "Where the tasks are."),
        ("queue.labels", "A task with a blocking label is never taken. A task needs one of the labels to take; an empty list means any task."),
        ("assistant", "Who does the tasks."),
    ];

    public static SettingsValidation Read(string text)
    {
        var parsed = Parse(text);
        return parsed.Errors.Count > 0 ? new SettingsValidation(null, parsed.Errors) : SettingsValidator.Validate(parsed.Document);
    }

    // Returns the text of a settings file with the given settings. Out of an empty text it makes a new file with
    // a comment over each section; in an existing one it changes the values that differ and keeps every other key
    // and comment. Throws FormatException when the existing text is not TOML or is laid out in a way that cannot be
    // edited in place.
    public static string Write(ProjectSettings settings, string existing = "")
    {
        var parsed = Parse(existing);
        if (parsed.Errors.Count > 0)
            throw new FormatException("The existing settings are not valid TOML: " + parsed.Errors[0].Describe(SettingsFile.DisplayPath));

        var wanted = Values(settings);
        var edits = new List<Edit>();
        var additions = new Dictionary<string, StringBuilder>();
        foreach (var (key, value) in wanted)
        {
            var places = parsed.Places.Where(place => place.Key == key).ToList();
            if (places.Count > 1)
                throw new FormatException($"The existing settings set `{key}` more than once.");
            if (places.Count == 0 && value is not null)
                Add(additions, key, value);
            else if (places.Count == 1 && value is null)
                edits.Add(RemoveLine(existing, places[0].Pair));
            else if (places.Count == 1 && !Same(parsed.Document.Entries.Single(entry => entry.Key == key).Value, value!))
                edits.Add(new Edit(places[0].Value.Span.Start.Offset, places[0].Value.Span.End.Offset + 1, Literal(value!)));
        }

        // Keys of the root go before everything else, keys of a section that the text has go to its end,
        // and a section that the text lacks is added after everything else.
        var first = "";
        var appended = new StringBuilder();
        foreach (var (section, lines) in additions)
        {
            if (section.Length == 0)
                first = existing.Length == 0 ? lines.ToString() : lines + "\n";
            else if (parsed.Tables.FindLastIndex(table => table.Name == section) is >= 0 and var index)
                edits.Add(InsertAfterLine(existing, parsed.Tables[index].End, lines.ToString()));
            else
                appended.Append("\n# ").Append(Sections.Single(known => known.Name == section).Comment).Append("\n[").Append(section).Append("]\n").Append(lines);
        }

        var text = new StringBuilder(existing);
        foreach (var edit in edits.OrderByDescending(edit => edit.Start))
            text.Remove(edit.Start, edit.End - edit.Start).Insert(edit.Start, edit.Text);
        if (appended.Length > 0 && text.Length > 0 && text[^1] != '\n')
            text.Append('\n');

        var result = first + text + appended;
        Verify(result, wanted);
        return result;
    }

    // The values in the order a new file lists them; null is a key that the file must not have.
    static List<(string Key, object? Value)> Values(ProjectSettings settings) =>
    [
        (SettingsKeys.Version, (long)ProjectSettings.SchemaVersion),
        (SettingsKeys.TrackerType, settings.Tracker.Type),
        (SettingsKeys.TrackerBoard, settings.Tracker.Board),
        (SettingsKeys.BlockingLabels, settings.Queue.Labels.Blocking),
        (SettingsKeys.TakeLabels, settings.Queue.Labels.Take),
        (SettingsKeys.AssistantType, settings.Assistant.Type),
    ];

    static void Add(Dictionary<string, StringBuilder> additions, string key, object value)
    {
        var dot = key.LastIndexOf('.');
        var section = dot < 0 ? "" : key[..dot];
        if (!additions.TryGetValue(section, out var lines))
            additions.Add(section, lines = new StringBuilder());
        lines.Append(key[(dot + 1)..]).Append(" = ").Append(Literal(value)).Append('\n');
    }

    // The whole lines of a key, with the comment at their end.
    static Edit RemoveLine(string text, KeyValueSyntax pair)
    {
        var start = pair.Span.Start.Offset;
        while (start > 0 && text[start - 1] is ' ' or '\t')
            start--;
        if (start > 0 && text[start - 1] != '\n')
            throw new FormatException($"The existing settings keep `{Name(pair.Key!)}` inside a line; it cannot be removed in place.");
        return new Edit(start, LineEnd(text, pair.Span.End.Offset), "");
    }

    static Edit InsertAfterLine(string text, int offset, string lines)
    {
        var end = LineEnd(text, offset);
        return new Edit(end, end, end == 0 || text[end - 1] == '\n' ? lines : "\n" + lines);
    }

    // The offset after the line that holds the given one. A span ends before a comment that closes the last line of a text.
    static int LineEnd(string text, int offset)
    {
        var newLine = offset < text.Length ? text.IndexOf('\n', offset) : -1;
        return newLine < 0 ? text.Length : newLine + 1;
    }

    static void Verify(string result, List<(string Key, object? Value)> wanted)
    {
        var parsed = Parse(result);
        var intact = parsed.Errors.Count == 0 && wanted.All(pair =>
        {
            var entries = parsed.Document.Entries.Where(entry => entry.Key == pair.Key).ToList();
            return pair.Value is null ? entries.Count == 0 : entries.Count == 1 && Same(entries[0].Value, pair.Value);
        });
        if (!intact)
            throw new FormatException("The existing settings are laid out in a way that cannot be edited in place.");
    }

    static bool Same(object left, object right) => (left, right) switch
    {
        (IReadOnlyList<object> one, IReadOnlyList<object> other) => one.Count == other.Count && one.Zip(other).All(pair => Same(pair.First, pair.Second)),
        (IReadOnlyList<object>, _) or (_, IReadOnlyList<object>) => false,
        _ => left.Equals(right),
    };

    static string Literal(object value) => value switch
    {
        long number => number.ToString(CultureInfo.InvariantCulture),
        string text => Quoted(text),
        IReadOnlyList<object> items => "[" + string.Join(", ", items.Select(Literal)) + "]",
        _ => throw new ArgumentOutOfRangeException(nameof(value)),
    };

    static string Quoted(string text)
    {
        var quoted = new StringBuilder("\"");
        foreach (var character in text)
        {
            quoted.Append(character switch
            {
                '"' => "\\\"",
                '\\' => "\\\\",
                '\n' => "\\n",
                '\r' => "\\r",
                '\t' => "\\t",
                < ' ' or '\u007f' => string.Create(CultureInfo.InvariantCulture, $"\\u{(int)character:X4}"),
                _ => character.ToString(),
            });
        }

        return quoted.Append('"').ToString();
    }

    static Parsed Parse(string text)
    {
        var syntax = SyntaxParser.Parse(text, SettingsFile.DisplayPath);
        var parsed = new Parsed();
        if (syntax.HasErrors)
        {
            parsed.Errors.AddRange(syntax.Diagnostics
                .Where(diagnostic => diagnostic.Kind == DiagnosticMessageKind.Error)
                .Select(diagnostic => new SettingsError(null, diagnostic.Span.Start.Line + 1, diagnostic.Message)));
            return parsed;
        }

        foreach (var pair in syntax.KeyValues)
            parsed.Add("", pair);
        foreach (var table in syntax.Tables)
        {
            var name = Name(table.Name!);
            var line = table.Span.Start.Line + 1;
            if (table is TableArraySyntax)
            {
                // A list of tables is nothing the settings have: it is handed over as a value of a kind they refuse.
                parsed.Entries.Add(new SettingsEntry(name, SettingsEntry.Other, line));
                continue;
            }

            parsed.Sections.Add(new SettingsSection(name, line));
            parsed.Tables.Add((name, table.Span.End.Offset));
            foreach (var pair in table.Items)
                parsed.Add(name + ".", pair);
        }

        return parsed;
    }

    static string Name(KeySyntax key) =>
        string.Join('.', new[] { Part(key.Key!) }.Concat(key.DotKeys.Select(dotted => Part(dotted.Key!))));

    static string Part(BareKeyOrStringValueSyntax part) => part switch
    {
        BareKeySyntax bare => bare.Key!.Text!,
        StringValueSyntax quoted => quoted.Value!,
        _ => part.ToString(),
    };

    static object Value(ValueSyntax value) => value switch
    {
        StringValueSyntax text => text.Value!,
        IntegerValueSyntax number => number.Value,
        BooleanValueSyntax flag => flag.Value,
        ArraySyntax array => array.Items.Select(item => Value(item.Value!)).ToArray(),
        _ => SettingsEntry.Other,
    };

    readonly record struct Edit(int Start, int End, string Text);

    sealed class Parsed
    {
        public List<SettingsError> Errors { get; } = [];

        public List<SettingsEntry> Entries { get; } = [];

        public List<SettingsSection> Sections { get; } = [];

        // Where each value is written, for the keys that can be edited in place.
        public List<(string Key, KeyValueSyntax Pair, ValueSyntax Value)> Places { get; } = [];

        // The offset of the last character of each table; a new key of the table goes after that line.
        public List<(string Name, int End)> Tables { get; } = [];

        public SettingsDocument Document => new(Entries, Sections);

        public void Add(string prefix, KeyValueSyntax pair)
        {
            var key = prefix + Name(pair.Key!);
            Entries.Add(new SettingsEntry(key, Value(pair.Value!), pair.Span.Start.Line + 1));
            Places.Add((key, pair, pair.Value!));
        }
    }
}
