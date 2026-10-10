namespace TooBusy.Cli.Terminal;

// The text of a prompt and the place in it where typing goes. It knows nothing of the screen.
public sealed class LineEditor(string text)
{
    public string Text { get; private set; } = text;

    public int Caret { get; private set; } = text.Length;

    public bool AtEnd => Caret == Text.Length;

    public void Set(string text)
    {
        Text = text;
        Caret = text.Length;
    }

    public void Insert(char character)
    {
        Text = Text.Insert(Caret, character.ToString());
        Caret++;
    }

    public void Backspace() => Remove(Caret - 1, Caret);

    public void Delete() => Remove(Caret, Caret + 1);

    public void Left() => Caret = Math.Max(0, Caret - 1);

    public void Right() => Caret = Math.Min(Text.Length, Caret + 1);

    public void Home() => Caret = 0;

    public void End() => Caret = Text.Length;

    public void ClearToStart() => Remove(0, Caret);

    // Removes the word before the caret with the spaces that follow it.
    public void DeleteWordBack()
    {
        var start = Caret;
        while (start > 0 && Text[start - 1] == ' ')
            start--;
        while (start > 0 && Text[start - 1] != ' ')
            start--;
        Remove(start, Caret);
    }

    // Does what the key means for a line of text; false when it means nothing for it.
    public bool Press(ConsoleKeyInfo key)
    {
        var control = key.Modifiers.HasFlag(ConsoleModifiers.Control);
        switch (key.Key)
        {
            case ConsoleKey.LeftArrow:
                Left();
                return true;
            case ConsoleKey.RightArrow:
                Right();
                return true;
            case ConsoleKey.Home:
            case ConsoleKey.A when control:
                Home();
                return true;
            case ConsoleKey.End:
            case ConsoleKey.E when control:
                End();
                return true;
            case ConsoleKey.Backspace:
                Backspace();
                return true;
            case ConsoleKey.Delete:
                Delete();
                return true;
            case ConsoleKey.U when control:
                ClearToStart();
                return true;
            case ConsoleKey.W when control:
                DeleteWordBack();
                return true;
            default:
                if (control || char.IsControl(key.KeyChar))
                    return false;

                Insert(key.KeyChar);
                return true;
        }
    }

    // The part of the text that fits into `room` columns with the caret inside it, and the column of the caret there.
    // An end that is cut off shows `…`. One column is kept for the caret that stands after the last character.
    public (string Text, int Caret) View(int room)
    {
        if (Text.Length < room)
            return (Text, Caret);

        var start = Math.Clamp(Caret - room / 2, 0, Text.Length - room + 1);
        var part = Text.Substring(start, Math.Min(room - 1, Text.Length - start)).ToCharArray();
        if (start > 0 && Caret > start)
            part[0] = '…';
        if (start + part.Length < Text.Length && Caret < start + part.Length - 1)
            part[^1] = '…';
        return (new string(part), Caret - start);
    }

    void Remove(int from, int to)
    {
        if (from < 0 || to > Text.Length)
            return;

        Text = Text.Remove(from, to - from);
        Caret = from;
    }
}
