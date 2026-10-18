using System.Globalization;

namespace TooBusy.Infrastructure.Processes;

// What a command printed and what it was given, as the log of a run shows them. A command that failed has its whole
// answer there, up to Whole; one that passed its first lines, up to Shown, each cut to Width, and how much there
// was. An argument with a space is quoted; one of several lines, or longer than ArgumentWidth, is cut to its first
// line and that width, with how long it was.
static class Printed
{
    const int Shown = 5;
    const int Width = 300;
    const int ArgumentWidth = 120;
    const int Whole = 64 * 1024;

    // The answer of a command, as lines under the line that says how it ended; empty when it said nothing.
    public static string Of(string output, string error, bool whole)
    {
        var text = Joined(output, error).TrimEnd('\n', '\r');
        if (text.Length == 0)
            return "";
        if (whole)
            return text.Length <= Whole ? "\n" + text : "\n" + text[..Whole] + $"\n… cut: {Size(text.Length)} in all";

        var lines = text.Split('\n');
        var cut = lines.Length > Shown || lines.Take(Shown).Any(line => line.Length > Width);
        var shown = lines.Take(Shown).Select(line => line.Length > Width ? line[..Width] + "…" : line);
        return "\n" + string.Join('\n', shown) + (cut ? $"\n… {lines.Length} lines, {Size(text.Length)} in all" : "");
    }

    public static string Line(string command, IReadOnlyList<string> arguments) => string.Join(' ', arguments.Select(Argument).Prepend(command));

    public static string Argument(string argument)
    {
        var first = argument.Split('\n')[0].TrimEnd('\r');
        var cut = first.Length > ArgumentWidth ? first[..ArgumentWidth] : first;
        var shortened = cut.Length < argument.Length;
        if (!shortened && argument.Length > 0 && !argument.Any(char.IsWhiteSpace) && !argument.Contains('"'))
            return argument;

        return "\"" + cut.Replace("\"", "\\\"") + (shortened ? $"… ({Size(argument.Length)})" : "") + "\"";
    }

    public static string Size(int length) =>
        length < 1024 ? string.Create(CultureInfo.InvariantCulture, $"{length} B") : string.Create(CultureInfo.InvariantCulture, $"{length / 1024.0:0.#} KB");

    static string Joined(string output, string error) =>
        output.Length == 0 || error.Length == 0 || output.EndsWith('\n') ? output + error : output + "\n" + error;
}
