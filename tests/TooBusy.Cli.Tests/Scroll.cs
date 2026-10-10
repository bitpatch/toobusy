using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace TooBusy.Cli.Tests;

// The terminal's own screen as a tape leaves it. What is written is read as a terminal reads it, sequence by
// sequence, into rows, those that have scrolled out of the window among them. It knows only what a tape, and the
// lines that are printed around one, may write: anything else fails the test, and so does a line that wraps. A tape
// that has lost count of its lines, or a screen that draws into one, is caught here.
//
// What is drawn on the alternate screen is passed over: a test reads that as frames.
public sealed partial class Scroll(int width = 120, int height = 30)
{
    readonly List<StringBuilder> rows = [new()];

    // The row at the top of the window, and where the cursor is; rows are counted from the first one ever written.
    int top;
    int row;
    int column;
    bool wraps = true;

    public bool Alternate { get; private set; }

    public bool CursorShown { get; private set; } = true;

    // Whether lines wrap at the edge, as they do unless a tape is unrolled.
    public bool Wraps => wraps;

    public (int Row, int Column) Cursor => (row, column);

    public IReadOnlyList<string> Rows => [.. rows.Select(line => line.ToString().TrimEnd())];

    // The rows down to the last one that has something: under it are only those a foot has stood on.
    public IReadOnlyList<string> Written => [.. Rows.Take(Rows.ToList().FindLastIndex(line => line.Length > 0) + 1)];

    // The rows without the empty ones and the rules, as one text.
    public string Text => string.Join('\n', Rows.Where(line => line.Trim(' ', '─').Length > 0));

    public static Scroll Read(string output, int width = 120, int height = 30)
    {
        var scroll = new Scroll(width, height);
        scroll.Write(output);
        return scroll;
    }

    public void Write(string text)
    {
        for (var at = 0; at < text.Length; at++)
        {
            if (text[at] == '\u001b')
            {
                var sequence = Sequence().Match(text, at);
                if (!sequence.Success)
                    throw new InvalidOperationException($"The terminal was written a sequence the tape does not know: {Shown(text, at)}");

                at += sequence.Length - 1;
                Do(sequence.Groups[1].Value + sequence.Groups[4].Value, sequence.Groups[2].Value, text, at);
            }
            else if (!Alternate)
            {
                Put(text[at]);
            }
        }
    }

    void Do(string kind, string argument, string text, int at)
    {
        var count = int.TryParse(argument, NumberStyles.None, CultureInfo.InvariantCulture, out var given) && given > 0 ? given : 1;
        switch (kind, argument)
        {
            case ("?h", "1049"):
                Alternate = true;
                return;
            case ("?l", "1049"):
                Alternate = false;
                return;
            case (_, _) when Alternate:
            case ("m", _) or ("q", _):
                return;
            case ("?h", "25") or ("?l", "25"):
                CursorShown = kind == "?h";
                return;
            case ("?h", "7") or ("?l", "7"):
                wraps = kind == "?h";
                return;
            case ("A", _):
                row = Math.Max(top, row - count);
                return;
            case ("B", _):
                row = Math.Min(top + height - 1, row + count);
                Reach();
                return;
            case ("G", _):
                column = count - 1;
                return;
            case ("K", ""):
                Erase(row);
                return;
            case ("J", ""):
                Erase(row);
                for (var below = row + 1; below < rows.Count; below++)
                    rows[below].Clear();
                return;
            default:
                throw new InvalidOperationException($"The terminal's own screen was written a sequence a tape does not write: {Shown(text, at)}");
        }
    }

    void Put(char character)
    {
        switch (character)
        {
            case '\r':
                column = 0;
                return;

            // A terminal starts a new line at its left edge whatever the program wrote to end the line before.
            case '\n':
                if (row == top + height - 1)
                    top++;
                (row, column) = (row + 1, 0);
                Reach();
                return;
        }

        if (column >= width)
        {
            if (wraps)
                throw new InvalidOperationException($"A line of the terminal's own screen wrapped: “{rows[row]}”.");
            column = width - 1;
        }

        var line = rows[row];
        if (line.Length < column)
            line.Append(' ', column - line.Length);
        if (line.Length == column)
            line.Append(character);
        else
            line[column] = character;
        column++;
    }

    void Erase(int line)
    {
        if (rows[line].Length > column)
            rows[line].Length = column;
    }

    void Reach()
    {
        while (rows.Count <= row)
            rows.Add(new StringBuilder());
    }

    static string Shown(string text, int at) => text[at..Math.Min(text.Length, at + 12)].Replace("\u001b", "ESC", StringComparison.Ordinal);

    [GeneratedRegex(@"\G\u001b\[(\??)([0-9;]*)( ?)([A-Za-z])")]
    private static partial Regex Sequence();
}
