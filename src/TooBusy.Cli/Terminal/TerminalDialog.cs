using System.Globalization;
using System.Text;
using TooBusy.Core.Setup;

namespace TooBusy.Cli.Terminal;

// The questions of the setup in a terminal: a selection with the arrow keys, a multiple choice with the space bar and
// a text prompt. A question that is answered collapses into one line, so that the screen reads as a short history
// with the current question at its bottom. The prompts are drawn by hand with the escape sequences every terminal knows.
public sealed class TerminalDialog(TextWriter output, Func<ConsoleKeyInfo> readKey, Palette palette) : ISetupDialog
{
    const int LabelWidth = 16;

    // A list longer than this scrolls.
    const int Rows = 10;

    const string HideCursor = "\u001b[?25l";

    // What a program that is stopped in the middle of a prompt must still write.
    public const string ShowCursor = "\u001b[?25h";

    // The lines of the prompt that is on the screen now; the cursor is on the last of them.
    int drawn;

    public int Choose(string label, IReadOnlyList<SetupOption> options, int proposed)
    {
        var at = List(label, options, proposed, null, "↑↓ to move, Enter to choose");
        Collapse(label, options[at].Name);
        return at;
    }

    public IReadOnlyList<int> ChooseMany(string label, IReadOnlyList<string> options, IReadOnlyList<int> proposed, string whenNone)
    {
        var chosen = new SortedSet<int>(proposed);
        if (options.Count > 0)
            List(label, [.. options.Select(option => new SetupOption(option, ""))], chosen.FirstOrDefault(), chosen, "↑↓ to move, Space to select, Enter to confirm");
        Collapse(label, chosen.Count == 0 ? whenNone : string.Join(", ", chosen.Select(index => options[index])));
        return [.. chosen];
    }

    public string Ask(string label, string hint, string proposed, string whenEmpty, Func<string, string?> refuse)
    {
        var text = new StringBuilder(proposed);
        string? reason = null;
        while (true)
        {
            Draw([$"{Question(label)}  {(reason is null ? palette.Muted(hint) : palette.Error(reason))}", $"{palette.Accent("❯")} {text}"]);
            var key = readKey();
            if (key.Key == ConsoleKey.Enter)
            {
                reason = refuse(text.ToString());
                if (reason is null)
                    break;
            }
            else if (key.Key == ConsoleKey.Backspace)
            {
                text.Length = Math.Max(0, text.Length - 1);
            }
            else if (!char.IsControl(key.KeyChar))
            {
                text.Append(key.KeyChar);
            }
        }

        var answer = text.ToString().Trim();
        Collapse(label, answer.Length == 0 ? whenEmpty : answer);
        return answer;
    }

    public bool Confirm(string question)
    {
        while (true)
        {
            Draw([$"{Question(question)}  {palette.Muted("Enter for yes, n for no")}"]);
            var key = readKey();
            var yes = key.Key is ConsoleKey.Enter or ConsoleKey.Y;
            if (!yes && key.Key is not (ConsoleKey.N or ConsoleKey.Escape))
                continue;

            Erase();
            output.WriteLine($"{(yes ? palette.Success("✔") : palette.Error("✘"))} {question} {(yes ? "yes" : "no")}");
            return yes;
        }
    }

    public void Answered(string label, string value) => Collapse(label, value);

    public void Say(SetupTone tone, string text) => output.WriteLine(tone switch
    {
        SetupTone.Muted => palette.Muted(text),
        SetupTone.Warning => $"{palette.Warning("!")} {palette.Warning(text)}",
        SetupTone.Failure => $"{palette.Error("✘")} {palette.Error(text)}",
        SetupTone.Added => palette.Success(text),
        SetupTone.Removed => palette.Error(text),
        _ => palette.Message(text),
    });

    // The list of both choices. With `chosen` the space bar marks lines and Enter confirms them all;
    // without it Enter chooses the line the pointer is on.
    int List(string label, IReadOnlyList<SetupOption> options, int start, SortedSet<int>? chosen, string keys)
    {
        var at = Math.Clamp(start, 0, options.Count - 1);
        var top = 0;
        var width = options.Max(option => option.Name.Length);
        output.Write(HideCursor);
        try
        {
            while (true)
            {
                top = Math.Clamp(top, Math.Max(0, at - Rows + 1), at);
                var lines = new List<string> { Question(label) };
                for (var index = top; index < Math.Min(options.Count, top + Rows); index++)
                {
                    var mark = chosen is null ? "" : chosen.Contains(index) ? "◉ " : "◯ ";
                    var name = mark + options[index].Name.PadRight(width);
                    var detail = options[index].Detail.Length == 0 ? "" : "  " + palette.Muted(options[index].Detail);
                    lines.Add((index == at ? palette.Accent("❯ " + name) : "  " + name) + detail);
                }

                var more = options.Count > Rows ? string.Create(CultureInfo.InvariantCulture, $"{at + 1} of {options.Count}, ") : "";
                lines.Add(palette.Muted("  " + more + keys));
                Draw(lines);

                var key = readKey();
                if (key.Key is ConsoleKey.UpArrow or ConsoleKey.K)
                    at = (at + options.Count - 1) % options.Count;
                else if (key.Key is ConsoleKey.DownArrow or ConsoleKey.J)
                    at = (at + 1) % options.Count;
                else if (key.Key == ConsoleKey.Spacebar && chosen is not null && !chosen.Remove(at))
                    chosen.Add(at);
                else if (key.Key == ConsoleKey.Enter)
                    return at;
            }
        }
        finally
        {
            output.Write(ShowCursor);
        }
    }

    string Question(string label) => $"{palette.Accent("?")} {label}";

    void Collapse(string label, string value)
    {
        Erase();
        output.WriteLine($"{palette.Success("✔")} {label.PadRight(LabelWidth)} {value}".TrimEnd());
    }

    void Draw(List<string> lines)
    {
        Erase();
        output.Write(string.Join('\n', lines));
        output.Flush();
        drawn = lines.Count;
    }

    // Goes back to the first line of the prompt and clears everything from there down.
    void Erase()
    {
        if (drawn > 1)
            output.Write(string.Create(CultureInfo.InvariantCulture, $"\u001b[{drawn - 1}A"));
        if (drawn > 0)
            output.Write("\r\u001b[J");
        drawn = 0;
    }
}
