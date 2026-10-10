namespace TooBusy.Core.Run;

public enum OutcomeKind
{
    // The task is done, committed and pushed.
    Done,

    // A part is done; what is left is a new task for the owner.
    Partial,

    // Nothing could be done without the owner.
    Owner,

    // The work stopped on something the session could not fix.
    Failed,

    // The session wrapped the task up because it was asked to.
    Interrupted,
}

// What is left of a task that was done in part: a task of its own, as the session described it.
public sealed record RestTask(string Title, string Description);

// How a task went, as the last reply of its session says. The reply ends with a line for toobusy,
// `TOOBUSY: done`, and what stands before that line is the report of the session. A task that was done in part
// names what is left after the report: a line `TOOBUSY-REST: <title>` and the description under it.
public sealed record Outcome(OutcomeKind Kind, string Report, string? Reason = null, RestTask? Rest = null)
{
    public const string Mark = "TOOBUSY:";
    public const string RestMark = "TOOBUSY-REST:";

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
        var rest = found == OutcomeKind.Partial ? Array.FindLastIndex(lines, last, line => line.Trim(Decoration).StartsWith(RestMark, StringComparison.OrdinalIgnoreCase)) : -1;
        if (rest < 0)
            return new Outcome(found, Text(lines[..last]), reason.Length == 0 ? null : reason);

        var title = lines[rest].Trim(Decoration)[RestMark.Length..].Trim(Decoration);
        var description = Text(lines[(rest + 1)..last]);
        return new Outcome(found, Text(lines[..rest]), null, title.Length == 0 && description.Length == 0 ? null : new RestTask(title, description));
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
