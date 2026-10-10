namespace TooBusy.Cli.Tests;

// Keys for a test to press, in order. Reading past the last one fails the test instead of waiting for ever.
public sealed class Keys
{
    readonly Queue<ConsoleKeyInfo> keys = new();

    public static ConsoleKeyInfo Enter { get; } = Of(ConsoleKey.Enter, '\r');

    public static ConsoleKeyInfo Up { get; } = Of(ConsoleKey.UpArrow);

    public static ConsoleKeyInfo Down { get; } = Of(ConsoleKey.DownArrow);

    public static ConsoleKeyInfo Space { get; } = Of(ConsoleKey.Spacebar, ' ');

    public static ConsoleKeyInfo Backspace { get; } = Of(ConsoleKey.Backspace, '\b');

    public static ConsoleKeyInfo No { get; } = Of(ConsoleKey.N, 'n');

    public static ConsoleKeyInfo Left { get; } = Of(ConsoleKey.LeftArrow);

    public static ConsoleKeyInfo Right { get; } = Of(ConsoleKey.RightArrow);

    public static ConsoleKeyInfo Home { get; } = Of(ConsoleKey.Home);

    public static ConsoleKeyInfo End { get; } = Of(ConsoleKey.End);

    public static ConsoleKeyInfo Delete { get; } = Of(ConsoleKey.Delete);

    public static ConsoleKeyInfo Tab { get; } = Of(ConsoleKey.Tab, '\t');

    public static ConsoleKeyInfo ShiftTab { get; } = new('\t', ConsoleKey.Tab, shift: true, alt: false, control: false);

    public static ConsoleKeyInfo Yes { get; } = Of(ConsoleKey.Y, 'y');

    public static ConsoleKeyInfo ControlC { get; } = Control('c');

    public static ConsoleKeyInfo Escape { get; } = Of(ConsoleKey.Escape, '\u001b');

    // Whether a key is pressed and not read yet.
    public bool Waiting => keys.Count > 0;

    // A letter pressed together with Ctrl.
    public static ConsoleKeyInfo Control(char letter) =>
        new((char)(char.ToUpperInvariant(letter) - '@'), ConsoleKey.A + (char.ToUpperInvariant(letter) - 'A'), shift: false, alt: false, control: true);

    public Keys Press(params ConsoleKeyInfo[] pressed)
    {
        foreach (var key in pressed)
            keys.Enqueue(key);
        return this;
    }

    public Keys Type(string text)
    {
        foreach (var character in text)
            keys.Enqueue(Of(ConsoleKey.NoName, character));
        return this;
    }

    public ConsoleKeyInfo Read() => keys.Count > 0 ? keys.Dequeue() : throw new InvalidOperationException("The prompt asks for a key that the test did not press.");

    static ConsoleKeyInfo Of(ConsoleKey key, char character = '\0') => new(character, key, shift: false, alt: false, control: false);
}
