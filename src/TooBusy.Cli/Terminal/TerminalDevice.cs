namespace TooBusy.Cli.Terminal;

// The terminal as a screen needs it: its keys, its size, and two things only a real terminal has to do.
// `TakeControlC` makes Ctrl+C a key like any other while a screen is open, so that the screen can ask before it leaves.
// `WatchSize` calls back when the size of the window changes; what it gives is disposed to stop watching.
// `Every` calls back again and again after the given time, until what it gives is disposed.
public sealed record TerminalDevice(Func<ConsoleKeyInfo> ReadKey, Func<(int Width, int Height)> Size)
{
    // Tells that more keys are typed already, as in a paste.
    public Func<bool> KeyWaiting { get; init; } = () => false;

    public Action<bool> TakeControlC { get; init; } = _ => { };

    public Func<Action, IDisposable?> WatchSize { get; init; } = _ => null;

    public Func<TimeSpan, Action, IDisposable?> Every { get; init; } = (_, _) => null;
}
