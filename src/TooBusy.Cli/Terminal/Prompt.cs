namespace TooBusy.Cli.Terminal;

// A question that is answered with a text: its name and what to do, and under them the line where the text is typed.
public static class Prompt
{
    // Asks for a text, starting from the proposed one. `refuse` says why a text cannot be taken, or null when it
    // can. Gives the text without the spaces around it, or null when Escape is pressed.
    public static string? Ask(Page page, string label, string hint, string proposed, Func<string, string?> refuse)
    {
        var editor = new LineEditor(proposed);
        string? reason = null;
        while (true)
        {
            var (field, column) = Field(page, editor, "");
            page.Draw(Picker.Head(label, hint, reason), [field], "enter confirm", new Caret(0, column));

            var key = page.Read();
            if (key.Key == ConsoleKey.Escape)
                return null;
            if (key.Key != ConsoleKey.Enter)
                editor.Press(key);
            else if ((reason = refuse(editor.Text)) is null)
                return editor.Text.Trim();
        }
    }

    // The line where a text is typed, and the column of the caret in it.
    public static (Line Line, int Column) Field(Page page, LineEditor editor, string prompt)
    {
        var (shown, caret) = editor.View(Math.Max(4, page.Width - 3 - prompt.Length));
        return (new Line(new Part(prompt, Tone.Muted), new Part(shown)), prompt.Length + caret);
    }
}
