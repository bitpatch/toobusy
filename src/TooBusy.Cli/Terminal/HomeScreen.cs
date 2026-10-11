using TooBusy.Core.Doctor;
using TooBusy.Core.Queue;
using TooBusy.Core.Run;

namespace TooBusy.Cli.Terminal;

public enum HomeAction
{
    Run,
    Assistant,
    Milestone,
    Settings,
    Exit,
}

// What a run would take, as the menu shows it: the number of its tasks and the task that was interrupted before. It
// is counted when it is first asked for, kept, and counted anew once it is forgotten. Null is what cannot be found
// out.
public sealed class TaskCount(Func<Task<QueueOutlook?>> count)
{
    Task<QueueOutlook?>? counting;

    // The count that is going on or is done; it never fails.
    public Task<QueueOutlook?> Counting => counting ??= count();

    public void Forget() => counting = null;
}

// The checks of `doctor` before a run that the menu opens, as the menu watches them: the check that is running, when
// something of it changes, and the checks that failed once all of them are over.
public sealed class RunChecks : ICheckupView
{
    readonly Lock telling = new();
    readonly List<CheckResult> failed = [];
    TaskCompletionSource changed = new(TaskCreationOptions.RunContinuationsAsynchronously);
    string? running;
    bool over;

    // The check that is running, or the one that ran last; null before the first of them.
    public string? Running
    {
        get
        {
            lock (telling)
                return running;
        }
    }

    public bool Over
    {
        get
        {
            lock (telling)
                return over;
        }
    }

    public IReadOnlyList<CheckResult> Failed
    {
        get
        {
            lock (telling)
                return [.. failed];
        }
    }

    // Done when another check starts or the checks are over; once they are over it is done for good.
    public Task Changed
    {
        get
        {
            lock (telling)
                return over ? Task.CompletedTask : changed.Task;
        }
    }

    public void Start(string name) => Tell(() => running = name);

    public void Done(CheckResult result)
    {
        lock (telling)
        {
            if (result.State == CheckState.Failed)
                failed.Add(result);
        }
    }

    // The checks are over, however they ended.
    public void End() => Tell(() => over = true);

    void Tell(Action change)
    {
        TaskCompletionSource told;
        lock (telling)
        {
            change();
            told = changed;
            changed = new(TaskCreationOptions.RunContinuationsAsynchronously);
        }

        told.TrySetResult();
    }
}

// The menu toobusy opens with: what can be done in a project that is set up and has its milestone, its model and
// its effort chosen.
public static class HomeScreen
{
    // `assistant` is the chosen model and effort, and `milestone` the chosen milestone, in a few words. `Run` says
    // how many tasks a run would take: while they are counted the dots of a wait run there and Enter does nothing,
    // and it does not open a run that would take none either: it counts again, for what has changed meanwhile. A
    // task that was interrupted is said there instead of the number, in the colour of a warning: it is the one a
    // run takes first.
    // Escape, asked twice, is the same as `Exit`: the row names the key at its right, as `Back` does, so the keys of
    // the page do not name it again.
    // `checks` are the checks before a run that `Run` has asked for: while they go on the check that is running
    // stands at `Run` before the dots of a wait, Enter does nothing there, and the other rows are chosen as ever.
    // When the checks are over the menu gives `Run` by itself.
    public static async Task<HomeAction> AskAsync(Page page, string assistant, string milestone, TaskCount tasks, RunChecks? checks = null)
    {
        page.EscapeLeaves = true;
        page.Keys = "";
        IReadOnlyList<Choice> Choices() =>
        [
            checks is null ? Run(tasks.Counting, page.Width) : new("Run", Note: checks.Running is { } check ? $"checking: {check}" : "", Off: true, Waits: true),
            new("Assistant", Note: assistant),
            new("Milestone", Note: milestone),
            new("Settings"),
            new("Exit", Picker.BackKey),
        ];

        var picked = await Picker.PickAsync(
            page,
            [Line.Of("What to do", Tone.Strong)],
            Choices,
            () => checks is null ? tasks.Counting : checks.Changed,
            _ =>
            {
                if (checks is null && tasks.Counting.IsCompleted)
                    tasks.Forget();
            },
            () => checks is { Over: true } ? (int)HomeAction.Run : null);
        return picked is { } index ? (HomeAction)index : HomeAction.Exit;
    }

    // What stands before the note of `Run`: the margin, the pointer, the widest name, `Assistant`, the two spaces
    // after it, and the column that is kept free at the edge.
    const int BeforeNote = 1 + 2 + 9 + 2 + 1;

    static Choice Run(Task<QueueOutlook?> counting, int width) => counting switch
    {
        { IsCompleted: false } => new("Run", Off: true, Waits: true),
        { Result: null } => new("Run", "the tasks cannot be read"),
        { Result: { Interrupted: { } task } } => new("Run", Note: Cut("interrupted: " + Spoken.Task(task), width - BeforeNote), NoteTone: Tone.Warning),
        { Result: { Tasks: 0 } } => new("Run", Note: "no tasks", Off: true),
        { Result: { Tasks: var count } } => new("Run", Note: Spoken.Tasks(count)),
    };

    // The text as long as the room allows: the end of what does not fit gives way to an ellipsis.
    static string Cut(string text, int room) => text.Length <= room ? text : text[..Math.Max(1, room - 1)] + "…";
}
