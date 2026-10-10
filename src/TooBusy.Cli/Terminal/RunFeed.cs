using System.Collections.Concurrent;
using TooBusy.Core.Run;

namespace TooBusy.Cli.Terminal;

// What a run tells, kept for the page that shows it: the run speaks from its own thread, and the page takes what was
// said when it draws. `Changed` is done when there is something new since the page took last.
public sealed class RunFeed : IRunView
{
    readonly ConcurrentQueue<RunLine> lines = new();
    volatile RunStatus status = new(RunPhase.Preparing, "");
    volatile TaskCompletionSource changed = New();

    public RunStatus Status => status;

    public Task Changed => changed.Task;

    public void Say(RunLine line)
    {
        lines.Enqueue(line);
        changed.TrySetResult();
    }

    public void Show(RunStatus status)
    {
        this.status = status;
        changed.TrySetResult();
    }

    // Moves the lines that were said to the log of the page. What is said from now on is something new.
    public void Take(List<RunLine> log)
    {
        changed = New();
        while (lines.TryDequeue(out var line))
            log.Add(line);
    }

    static TaskCompletionSource New() => new(TaskCreationOptions.RunContinuationsAsynchronously);
}
