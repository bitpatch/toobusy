using System.Collections.Concurrent;
using TooBusy.Core.Run;

namespace TooBusy.Cli.Terminal;

// What a run tells, kept for the page that shows it: the run speaks from its own thread, and the page takes what was
// said when it draws. `Changed` is done when there is something new since the page took last.
public sealed class RunFeed : IRunView
{
    // The lines of the log and the tasks that are over, in the order the run told them.
    readonly ConcurrentQueue<object> told = new();
    volatile RunStatus status = new(RunPhase.Preparing, "");
    volatile TaskCompletionSource changed = New();

    public RunStatus Status => status;

    public Task Changed => changed.Task;

    public void Say(RunLine line) => Tell(line);

    public void Report(TaskEnd ended) => Tell(ended);

    public void Show(RunStatus status)
    {
        this.status = status;
        changed.TrySetResult();
    }

    // Gives what was told since it was taken last: each is a line of the log or a task that is over. What is told
    // from now on is something new.
    public IReadOnlyList<object> Take()
    {
        changed = New();
        var taken = new List<object>();
        while (told.TryDequeue(out var said))
            taken.Add(said);
        return taken;
    }

    void Tell(object said)
    {
        told.Enqueue(said);
        changed.TrySetResult();
    }

    static TaskCompletionSource New() => new(TaskCreationOptions.RunContinuationsAsynchronously);
}
