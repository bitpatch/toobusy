using System.Text;

namespace TooBusy.Trackers.GitHub;

// What the tool prints when its filter writes lines of tab-separated fields with `@tsv`.
static class Tsv
{
    // The lines of what the tool printed, without the empty ones.
    public static IEnumerable<string> Lines(string text) =>
        text.Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(line => line.TrimEnd('\r')).Where(line => line.Length > 0);

    // A field as it was before the tool wrote it into a line: it writes a tab, a line break and a backslash as two
    // characters each.
    public static string Unescaped(string field)
    {
        if (!field.Contains('\\', StringComparison.Ordinal))
            return field;

        var text = new StringBuilder(field.Length);
        for (var index = 0; index < field.Length; index++)
        {
            if (field[index] != '\\' || index == field.Length - 1)
            {
                text.Append(field[index]);
                continue;
            }

            text.Append(field[++index] switch
            {
                't' => '\t',
                'n' => '\n',
                'r' => '\r',
                var other => other,
            });
        }

        return text.ToString();
    }
}
