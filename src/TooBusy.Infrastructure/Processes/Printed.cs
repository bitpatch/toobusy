using System.Globalization;
using TooBusy.Core.Run;

namespace TooBusy.Infrastructure.Processes;

// What a command printed and what it was given, as the log of a run shows them. A command that failed has its whole
// answer there, up to Whole. One that passed has it only when it is short, up to Lines and Short, and not JSON, as
// the answer that says which session started: a long one, as a list of every session on the machine, says nothing
// in the log.
// An argument with a space is quoted; one of several lines, or longer than ArgumentWidth, is cut to its first line
// and that width, with how long it was.
static class Printed
{
    const int Lines = 8;
    const int Short = 1024;
    const int ArgumentWidth = 120;
    const int Whole = 64 * 1024;

    // The answer of a command, as lines under the line that says how it ended; empty when nothing of it is shown.
    public static string Of(string output, string error, bool failed)
    {
        var text = Joined(output, error).TrimEnd('\n', '\r');
        if (text.Length == 0)
            return "";
        if (failed)
            return text.Length <= Whole ? "\n" + text : "\n" + text[..Whole] + $"\n… cut: {Size(text.Length)} in all";

        return text.Length <= Short && text.Split('\n').Length <= Lines && !Json(text) ? "\n" + text : "";
    }

    // An answer in JSON is for the tool that reads it, not for whoever reads the log, however short it is.
    static bool Json(string text) => text.TrimStart() is ['[' or '{', ..];

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

    // How long a command took: under a second in milliseconds, as a second rounded up would say more than it took.
    public static string Took(TimeSpan time) =>
        time < TimeSpan.FromSeconds(1) ? string.Create(CultureInfo.InvariantCulture, $"{(int)Math.Max(0, time.TotalMilliseconds)} ms") : Spoken.Time(time);

    public static string Size(int length) =>
        length < 1024 ? string.Create(CultureInfo.InvariantCulture, $"{length} B") : string.Create(CultureInfo.InvariantCulture, $"{length / 1024.0:0.#} KB");

    static string Joined(string output, string error) =>
        output.Length == 0 || error.Length == 0 || output.EndsWith('\n') ? output + error : output + "\n" + error;
}
