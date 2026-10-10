using TooBusy.Cli.Terminal;
using TooBusy.Core.Queue;
using TooBusy.Core.Setup;
using TooBusy.Infrastructure.Settings;

namespace TooBusy.Cli;

// What happens on the page of toobusy from the moment it is opened: the setup, the choice of the milestone, the menu
// and the run, each a screen that takes the page for a while. It remembers what was done, for the report that is
// left in the terminal when the page is closed.
public sealed class Session(Page page, Workbench bench)
{
    IReadOnlyList<Milestone>? open;
    MilestoneStanding standing = MilestoneStanding.Of(null, null);
    bool read;

    // The last setup that was gone through; null when there was none.
    public SetupResult? Setup { get; private set; }

    // The milestone that was chosen on the page; null when none was.
    public MilestoneChoice? Chosen { get; private set; }

    // Opens with the menu or, when asked, with the run. A project that is not set up is set up first, and a user who
    // has no milestone to work on chooses one; leaving either of them leaves the page.
    public async Task HomeAsync(bool run, CancellationToken cancellationToken)
    {
        if (bench.Settings.Load()?.Settings is null)
        {
            var first = await SetupAsync(leavesPage: true, cancellationToken);
            if (first.Outcome is not (SetupOutcome.Written or SetupOutcome.NothingToChange) || bench.Settings.Load()?.Settings is null)
                return;
        }

        await ReadAsync(cancellationToken);
        if (!standing.Ready && !await ChooseMilestoneAsync(leavesPage: true, cancellationToken))
            return;

        HomeAction? action = run ? HomeAction.Run : null;
        string? note = null;
        while (true)
        {
            Clear("", note);
            switch (action ?? HomeScreen.Ask(page, standing.Name))
            {
                case HomeAction.Run:
                    Clear("Running the queue", "Running the tasks is not built yet.");
                    if (new RunScreen(page).Run() == RunEnd.Exit)
                        return;
                    note = null;
                    break;
                case HomeAction.Milestone:
                    note = await ChooseMilestoneAsync(leavesPage: false, cancellationToken) ? null : "The milestone is left as it was.";
                    break;
                case HomeAction.Settings:
                    note = Ending(await SetupAsync(leavesPage: false, cancellationToken), bench.Demo).Text;
                    break;
                default:
                    return;
            }

            action = null;
        }
    }

    // `leavesPage` tells that there is nowhere to go back to from the first question.
    public async Task<SetupResult> SetupAsync(bool leavesPage, CancellationToken cancellationToken)
    {
        Clear("Setting up this project", null);
        page.Body = [];
        return Setup = await new ProjectSetup(new SetupScreen(page, leavesPage), bench.Environment, bench.Tracker, bench.Boards, bench.Settings).RunAsync(cancellationToken);
    }

    // Asks for the milestone and remembers the answer; false when the user went back instead.
    public async Task<bool> ChooseMilestoneAsync(bool leavesPage, CancellationToken cancellationToken)
    {
        if (!read)
            await ReadAsync(cancellationToken);

        Clear("Choosing the milestone", null);
        page.EscapeLeaves = leavesPage;
        page.Keys = leavesPage ? "esc exit" : "esc back";
        if (MilestoneScreen.Ask(page, open, standing) is not { } choice)
            return false;

        bench.Personal.SaveMilestone(choice);
        Chosen = choice;
        standing = MilestoneStanding.Of(choice, open);
        return true;
    }

    // How a setup ended, in one line, and whether that is a failure.
    public static (bool Failed, string Text) Ending(SetupResult result, bool demo) => result.Outcome switch
    {
        SetupOutcome.Written when demo => (false, $"Demo: nothing was made, linked or written; {SettingsFile.DisplayPath} is left as it was."),
        SetupOutcome.Written when result.SettingsWritten => (false, $"The settings are written to {SettingsFile.DisplayPath}. Commit the file."),
        SetupOutcome.Written => (false, "The project is linked to the repository."),
        SetupOutcome.NothingToChange => (false, "Nothing to change: the settings already say this."),
        SetupOutcome.Declined => (true, "The setup was declined: nothing was changed."),
        SetupOutcome.Failed => (true, $"{result.Failure} The settings were not written."),
        _ => (true, "The setup was left: nothing was changed."),
    };

    async Task ReadAsync(CancellationToken cancellationToken)
    {
        Clear("", null);
        page.Draw([], [Line.Of("Reading the milestones…", Tone.Muted)], "");
        open = await bench.ReadMilestonesAsync(cancellationToken);
        standing = MilestoneStanding.Of(bench.Personal.LoadMilestone(), open);
        read = true;
    }

    // Takes the page back from the screen that had it: what is settled in its body, with a note under it, and
    // nothing of that screen around.
    void Clear(string status, string? note)
    {
        page.Status = bench.Demo ? status.Length == 0 ? "demo" : $"{status} · demo" : status;
        page.Foot = Line.Empty;
        page.Keys = "";
        page.EscapeLeaves = false;

        var body = new List<Line>();
        if (bench.Settings.Load()?.Settings is { } settings)
        {
            body.Add(Picker.Answer("Project", settings.Tracker.Board ?? "none"));
            body.Add(Picker.Answer("Blocking labels", settings.Queue.Labels.Blocking.Count == 0 ? "none" : string.Join(", ", settings.Queue.Labels.Blocking)));
            body.Add(Picker.Answer("Labels to take", settings.Queue.Labels.Take.Count == 0 ? "any task" : string.Join(", ", settings.Queue.Labels.Take)));
        }

        if (standing.Ready)
        {
            body.Add(Picker.Answer("Milestone", standing switch
            {
                { State: MilestoneState.NoMilestone } => "none: tasks are taken whatever their milestone",
                { Milestone: { } milestone } => $"{milestone.Title} · {MilestoneScreen.Describe(milestone)}",
                _ => $"{standing.Name} · not checked: the milestones cannot be read",
            }));
        }

        if (note is not null)
            body.AddRange([Line.Empty, Line.Of(note, Tone.Muted)]);
        page.Body = body;
    }
}
