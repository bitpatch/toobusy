namespace TooBusy.Core.Processes;

// One at a time, in the order they came: for those who share something that does not take two at once, as a repository
// a fetch of each worktree goes to. Whoever is cancelled while waiting for a turn gives it up.
public sealed class Turns
{
    readonly Lock gate = new();
    Task last = Task.CompletedTask;

    public async Task TakeAsync(Func<Task> doing, CancellationToken cancellationToken) =>
        await TakeAsync(
            async () =>
            {
                await doing();
                return true;
            },
            cancellationToken);

    public async Task<T> TakeAsync<T>(Func<Task<T>> doing, CancellationToken cancellationToken)
    {
        var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Task before;
        lock (gate)
            (before, last) = (last, done.Task);

        try
        {
            await before.WaitAsync(cancellationToken);
            return await doing();
        }
        finally
        {
            done.SetResult();
        }
    }
}
