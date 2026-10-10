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

// A run that does what the test wrote: it says and shows what it is told to, waits for the commands the page sends,
// and ends when its play does. Being killed ends it too.
public sealed class ScriptedRun(Func<ScriptedRun, IRunView, Task<RunResult>> play) : IQueueRun
{
    readonly Channel<RunCommand> commands = Channel.CreateUnbounded<RunCommand>();
    CancellationToken kill;
    volatile bool killed;
    volatile bool over;

    public bool Killed => killed;

    // Whether the play has ended.
    public bool Over => over;

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
        finally
        {
            over = true;
        }
    }

    public void Send(RunCommand command) => commands.Writer.TryWrite(command);

    // The next command of the page; a run that is killed meanwhile stops waiting.
    public async Task<RunCommand> NextAsync() => await commands.Reader.ReadAsync(kill);

    // Goes on working for ever, as a run that only a kill ends.
    public async Task<RunResult> ForEverAsync()
    {
        await Task.Delay(Timeout.Infinite, kill);
        return new RunResult(RunEnd.Emptied, 0);
    }
}
