namespace TooBusy.Cli.Terminal;

// The terminal as a screen needs it: its keys, its size, and what only a real terminal does.
// `TakeControlC` makes Ctrl+C a key like any other while a screen is open, so that the screen can ask before it leaves.
// `WatchSize` calls back when the size of the window changes; what it gives is disposed to stop watching.
// `Every` calls back again and again after the given time, until what it gives is disposed.
// `Lost` says why the terminal went away, as `TerminalWriter` found when a write to it failed.
public sealed record TerminalDevice(Func<ConsoleKeyInfo> ReadKey, Func<(int Width, int Height)> Size)
{
    // Tells that more keys are typed already, as in a paste.
    public Func<bool> KeyWaiting { get; init; } = () => false;

    public Action<bool> TakeControlC { get; init; } = _ => { };

    public Func<Action, IDisposable?> WatchSize { get; init; } = _ => null;

    public Func<TimeSpan, Action, IDisposable?> Every { get; init; } = (_, _) => null;

    public Func<string?> Lost { get; init; } = () => null;
}
