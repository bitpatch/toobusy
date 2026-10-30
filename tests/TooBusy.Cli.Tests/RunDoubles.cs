using System.Collections.Concurrent;
using System.Threading.Channels;
using TooBusy.Core.Run;

namespace TooBusy.Cli.Tests;

// A clock that makes nobody wait: a delay moves its time on and is over at once.
public sealed class InstantClock : IClock
{
    public static readonly DateTimeOffset Start = new(2030, 1, 1, 9, 0, 0, TimeSpan.Zero);

    long ticks = Start.UtcTicks;

    public DateTimeOffset Now => new(Interlocked.Read(ref ticks), TimeSpan.Zero);

    public Task DelayAsync(TimeSpan time, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Interlocked.Add(ref ticks, time.Ticks);
        return Task.CompletedTask;
    }
}

// A clock whose time passes as the run asks for it but twenty times faster than the real one: a demo is over in
// seconds, and a key that is held back for a moment of it still finds the run where it waits for it.
public sealed class PacedClock : IClock
{
    long ticks = InstantClock.Start.UtcTicks;

    public DateTimeOffset Now => new(Interlocked.Read(ref ticks), TimeSpan.Zero);

    public async Task DelayAsync(TimeSpan time, CancellationToken cancellationToken)
    {
        await Task.Delay(time / 20, cancellationToken);
        Interlocked.Add(ref ticks, time.Ticks);
    }
}

// A run that does what the test wrote: it says and shows what it is told to, waits for the commands the page sends,
// and ends when its play does. Being killed ends it too.
public sealed class ScriptedRun(Func<ScriptedRun, IRunView, Task<RunResult>> play) : IQueueRun
{
    readonly Channel<RunCommand> commands = Channel.CreateUnbounded<RunCommand>();
    CancellationToken kill;
    volatile bool killed;

    public bool Killed => killed;

    public async Task<RunResult> RunAsync(IRunView view, CancellationToken kill)
    {
        this.kill = kill;
        try
        {
            return await play(this, view);
        }
        catch (OperationCanceledException) when (kill.IsCancellationRequested)
        {
            killed = true;
            view.Say(new RunLine(RunMark.Interrupted, "Killed the session of #3"));
            return new RunResult(RunEnd.Killed, 0);
        }
    }

    public void Send(RunCommand command, int? task = null)
    {
        Aimed = task;
        commands.Writer.TryWrite(command);
    }

    // The task the last command was meant for.
    public int? Aimed { get; private set; }

    readonly ConcurrentQueue<string> notes = new();

    // What the page noted for the log of the run, in order.
    public IReadOnlyList<string> Notes => [.. notes];

    public void Note(string text) => notes.Enqueue(text);

    // The next command of the page; a run that is killed meanwhile stops waiting. A page that sends none within the
    // patience of the keys fails the test instead of keeping it waiting.
    public async Task<RunCommand> NextAsync()
    {
        using var patience = CancellationTokenSource.CreateLinkedTokenSource(kill);
        patience.CancelAfter(Keys.Patience);
        try
        {
            return await commands.Reader.ReadAsync(patience.Token);
        }
        catch (OperationCanceledException) when (!kill.IsCancellationRequested)
        {
            throw new InvalidOperationException($"The page sent no command in {Keys.Patience.TotalSeconds} s.");
        }
    }

    // Goes on working, as a run that only a kill ends. One that is not killed within the patience of the keys fails
    // the test instead of keeping it waiting.
    public async Task<RunResult> ForEverAsync()
    {
        await Task.Delay(Keys.Patience, kill);
        throw new InvalidOperationException($"The run was not killed in {Keys.Patience.TotalSeconds} s.");
    }
}
