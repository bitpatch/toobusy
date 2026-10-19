using System.Globalization;

namespace TooBusy.Core.Run;

public enum OutcomeKind
{
    // The task is done, committed and pushed.
    Done,

    // A part is done; what is left is a new task, which the session made.
    Partial,

    // Nothing could be done without the owner.
    Owner,

    // The work stopped on something the session could not fix.
    Failed,

    // The session wrapped the task up because it was asked to.
    Interrupted,
}

// How a task went, as the last reply of its session says. The reply ends with a line for toobusy,
// `TOOBUSY: done`, and what stands before that line is the report of the session.
public sealed record Outcome(OutcomeKind Kind, string Report, string? Reason = null)
{
    public const string Mark = "TOOBUSY:";

    // What stands before the number of a task where a report names one: `#34`, or the address of an issue.
    static readonly string[] Naming = ["#", "/issues/"];

    // What a session may write around the line without meaning anything by it.
    static readonly char[] Decoration = [' ', '\t', '\r', '*', '_', '`', '>'];

    // Null when the reply has no line for toobusy, or one that says nothing toobusy knows.
    public static Outcome? Read(string? reply)
    {
        if (reply is null)
            return null;

        var lines = reply.Split('\n');
        var last = Array.FindLastIndex(lines, line => line.Trim(Decoration).StartsWith(Mark, StringComparison.OrdinalIgnoreCase));
        if (last < 0)
            return null;

        var said = lines[last].Trim(Decoration)[Mark.Length..].Trim(Decoration);
        var word = said.Split(' ', 2)[0].TrimEnd('.', ':', ',');
        var known = Array.FindIndex(Kinds, kind => kind.Word.Equals(word, StringComparison.OrdinalIgnoreCase));
        if (known < 0)
            return null;

        var found = Kinds[known].Kind;
        var reason = said.Length > word.Length ? said[word.Length..].Trim(' ', ':', '-', '—') : "";
        return new Outcome(found, Text(lines[..last]), reason.Length == 0 ? null : reason);
    }

    // Whether the reply ends with a line for toobusy: the last of its lines that is not empty is the one `Read`
    // reads. A reply with a question after the line does not end with it.
    public static bool Ends(string? reply)
    {
        if (Read(reply) is null)
            return false;

        var lines = reply!.Split('\n');
        return lines[Array.FindLastIndex(lines, line => line.Trim(Decoration).Length > 0)].Trim(Decoration).StartsWith(Mark, StringComparison.OrdinalIgnoreCase);
    }

    // Whether the report names a task other than `own`, as a session that made a task of what is left does. It is
    // the report that is read, not the tracker: what the named task is, is the session's to see to. So any number
    // after a `#` counts, an ordinal as `step #2 of 5` among it: should a report pass on that alone, the check may ask
    // for the address of an issue or for a number that is not a small one.
    public bool Names(int own)
    {
        foreach (var naming in Naming)
        {
            for (var at = Report.IndexOf(naming, StringComparison.OrdinalIgnoreCase); at >= 0; at = Report.IndexOf(naming, at + 1, StringComparison.OrdinalIgnoreCase))
            {
                var digits = Report[(at + naming.Length)..].TakeWhile(char.IsAsciiDigit).ToArray();
                if (digits.Length is > 0 and < 10 && int.Parse(digits, CultureInfo.InvariantCulture) != own)
                    return true;
            }
        }

        return false;
    }

    static readonly (string Word, OutcomeKind Kind)[] Kinds =
    [
        ("done", OutcomeKind.Done),
        ("partial", OutcomeKind.Partial),
        ("owner", OutcomeKind.Owner),
        ("failed", OutcomeKind.Failed),
        ("interrupted", OutcomeKind.Interrupted),
    ];

    static string Text(string[] lines) => string.Join('\n', lines).Trim();
}
