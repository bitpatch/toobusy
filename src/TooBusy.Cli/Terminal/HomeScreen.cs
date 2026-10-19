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
    SkipLabels,
    Exit,
}

// What a run would take, as the menu shows it: the number of its tasks and the task that was interrupted before. It
// is counted when it is first asked for, kept, and counted anew once it is forgotten. Null is what cannot be found
// out.
public sealed class TaskCount(Func<Task<QueueOutlook?>> count)
{
    Task<QueueOutlook?>? counting;

    // The count that is going on or is done. One that failed found nothing out.
    public Task<QueueOutlook?> Counting => counting ??= count();

    public void Forget() => counting = null;
}

// What a row of the menu waits for before it opens what it opens, as the menu watches it: what stands at the row
// meanwhile, when that changes, and whether the wait is over.
public interface IHomeWait
{
    // The row that waits.
    HomeAction Row { get; }

    // What stands at the row before the dots of the wait; empty for nothing.
    string Note { get; }

    // Done when the note changes or the wait is over; once it is over it is done for good.
    Task Changed { get; }

    bool Over { get; }
}

// A wait for work that has nothing to say of itself but that it goes on, as the settings that are opened.
public sealed class HomeWork(HomeAction row, Task work) : IHomeWait
{
    public HomeAction Row => row;

    public string Note => "";

    public Task Changed => work;

    public bool Over => work.IsCompleted;
}

// The checks of `doctor` before a run that the menu opens, as the menu watches them: the check that is running, when
// something of it changes, and the checks that failed once all of them are over.
public sealed class RunChecks : ICheckupView, IHomeWait
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

    public HomeAction Row => HomeAction.Run;

    public string Note => Running is { } check ? $"checking: {check}" : "";

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
    // `assistant` is the chosen model and effort, `milestone` the chosen milestone, and `skipped` the labels whose
    // tasks the runs of the menu leave out, empty for none. `Run` says
    // how many tasks a run would take: while they are counted the dots of a wait run there and Enter does nothing,
    // and it does not open a run that would take none either: it counts again, for what has changed meanwhile. A
    // task that was interrupted is said there instead of the number, in the colour of a warning: it is the one a
    // run takes first.
    // Escape, asked twice, is the same as `Exit`: the row names the key at its right, as `Back` does, so the keys of
    // the page do not name it again.
    public static async Task<HomeAction> AskAsync(Page page, string assistant, string milestone, IReadOnlyList<string> skipped, TaskCount tasks) =>
        await PickAsync(page, assistant, milestone, skipped, tasks, null) ?? HomeAction.Exit;

    // The same menu while a row waits for what it opens, as `Run` for the checks before a run and `Settings` for the
    // settings: what the wait says stands at the row before the dots of a wait, Enter does nothing there, and the
    // other rows are chosen as ever, from the pointer on the row that waits. When the wait is over the menu gives
    // the row by itself. Escape stops the wait at once, and null is given then: `Exit` does not name the key
    // meanwhile, and the keys say `esc stop`.
    public static Task<HomeAction?> WaitAsync(Page page, string assistant, string milestone, IReadOnlyList<string> skipped, TaskCount tasks, IHomeWait wait) =>
        PickAsync(page, assistant, milestone, skipped, tasks, wait);

    static async Task<HomeAction?> PickAsync(Page page, string assistant, string milestone, IReadOnlyList<string> skipped, TaskCount tasks, IHomeWait? wait)
    {
        page.EscapeLeaves = wait is null;
        page.Keys = wait is null ? "" : "esc stop";
        Choice Waiting(HomeAction row) => new(Name(row), Note: wait!.Note, Off: true, Waits: true);
        IReadOnlyList<Choice> Choices() =>
        [
            wait?.Row == HomeAction.Run ? Waiting(HomeAction.Run) : Run(tasks.Counting, page.Width),
            new(Name(HomeAction.Assistant), Note: assistant),
            new(Name(HomeAction.Milestone), Note: milestone),
            wait?.Row == HomeAction.Settings ? Waiting(HomeAction.Settings) : new(Name(HomeAction.Settings)),
            skipped.Count == 0 ? new(Name(HomeAction.SkipLabels), Note: "none", NoteTone: Tone.Muted) : new(Name(HomeAction.SkipLabels), Note: string.Join(", ", skipped)),
            new(Name(HomeAction.Exit), wait is null ? Picker.BackKey : ""),
        ];

        // The count at `Run` goes on while another row waits, and is drawn when it is done.
        Task Changed()
        {
            if (wait is null)
                return tasks.Counting;
            return wait.Row == HomeAction.Run || tasks.Counting.IsCompleted ? wait.Changed : Task.WhenAny(tasks.Counting, wait.Changed);
        }

        var picked = await Picker.PickAsync(
            page,
            [Line.Of("What to do", Tone.Strong)],
            Choices,
            Changed,
            index =>
            {
                if (index == (int)HomeAction.Run && wait?.Row != HomeAction.Run && tasks.Counting.IsCompleted)
                    tasks.Forget();
            },
            () => wait is { Over: true } ? (int)wait.Row : null,
            at: wait is null ? 0 : (int)wait.Row,
            back: false);
        return picked is { } index ? (HomeAction)index : null;
    }

    // The names of the rows, in the order of `HomeAction`.
    static readonly string[] Names = ["Run", "Assistant", "Milestone", "Settings", "Skip labels", "Exit"];

    static string Name(HomeAction row) => Names[(int)row];

    // What stands before the note of `Run`: the margin, the pointer, the widest name, the two spaces after it, and
    // the column that is kept free at the edge.
    static readonly int BeforeNote = 1 + 2 + Names.Max(name => name.Length) + 2 + 1;

    // A count that has not ended waits; one that failed, or found nothing out, cannot read the tasks.
    static Choice Run(Task<QueueOutlook?> counting, int width) => counting.Status switch
    {
        TaskStatus.RanToCompletion => Counted(counting.Result, width),
        TaskStatus.Faulted or TaskStatus.Canceled => Counted(null, width),
        _ => new(Name(HomeAction.Run), Off: true, Waits: true),
    };

    static Choice Counted(QueueOutlook? outlook, int width) => outlook switch
    {
        null => new(Name(HomeAction.Run), "the tasks cannot be read"),
        { Interrupted: { } task } => new(Name(HomeAction.Run), Note: Cut("interrupted: " + Spoken.Task(task), width - BeforeNote), NoteTone: Tone.Warning),
        { Tasks: 0 } => new(Name(HomeAction.Run), Note: "no tasks", Off: true),
        { Tasks: var count } => new(Name(HomeAction.Run), Note: Spoken.Tasks(count)),
    };

    // The text as long as the room allows: the end of what does not fit gives way to an ellipsis.
    static string Cut(string text, int room) => text.Length <= room ? text : text[..Math.Max(1, room - 1)] + "…";
}
