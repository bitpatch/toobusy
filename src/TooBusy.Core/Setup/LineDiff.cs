namespace TooBusy.Core.Setup;

public sealed record ChangedLine(bool Added, string Text);

public static class LineDiff
{
    // The lines that differ between two texts, removed ones before the added ones that replace them.
    // Lines that stay are left out.
    public static IReadOnlyList<ChangedLine> Changes(string before, string after)
    {
        var old = Lines(before);
        var now = Lines(after);

        // common[i, j] is the length of the longest common run of lines of old[i..] and now[j..].
        var common = new int[old.Length + 1, now.Length + 1];
        for (var i = old.Length - 1; i >= 0; i--)
        {
            for (var j = now.Length - 1; j >= 0; j--)
                common[i, j] = old[i] == now[j] ? common[i + 1, j + 1] + 1 : Math.Max(common[i + 1, j], common[i, j + 1]);
        }

        var changes = new List<ChangedLine>();
        var (at, to) = (0, 0);
        while (at < old.Length || to < now.Length)
        {
            if (at < old.Length && to < now.Length && old[at] == now[to])
                (at, to) = (at + 1, to + 1);
            else if (at < old.Length && (to == now.Length || common[at + 1, to] >= common[at, to + 1]))
                changes.Add(new ChangedLine(false, old[at++]));
            else
                changes.Add(new ChangedLine(true, now[to++]));
        }

        return changes;
    }

    static string[] Lines(string text) =>
        text.Length == 0 ? [] : [.. (text.EndsWith('\n') ? text[..^1] : text).Split('\n').Select(line => line.TrimEnd('\r'))];
}
