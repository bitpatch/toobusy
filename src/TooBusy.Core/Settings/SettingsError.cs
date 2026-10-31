using System.Globalization;

namespace TooBusy.Core.Settings;

// One thing that is wrong with a settings file. The key is absent for a file that cannot be parsed at all,
// the line for a key of a section that the file does not have.
public sealed record SettingsError(string? Key, int? Line, string Message)
{
    public string Describe(string file)
    {
        var place = Line is { } line ? string.Create(CultureInfo.InvariantCulture, $"{file}:{line}") : file;
        return Key is null ? $"{place}: {Message}" : $"{place}: {Key}: {Message}";
    }
}
