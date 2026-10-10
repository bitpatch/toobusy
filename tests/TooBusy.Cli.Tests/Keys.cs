namespace TooBusy.Cli.Tests;

// Keys for a test to press, in order. Reading past the last one fails the test instead of waiting for ever.
// The keys after a hold are not pressed before what the hold waits for has happened: a page that goes on by itself
// is given time to get there. That time has an end too: a hold that is asked about for longer than the patience
// fails the test, whether the page reads a key or only looks whether one is pressed, so that a page that never gets
// where the test waits for it cannot go on for ever.
public sealed class Keys
{
    // How long a key that is held back is waited for by a page that cannot go on without it.
    static readonly TimeSpan Patience = TimeSpan.FromSeconds(20);

    readonly Queue<(ConsoleKeyInfo Key, Func<bool>? Until)> keys = new();

    // Since when the hold that is first in the line has been asked about; null when it has not been yet.
    System.Diagnostics.Stopwatch? held;

    public static ConsoleKeyInfo Enter { get; } = Of(ConsoleKey.Enter, '\r');

    public static ConsoleKeyInfo Up { get; } = Of(ConsoleKey.UpArrow);

    public static ConsoleKeyInfo Down { get; } = Of(ConsoleKey.DownArrow);

    public static ConsoleKeyInfo Space { get; } = Of(ConsoleKey.Spacebar, ' ');

    public static ConsoleKeyInfo Backspace { get; } = Of(ConsoleKey.Backspace, '\b');

    public static ConsoleKeyInfo Left { get; } = Of(ConsoleKey.LeftArrow);

    public static ConsoleKeyInfo Right { get; } = Of(ConsoleKey.RightArrow);

    public static ConsoleKeyInfo Home { get; } = Of(ConsoleKey.Home);

    public static ConsoleKeyInfo End { get; } = Of(ConsoleKey.End);

    public static ConsoleKeyInfo Delete { get; } = Of(ConsoleKey.Delete);

    public static ConsoleKeyInfo Tab { get; } = Of(ConsoleKey.Tab, '\t');

    public static ConsoleKeyInfo ShiftTab { get; } = new('\t', ConsoleKey.Tab, shift: true, alt: false, control: false);

    public static ConsoleKeyInfo ControlC { get; } = Control('c');

    public static ConsoleKeyInfo Escape { get; } = Of(ConsoleKey.Escape, '\u001b');

    // Whether a key is pressed and not read yet.
    public bool Waiting => Next() is (true, false);

    // A letter pressed together with Ctrl.
    public static ConsoleKeyInfo Control(char letter) =>
        new((char)(char.ToUpperInvariant(letter) - '@'), ConsoleKey.A + (char.ToUpperInvariant(letter) - 'A'), shift: false, alt: false, control: true);

    public Keys Press(params ConsoleKeyInfo[] pressed)
    {
        foreach (var key in pressed)
            keys.Enqueue((key, null));
        return this;
    }

    public Keys Type(string text)
    {
        foreach (var character in text)
            keys.Enqueue((Of(ConsoleKey.NoName, character), null));
        return this;
    }

    // Holds the keys that follow back until this is so.
    public Keys Hold(Func<bool> until)
    {
        keys.Enqueue((default, until));
        return this;
    }

    public ConsoleKeyInfo Read()
    {
        while (true)
        {
            var (any, held) = Next();
            if (!any)
                throw new InvalidOperationException("The prompt asks for a key that the test did not press.");
            if (!held)
                return keys.Dequeue().Key;

            Thread.Sleep(1);
        }
    }

    // Whether there is a key, and whether it is held back. A hold whose time has come is gone; one that is asked
    // about for longer than the patience fails the test.
    (bool Any, bool Held) Next()
    {
        while (keys.Count > 0 && keys.Peek().Until is { } until)
        {
            if (!until())
            {
                held ??= System.Diagnostics.Stopwatch.StartNew();
                if (held.Elapsed > Patience)
                    throw new InvalidOperationException("The test holds a key back for something that did not happen.");
                return (true, true);
            }

            keys.Dequeue();
            held = null;
        }

        return (keys.Count > 0, false);
    }

    static ConsoleKeyInfo Of(ConsoleKey key, char character = '\0') => new(character, key, shift: false, alt: false, control: false);
}
