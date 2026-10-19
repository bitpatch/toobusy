namespace TooBusy.Cli.Tests;

// Keys for a test to press, in order. Reading past the last one fails the test instead of waiting for ever.
// The keys after a hold are not pressed before what the hold waits for has happened: a page that goes on by itself
// is given time to get there. That time has an end too: a hold that is asked about for longer than the patience
// fails the test, whether the page reads a key or only looks whether one is pressed, so that a page that never gets
// where the test waits for it cannot go on for ever. A key may also be pressed until the page shows something, so
// that a test goes to a line of a list by what the line says and not by counting the lines before it.
public sealed class Keys
{
    // How long a key that is held back is waited for by a page that cannot go on without it. A test that waits for
    // the page in another way waits as long.
    public static TimeSpan Patience { get; } = TimeSpan.FromSeconds(20);

    // How many times a key is pressed at most for the page to show what it is pressed until: more than any list has
    // lines.
    const int Presses = 100;

    readonly Queue<Step> keys = new();

    // Since when the hold that is first in the line has been asked about; null when it has not been yet.
    System.Diagnostics.Stopwatch? held;

    // How many times the key that is pressed until something is shown has been pressed so far, and what the page
    // showed when it was pressed last.
    int pressed;
    string? before;

    // What the page shows now, the text of the frame it drew last, which `Until` looks at: the terminal the keys are
    // read from says it.
    public Func<string>? Shown { get; set; }

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

    // A letter pressed alone, with its key as the terminal names it: `Type` gives the character only, as a paste does.
    public static ConsoleKeyInfo Letter(char letter) => Of(ConsoleKey.A + (char.ToUpperInvariant(letter) - 'A'), letter);

    // A letter pressed together with Ctrl.
    public static ConsoleKeyInfo Control(char letter) =>
        new((char)(char.ToUpperInvariant(letter) - '@'), ConsoleKey.A + (char.ToUpperInvariant(letter) - 'A'), shift: false, alt: false, control: true);

    public Keys Press(params ConsoleKeyInfo[] pressed)
    {
        foreach (var key in pressed)
            keys.Enqueue(new Step(key));
        return this;
    }

    public Keys Type(string text)
    {
        foreach (var character in text)
            keys.Enqueue(new Step(Of(ConsoleKey.NoName, character)));
        return this;
    }

    // Holds the keys that follow back until this is so.
    public Keys Hold(Func<bool> until)
    {
        keys.Enqueue(new Step(default, until));
        return this;
    }

    // Presses the key as many times as it takes for the page to show the text, none when it shows it already, and
    // goes on with the keys that follow: `Until("❯ Settings", Keys.Down)` goes down a list to the line it names.
    // After each press the key waits for the page to draw again, so the text is looked for in what the key brought,
    // on a page that reads its keys and on one that only looks whether one is pressed alike. It is for a page that
    // draws in answer to its keys: one that also draws by itself, as the page of a run does whenever the run says
    // something, may draw before it takes the key, and is held for by what it shows instead.
    public Keys Until(string shown, ConsoleKeyInfo key)
    {
        keys.Enqueue(new Step(key, () => (Shown ?? throw new InvalidOperationException("The keys do not know what the page shows."))().Contains(shown, StringComparison.Ordinal), shown));
        return this;
    }

    public ConsoleKeyInfo Read()
    {
        while (true)
        {
            var (any, held) = Next();
            if (!any)
                throw new InvalidOperationException("The prompt asks for a key that the test did not press.");
            if (held)
            {
                Thread.Sleep(1);
                continue;
            }

            if (keys.Peek() is not { Shown: { } shown } step)
                return keys.Dequeue().Key;
            before = Shown!();
            if (++pressed > Presses)
                throw new InvalidOperationException($"The test pressed {step.Key.Key} {Presses} times, and the page never showed \"{shown}\". It shows:\n{before}");
            return step.Key;
        }
    }

    // Whether there is a key, and whether it is held back. A hold whose time has come is gone, and so is a key that
    // is pressed until the page shows what it does; that key is held back while the page has not drawn what it
    // brought. A key that is held back for longer than the patience fails the test.
    (bool Any, bool Held) Next()
    {
        while (keys.Count > 0 && keys.Peek().Until is { } until)
        {
            if (!until())
            {
                if (keys.Peek().Shown is not null && (before is null || Shown!() != before))
                {
                    held = null;
                    return (true, false);
                }

                held ??= System.Diagnostics.Stopwatch.StartNew();
                if (held.Elapsed > Patience)
                    throw new InvalidOperationException("The test holds a key back for something that did not happen.");
                return (true, true);
            }

            keys.Dequeue();
            (held, pressed, before) = (null, 0, null);
        }

        return (keys.Count > 0, false);
    }

    static ConsoleKeyInfo Of(ConsoleKey key, char character = '\0') => new(character, key, shift: false, alt: false, control: false);

    // A key to press, with what it waits for: a hold presses nothing until it is so, and a key that is pressed until
    // the page shows a text is pressed until it is so.
    readonly record struct Step(ConsoleKeyInfo Key, Func<bool>? Until = null, string? Shown = null);
}
