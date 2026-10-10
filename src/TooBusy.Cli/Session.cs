using TooBusy.Cli.Terminal;
using TooBusy.Core.Assistant;
using TooBusy.Core.Queue;
using TooBusy.Core.Run;
using TooBusy.Core.Setup;
using TooBusy.Infrastructure.Settings;

namespace TooBusy.Cli;

// What happens on the page of toobusy from the moment it is opened: the setup, the choices of the milestone, the
// model, the effort and the weekly limit, the menu and the run, each a screen that takes the page for a while. It
// remembers what was done, for the report that is left in the terminal when the page is closed.
public sealed class Session(Page page, Workbench bench)
{
    IReadOnlyList<Milestone>? open;
    MilestoneStanding standing = MilestoneStanding.Of(null, null);
    bool read;

    // The last setup that was gone through; null when there was none.
    public SetupResult? Setup { get; private set; }

    // The milestone that was chosen on the page; null when none was.
    public MilestoneChoice? Chosen { get; private set; }

    // The model and the effort that were chosen on the page; null when none was.
    public ModelChoice? ChosenModel { get; private set; }

    public string? ChosenEffort { get; private set; }

    // The share of the weekly limit that was chosen on the page; null when none was.
    public int? ChosenShare { get; private set; }

    // Whether anything was chosen on the page.
    public bool Chose => Chosen is not null || ChosenModel is not null || ChosenEffort is not null || ChosenShare is not null;

    // How the runs that were on the page ended, in their order; null is one that never got to its end.
    public List<RunResult?> Runs { get; } = [];

    // Opens with the menu. A project that is not set up is set up first, and a user who has no milestone to work
    // on, no model or no effort chooses them; leaving any of these leaves the page. A run that is opened from the
    // menu comes back to it when it is over, with how it went said above the menu.
    public async Task HomeAsync(CancellationToken cancellationToken)
    {
        if (bench.Settings.Load()?.Settings is null)
        {
            var first = await SetupAsync(leavesPage: true, cancellationToken);
            if (first.Outcome is not (SetupOutcome.Written or SetupOutcome.NothingToChange) || bench.Settings.Load()?.Settings is null)
                return;
        }

        if (!await ReadAsync(cancellationToken))
            return;
        if (!standing.Ready && !await ChooseMilestoneAsync(leavesPage: true, cancellationToken))
            return;
        if (bench.Personal.LoadModel() is null && !ChooseModel(leavesPage: true))
            return;
        if (bench.Personal.LoadEffort() is null && !ChooseEffort(leavesPage: true))
            return;

        // The tasks are counted for the menu over what is set and chosen at the moment, and anew after everything
        // that may have changed their number.
        var tasks = new TaskCount(async () => bench.Settings.Load()?.Settings is { } settings && standing.Choice is { } choice
            ? await bench.CountTasksAsync(settings, choice.Title, cancellationToken)
            : null);
        string? note = null;
        while (true)
        {
            Clear("", note);
            switch (await HomeScreen.AskAsync(page, Assistant(), standing.Name, tasks))
            {
                case HomeAction.Run:
                    note = bench.OpenRun(bench.Settings.Load()!.Settings!, standing.Choice!.Title, bench.Personal.LoadModel()!, bench.Personal.LoadEffort()!) is { } run
                        ? await RunAsync(run, cancellationToken)
                        : "The `origin` remote is not a GitHub repository, so there are no tasks to take.";
                    tasks.Forget();
                    break;
                case HomeAction.Assistant:
                    ChooseAssistant();
                    note = null;
                    break;
                case HomeAction.Milestone:
                    note = await ChooseMilestoneAsync(leavesPage: false, cancellationToken) ? null : "The milestone is left as it was.";
                    tasks.Forget();
                    break;
                case HomeAction.Settings:
                    note = Ending(await SetupAsync(leavesPage: false, cancellationToken), bench.Demo).Text;
                    tasks.Forget();
                    break;
                default:
                    return;
            }
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
        if (!read && !await ReadAsync(cancellationToken))
            return false;

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

    // Asks for the model and remembers the answer; false when the user went back instead.
    public bool ChooseModel(bool leavesPage)
    {
        Clear("Choosing the model", null);
        page.EscapeLeaves = leavesPage;
        page.Keys = leavesPage ? "esc exit" : "esc back";
        if (ModelScreen.Ask(page, bench.Personal.LoadModel()) is not { } choice)
            return false;

        bench.Personal.SaveModel(choice);
        ChosenModel = choice;
        return true;
    }

    // Asks for the effort and remembers the answer; false when the user went back instead.
    public bool ChooseEffort(bool leavesPage)
    {
        Clear("Choosing the effort", null);
        page.EscapeLeaves = leavesPage;
        page.Keys = leavesPage ? "esc exit" : "esc back";
        if (EffortScreen.Ask(page, bench.Personal.LoadEffort()) is not { } effort)
            return false;

        bench.Personal.SaveEffort(effort);
        ChosenEffort = effort;
        return true;
    }

    // Asks how much of the weekly limit a run may use and remembers the answer; false when the user went back
    // instead.
    public bool ChooseLimit()
    {
        Clear("Choosing the weekly limit", null);
        page.Keys = "esc back";
        if (LimitScreen.Ask(page, Share()) is not { } share)
            return false;

        bench.Personal.SaveShare(share);
        ChosenShare = share;
        return true;
    }

    // Runs the queue on a tape under the bar of the page, until the run is over, and says how it went in one
    // line. How it ended is remembered whatever way the page was left.
    public async Task<string> RunAsync(IQueueRun run, CancellationToken cancellationToken)
    {
        Clear("Running the queue", null);
        var screen = new RunScreen(page, () => bench.Clock.Now);
        try
        {
            await screen.RunAsync(run, cancellationToken);
            return screen.Summary;
        }
        finally
        {
            Runs.Add(screen.Result);
        }
    }

    // What `Assistant` of the menu opens, until Escape goes back to the menu: the pointer stays on what was opened,
    // and a choice that was left is said above the list.
    void ChooseAssistant()
    {
        var at = AssistantAction.Model;
        string? note = null;
        while (true)
        {
            Clear("Assistant", note);
            if (AssistantScreen.Ask(page, ModelScreen.Describe(bench.Personal.LoadModel()), bench.Personal.LoadEffort() ?? "not chosen", LimitScreen.Describe(Share()), (int)at) is not { } action)
                return;

            at = action;
            note = action switch
            {
                AssistantAction.Model => ChooseModel(leavesPage: false) ? null : "The model is left as it was.",
                AssistantAction.Effort => ChooseEffort(leavesPage: false) ? null : "The effort is left as it was.",
                _ => ChooseLimit() ? null : "The weekly limit is left as it was.",
            };
        }
    }

    // The share of the weekly limit that is in force: the proposed one until the user chooses.
    int Share() => bench.Personal.LoadShare() ?? UsageShare.Proposed;

    // The model and the effort in a few words, as the menu shows them.
    string Assistant() =>
        $"{(bench.Personal.LoadModel() is { Name: { } name } ? name : "own model")} · {bench.Personal.LoadEffort()}";

    // How a setup ended, in one line, and whether that is a failure.
    public static (bool Failed, string Text) Ending(SetupResult result, bool demo) => result.Outcome switch
    {
        SetupOutcome.Written when demo => (false, $"Demo: nothing was made, linked or written; {SettingsFile.DisplayPath} is left as it was."),
        SetupOutcome.Written when result.SettingsWritten => (false, $"The settings are written to {SettingsFile.DisplayPath}. Commit the file."),
        SetupOutcome.Written => (false, "GitHub is set up as the settings say."),
        SetupOutcome.NothingToChange => (false, "Nothing to change: the settings already say this."),
        SetupOutcome.Declined => (true, "The setup was declined: nothing was changed."),
        SetupOutcome.Failed => (true, $"{result.Failure} The settings were not written."),
        _ => (true, "The setup was left: nothing was changed."),
    };

    // Reads the open milestones, which the page cannot go on without: there is nowhere to go back to from here,
    // so Escape leaves the page. False when the user left instead of waiting.
    async Task<bool> ReadAsync(CancellationToken cancellationToken)
    {
        Clear("", null);
        page.EscapeLeaves = true;
        page.Keys = "esc exit";
        var (done, milestones) = await page.WaitAsync("Reading the milestones", bench.ReadMilestonesAsync, cancellationToken);
        if (!done)
            return false;

        open = milestones;
        standing = MilestoneStanding.Of(bench.Personal.LoadMilestone(), open);
        read = true;
        return true;
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
            body.Add(Picker.Answer("Owner's label", settings.Queue.Labels.Owner));
            body.Add(Picker.Answer("Interrupt label", settings.Queue.Labels.Interrupted));
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

        if (bench.Personal.LoadModel() is { } model)
            body.Add(Picker.Answer("Model", ModelScreen.Describe(model)));
        if (bench.Personal.LoadEffort() is { } effort)
            body.AddRange([Picker.Answer("Effort", effort), Picker.Answer("Weekly limit", LimitScreen.Describe(Share()))]);

        if (note is not null)
            body.AddRange([Line.Empty, Line.Of(note, Tone.Muted)]);
        page.Body = body;
    }
}
