namespace TooBusy.Core.Settings;

// What a settings file says before it is validated: every value under its full dotted key, and every section,
// each with the line it is written on. Whoever parses the file produces it; SettingsValidator knows nothing of TOML.
public sealed record SettingsDocument(IReadOnlyList<SettingsEntry> Entries, IReadOnlyList<SettingsSection> Sections);

// Value is a string, a long, a bool, an IReadOnlyList<object> of such values,
// or SettingsEntry.Other for anything the settings never use.
public sealed record SettingsEntry(string Key, object Value, int Line)
{
    public static object Other { get; } = new();
}

public sealed record SettingsSection(string Name, int Line);
