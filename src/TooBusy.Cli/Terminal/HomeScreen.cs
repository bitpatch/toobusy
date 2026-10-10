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

// The number of tasks a run would take, as the menu shows it: it is counted when it is first asked for, kept, and
// counted anew once it is forgotten. Null is a number that cannot be found out.
public sealed class TaskCount(Func<Task<int?>> count)
{
    Task<int?>? counting;

    // The count that is going on or is done; it never fails.
    public Task<int?> Counting => counting ??= count();

    public void Forget() => counting = null;
}

// The menu toobusy opens with: what can be done in a project that is set up and has its milestone, its model and
// its effort chosen.
public static class HomeScreen
{
    // `assistant` is the chosen model and effort, and `milestone` the chosen milestone, in a few words. `Run` says
    // how many tasks a run would take: while they are counted the dots of a wait run there and Enter does nothing,
    // and it does not open a run that would take none either: it counts again, for what has changed meanwhile.
    // Escape, asked twice, is the same as `Exit`: the row names the key at its right, as `Back` does, so the keys of
    // the page do not name it again.
    public static async Task<HomeAction> AskAsync(Page page, string assistant, string milestone, TaskCount tasks)
    {
        page.EscapeLeaves = true;
        page.Keys = "";
        IReadOnlyList<Choice> Choices() =>
        [
            Run(tasks.Counting),
            new("Assistant", Note: assistant),
            new("Milestone", Note: milestone),
            new("Settings"),
            new("Exit", Picker.BackKey),
        ];

        var picked = await Picker.PickAsync(page, [Line.Of("What to do", Tone.Strong)], Choices, () => tasks.Counting, _ =>
        {
            if (tasks.Counting.IsCompleted)
                tasks.Forget();
        });
        return picked is { } index ? (HomeAction)index : HomeAction.Exit;
    }

    static Choice Run(Task<int?> counting) => counting switch
    {
        { IsCompleted: false } => new("Run", Off: true, Waits: true),
        { Result: null } => new("Run", "the tasks cannot be read"),
        { Result: 0 } => new("Run", Note: "no tasks", Off: true),
        { Result: { } count } => new("Run", Note: Spoken.Tasks(count)),
    };
}
