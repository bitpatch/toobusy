using TooBusy.Core.Queue;

namespace TooBusy.Core.Run;

// The tracker as a run calls it: `say` is where the run says what happens to a call.
sealed class RunTracker(ITaskTracker tracker, IClock clock, RunPlan plan, RunPolicy policy, Action<RunMark, string> say)
{
    // A comment is made once and no kill cuts it: one whose answer was lost may be written already, and a second would
    // stand twice.
    public Task CommentAsync(int number, string text) => tracker.CommentAsync(number, text, CancellationToken.None);

    // The calls of the tracker that are safe to make twice, as a run makes them: each is made again when GitHub did not
    // answer it, and the log says so in the words of what the call does. An `uncut` call is one that a kill does not
    // cut, as what is done for good measure, what is told of a task that is settled or paused, or what is done after
    // the kill; its pauses are cut all the same.
    public Task<IReadOnlyList<QueueTask>> OpenTasksAsync(CancellationToken kill) =>
        AgainAsync("the open tasks are read", calls => tracker.ReadOpenAsync(plan.Milestone, calls), kill);

    public Task<QueueTask?> ClosedTaskAsync(int number, CancellationToken kill, bool uncut = false) =>
        AgainAsync($"#{number} is read", calls => tracker.ReadClosedAsync(number, calls), kill, uncut);

    public Task MoveAsync(int number, BoardStatus to, CancellationToken kill, bool uncut = false) =>
        AgainAsync($"#{number} is moved to {plan.Rules.Statuses.NameOf(to)}", calls => tracker.SetStatusAsync(number, to, calls), kill, uncut);

    public Task AssignAsync(int number, CancellationToken kill) =>
        AgainAsync($"#{number} is assigned to the current user", calls => tracker.AssignAsync(number, calls), kill);

    public Task LabelAsync(int number, string label, CancellationToken kill, bool uncut = false) =>
        AgainAsync($"the label {label} is put on #{number}", calls => tracker.AddLabelAsync(number, label, calls), kill, uncut);

    public Task UnlabelAsync(int number, string label, CancellationToken kill) =>
        AgainAsync($"the label {label} is taken off #{number}", calls => tracker.RemoveLabelAsync(number, label, calls), kill);

    public Task CloseTaskAsync(int number, CancellationToken kill, bool uncut = false) =>
        AgainAsync($"#{number} is closed", calls => tracker.CloseAsync(number, calls), kill, uncut);

    // Makes a call that is safe to make twice, and makes it again when it failed in a way that may pass, after each
    // pause of the policy: the failure of the last try is the one that stands. `what` says what the call does, and
    // `call` makes it with the token it is cut by: none for one that is `uncut`. A kill ends a pause at once, and after
    // a kill nothing is made again: the owner waits for what is done then. An uncut call whose pause the kill ends
    // fails with the failure it had.
    async Task<T> AgainAsync<T>(string what, Func<CancellationToken, Task<T>> call, CancellationToken kill, bool uncut = false)
    {
        for (var tried = 0; ; tried++)
        {
            TrackerException failed;
            try
            {
                return await call(uncut ? CancellationToken.None : kill);
            }
            catch (TrackerException passing) when (passing.MayPass && tried < policy.RetryPauses.Count && !kill.IsCancellationRequested)
            {
                failed = passing;
            }

            var pause = policy.RetryPauses[tried];
            say(RunMark.Attention, $"GitHub did not answer: {what} again in {Spoken.Time(pause)}");
            try
            {
                await clock.DelayAsync(pause, kill);
            }
            catch (OperationCanceledException) when (uncut && kill.IsCancellationRequested)
            {
                throw failed;
            }
        }
    }

    async Task AgainAsync(string what, Func<CancellationToken, Task> call, CancellationToken kill, bool uncut = false) =>
        await AgainAsync(what, async calls =>
        {
            await call(calls);
            return true;
        }, kill, uncut);

    // A change of the tracker that a refusal does not stop: what was refused is added to `missing`, and the change
    // counts as not made. True when it was made.
    public static async Task<bool> Try(List<string> missing, Func<Task> change)
    {
        try
        {
            await change();
            return true;
        }
        catch (TrackerException refused)
        {
            missing.Add(refused.Message);
            return false;
        }
    }
}
