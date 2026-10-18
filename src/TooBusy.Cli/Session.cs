using TooBusy.Cli.Terminal;
using TooBusy.Core.Assistant;
using TooBusy.Core.Doctor;
using TooBusy.Core.Queue;
using TooBusy.Core.Run;
using TooBusy.Core.Setup;
using TooBusy.Infrastructure.Settings;

namespace TooBusy.Cli;

// What happens on the page of toobusy from the moment it is opened: the setup, the choices of the milestone, the
// model and the effort, the menu and the run, each a screen that takes the page for a while. It remembers what was
// done, for the report that is left in the terminal when the page is closed.
public sealed class Session(Page page, Workbench bench)
{
    IReadOnlyList<Milestone>? open;
    MilestoneStanding standing = MilestoneStanding.Of(null, null);
    bool read;

    // The labels whose tasks the runs of the menu leave out: chosen on the page, and kept nowhere.
    IReadOnlyList<string> skipped = [];

    // The last setup that was gone through; null when there was none.
    public SetupResult? Setup { get; private set; }

    // The milestone that was chosen on the page; null when none was.
    public MilestoneChoice? Chosen { get; private set; }

    // The model and the effort that were chosen on the page; null when none was.
    public ModelChoice? ChosenModel { get; private set; }

    public string? ChosenEffort { get; private set; }

    // Whether anything was chosen on the page.
    public bool Chose => Chosen is not null || ChosenModel is not null || ChosenEffort is not null;

    // How the runs that were on the page ended, in their order; null is one that never got to its end.
    public List<RunResult?> Runs { get; } = [];

    // Opens with the menu. A project that is not set up is set up first, and a user who has no milestone to work
    // on, no model or no effort chooses them; leaving any of these leaves the page. A run that is opened from the
    // menu comes back to it when it is over, with how it went said above the menu. No run is opened while one goes on in the working copy. Before it is opened the checks
    // of `doctor` are made, as `toobusy run` makes them: while one fails no run is opened, and the failures stand
    // above the menu with their fixes. A demo has it all and checks nothing. The labels to skip that are chosen in
    // the menu hold for every run it opens, until the page is closed.
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
            ? await bench.OutlookAsync(settings, choice.Title, skipped, cancellationToken)
            : null);
        string? note = null;
        IReadOnlyList<CheckResult> failed = [];
        while (true)
        {
            Clear("", note, failed);
            var action = await HomeScreen.AskAsync(page, Assistant(), standing.Name, skipped, tasks);
            failed = [];
            if (action == HomeAction.Run && bench.Going() is { } going)
            {
                note = $"A run is going on here already: {going.Sign.Told}";
                continue;
            }

            if (action == HomeAction.Run && !bench.Demo)
            {
                (action, failed) = await CheckAsync(tasks, cancellationToken);
                if (failed.Count > 0)
                {
                    note = "The run did not start. `toobusy doctor` checks all of it again.";
                    continue;
                }
            }

            switch (action)
            {
                case HomeAction.Run:
                    note = bench.OpenRun(bench.Settings.Load()!.Settings!, standing.Choice!.Title, bench.Personal.LoadModel()!, bench.Personal.LoadEffort()!, skip: skipped) is { } run
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
                    note = await ChangeSettingsAsync(cancellationToken);
                    tasks.Forget();
                    break;
                case HomeAction.SkipLabels:
                    note = await ChooseSkippedAsync(cancellationToken) ? null : "The labels to skip are left as they were.";
                    tasks.Forget();
                    break;
                default:
                    return;
            }
        }
    }

    // Makes the checks of `doctor` under the menu, which shows the one that is running at `Run` and stays what
    // it is: gives `Run` with the checks that failed once they are over, or what was chosen meanwhile, which stops
    // them.
    async Task<(HomeAction Action, IReadOnlyList<CheckResult> Failed)> CheckAsync(TaskCount tasks, CancellationToken cancellationToken)
    {
        var checks = new RunChecks();
        using var stopping = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var checking = MakeAsync();
        try
        {
            Clear("", null);
            var action = await HomeScreen.AskAsync(page, Assistant(), standing.Name, skipped, tasks, checks);
            return (action, action == HomeAction.Run ? checks.Failed : []);
        }
        finally
        {
            // Checks that were not waited for are told to stop and left to it.
            if (!checking.IsCompleted)
                await stopping.CancelAsync();
        }

        async Task MakeAsync()
        {
            try
            {
                await bench.Checkup.RunAsync(checks, stopping.Token);
            }
            catch (Exception stopped) when (stopped is OperationCanceledException or ObjectDisposedException)
            {
            }
            finally
            {
                checks.End();
            }
        }
    }

    // `leavesPage` tells that there is nowhere to go back to from the first question. The proposals are what the
    // options of `init` answer.
    public async Task<SetupResult> SetupAsync(bool leavesPage, CancellationToken cancellationToken, SetupProposals? proposals = null)
    {
        Clear("Setting up this project", null);
        page.Body = [];
        return Setup = await new ProjectSetup(new SetupScreen(page, leavesPage), bench.Environment, bench.Tracker, bench.Boards, bench.Settings, proposals).RunAsync(cancellationToken);
    }

    // What `Settings` of the menu opens: the settings one under another with their values, until Escape goes back to
    // the menu. Each opens its questions, and a change is saved when the user says so; what became of it is said
    // above the list, and the pointer stays on the setting that was opened. What was changed is remembered for the
    // report as the change is made. Settings that do not validate have nothing to show there: the steps of `init`
    // set them, as for a project that is set up anew, and the line that says how that ended is given for the menu.
    async Task<string?> ChangeSettingsAsync(CancellationToken cancellationToken)
    {
        const string status = "Changing the settings";
        if (bench.Settings.Load()?.Settings is null)
            return Ending(await SetupAsync(leavesPage: false, cancellationToken), bench.Demo).Text;

        Clear(status, null);
        var setup = new ProjectSetup(new SetupScreen(page, leavesPage: false), bench.Environment, bench.Tracker, bench.Boards, bench.Settings);
        if (!await setup.OpenAsync(cancellationToken))
            return Ending(await SetupAsync(leavesPage: false, cancellationToken), bench.Demo).Text;

        var at = 0;
        string? note = null;
        while (true)
        {
            // The values are in the list: over it stand what the setup says of the machine and what became of the
            // last change.
            Clear(status, null);
            var told = setup.Notes.Select(SetupScreen.Told).ToList();
            page.Body = note is null ? told : [.. told, .. told.Count > 0 ? [Line.Empty] : (Line[])[], Line.Of(note, Tone.Muted)];
            if (SettingsScreen.Ask(page, setup.Values, at) is not { } setting)
                return null;

            at = (int)setting;
            var result = await setup.ChangeAsync(setting, cancellationToken);
            Setup = setup.Changed ?? Setup;
            note = result.Outcome switch
            {
                SetupOutcome.Written or SetupOutcome.Failed => Ending(result, bench.Demo).Text,
                SetupOutcome.Declined => "The change was not saved.",
                _ => null,
            };
        }
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

    // Asks for the labels whose tasks the runs of the menu leave out, after they are read, and remembers the answer
    // for as long as the page is open; false when the user went back instead, before the labels were read too.
    async Task<bool> ChooseSkippedAsync(CancellationToken cancellationToken)
    {
        Clear("Choosing the labels to skip", null);
        var (done, read) = await page.WaitAsync(
            "Reading the labels",
            stopping => bench.ReadLabelsAsync(bench.Settings.Load()!.Settings!, stopping),
            cancellationToken,
            Picker.Head(SkipScreen.Label, SkipScreen.Hint));
        if (!done || SkipScreen.Ask(page, read.Labels, read.Unread, skipped) is not { } chosen)
            return false;

        skipped = chosen;
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
            if (AssistantScreen.Ask(page, ModelScreen.Describe(bench.Personal.LoadModel()), bench.Personal.LoadEffort() ?? "not chosen", (int)at) is not { } action)
                return;

            at = action;
            note = action switch
            {
                AssistantAction.Model => ChooseModel(leavesPage: false) ? null : "The model is left as it was.",
                _ => ChooseEffort(leavesPage: false) ? null : "The effort is left as it was.",
            };
        }
    }

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
    // nothing of that screen around. The checks that failed before a run stand before the note, with their fixes.
    // A page whose status says nothing, as the menu, shows the version of the tool there.
    void Clear(string status, string? note, IReadOnlyList<CheckResult>? failed = null)
    {
        status = status.Length == 0 ? $"v{CliApp.Version}" : status;
        page.Status = bench.Demo ? $"{status} · demo" : status;
        page.Foot = Line.Empty;
        page.Keys = "";
        page.EscapeLeaves = false;

        var body = new List<Line>();
        if (bench.Settings.Load()?.Settings is { } settings)
        {
            body.Add(Picker.Answer("Project", settings.Tracker.Board ?? "none"));
            body.Add(Picker.Answer("Blocking labels", settings.Queue.Labels.Blocking.Count == 0 ? "none" : string.Join(", ", settings.Queue.Labels.Blocking)));
            body.Add(Picker.Answer("Labels to take", settings.Queue.Labels.Take.Count == 0 ? "any task" : string.Join(", ", settings.Queue.Labels.Take)));
            body.Add(Picker.Answer("Manual label", settings.Queue.Labels.Manual));
            body.Add(Picker.Answer("Interrupt label", settings.Queue.Labels.Interrupted));
            body.Add(Picker.Answer("Skill", settings.Skill ?? "none"));
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
            body.Add(Picker.Answer("Effort", effort));
        if (skipped.Count > 0)
            body.Add(Picker.Answer("Skipped labels", string.Join(", ", skipped)));

        if (note is not null)
            body.AddRange([Line.Empty, .. (failed ?? []).SelectMany(check => CheckList.Told(check, page.Width)), Line.Of(note, Tone.Muted)]);
        page.Body = body;
    }
}
