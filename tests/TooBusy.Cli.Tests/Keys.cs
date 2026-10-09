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
