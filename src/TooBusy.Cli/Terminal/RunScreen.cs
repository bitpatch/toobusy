namespace TooBusy.Cli.Terminal;

public enum RunEnd
{
    Menu,
    Exit,
}

// The page of a run. The tasks are not run yet, so it is the line where commands are typed: `/` starts one,
// the commands that fit what is typed are listed under the line, and Enter runs the first of them.
public sealed class RunScreen(Page page)
{
    static readonly (string Name, string About, RunEnd End)[] Commands = [("menu", "go back to the menu", RunEnd.Menu), ("exit", "leave toobusy", RunEnd.Exit)];

    // Stays until the user leaves with a command; says which one.
    public RunEnd Run()
    {
        page.EscapeLeaves = false;
        page.Keys = "";
        page.Foot = Line.Empty;
        var editor = new LineEditor("");
        while (true)
        {
            var text = editor.Text;
            var fitting = text.StartsWith('/') ? Fitting(text[1..].Trim()) : [];
            var (shown, caret) = editor.View(Math.Max(4, page.Width - 3));
            var lines = new List<Line> { Line.Of(shown) };
            if (text.Length == 0)
                lines.Add(Line.Of("Type / for commands.", Tone.Muted));
            else if (!text.StartsWith('/'))
                lines.Add(Line.Of("Only commands work for now: they start with /.", Tone.Muted));
            else if (fitting.Count == 0)
                lines.Add(Line.Of("No command fits.", Tone.Error));
            lines.AddRange(fitting.Select((command, index) => new Line(
                new Part((index == 0 ? "❯ /" : "  /") + command.Name, index == 0 ? Tone.Accent : Tone.Plain), new Part("  " + command.About, Tone.Muted))));

            // The lines keep their number, so that nothing jumps while the text is typed.
            while (lines.Count < Commands.Length + 1)
                lines.Add(Line.Empty);
            page.Draw([], lines, fitting.Count > 0 ? "tab complete · enter run" : "", new Caret(0, caret), fitting.Count > 0 ? 1 : null);

            var key = page.Read();
            if (key.Key == ConsoleKey.Enter && fitting.Count > 0)
                return fitting[0].End;
            if (key.Key == ConsoleKey.Tab && fitting.Count > 0)
                editor.Set("/" + fitting[0].Name);
            else if (key.Key == ConsoleKey.Escape)
                editor.Set("");
            else if (key.Key != ConsoleKey.Enter)
                editor.Press(key);
        }
    }

    // The commands that fit what is typed after the slash, those that start with it first.
    static List<(string Name, string About, RunEnd End)> Fitting(string typed) =>
    [
        .. Commands.Where(command => command.Name.StartsWith(typed, StringComparison.OrdinalIgnoreCase)),
        .. Commands.Where(command => !command.Name.StartsWith(typed, StringComparison.OrdinalIgnoreCase) && command.Name.Contains(typed, StringComparison.OrdinalIgnoreCase)),
    ];
}
