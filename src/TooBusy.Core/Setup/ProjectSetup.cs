using TooBusy.Core.Queue;
using TooBusy.Core.Settings;

namespace TooBusy.Core.Setup;

public enum SetupOutcome
{
    Written,
    NothingToChange,
    Declined,

    // The user went back from the first step: the setup is left as if it never started.
    Left,

    // The tracker refused what a yes asked of it: the settings are not written.
    Failed,
}

// Answers are those the setup ended with, as the screen showed them; Failure says what went wrong when it failed.
// SettingsWritten tells a setup that wrote the file from one that only changed something on GitHub.
public sealed record SetupResult(SetupOutcome Outcome, IReadOnlyList<SetupAnswer> Answers, string? Failure = null, bool SettingsWritten = false);

// The steps of `toobusy init`: it asks what the settings need, shows what would change, and on a yes makes it so.
// With settings that exist every question proposes the current value, so that accepting them all changes nothing.
// From every step the user can go back to the one before; an answer that was given is proposed again.
public sealed class ProjectSetup(ISetupDialog dialog, ISetupEnvironment environment, ISetupTracker tracker, ISetupBoards boards, ISettingsStore store)
{
    // The steps that are asked; the confirmation comes when they are all done.
    const string ProjectLabel = "Project";
    const string ProjectHint = "The GitHub project of the tasks: their statuses are kept on its board.";
    const string BlockingLabel = "Blocking labels";
    const string BlockingHint = "Tasks with any of these labels are never taken. Choose none to block nothing.";
    const string TakeLabel = "Labels to take";
    const string TakeHint = "Only tasks with one of these labels are taken. Choose none to take any task.";
    const string OwnerLabel = "Owner's label";
    const string InterruptLabel = "Interrupt label";

    // The last choice of a label: one that the repository does not have yet.
    const string NewLabel = "New label…";

    // What a first setup proposes to call the two labels.
    const string ProposedOwner = "needs-owner";
    const string ProposedInterrupted = "interrupted";

    static readonly string[] Steps = [ProjectLabel, BlockingLabel, TakeLabel, OwnerLabel, InterruptLabel];

    readonly List<SetupNote> notes = [];

    ProjectSettings? current;
    string repository = "";
    bool verified;
    SetupBoards known = SetupBoards.None;
    IReadOnlyList<string>? labels;

    // The answers so far; null is a step that was not answered yet.
    BoardAnswer? board;
    List<string>? blocking;
    List<string>? take;
    string? owner;
    string? interrupted;

    string? failure;
    bool written;

    public async Task<SetupResult> RunAsync(CancellationToken cancellationToken = default)
    {
        var machine = await environment.InspectAsync(cancellationToken);
        foreach (var problem in machine.Problems)
        {
            notes.Add(new SetupNote(SetupTone.Failure, problem.Text));
            notes.Add(new SetupNote(SetupTone.Muted, $"fix: {problem.Fix}"));
        }

        // The tasks are those of the repository the project is cloned from. Without it, as without the tracker,
        // there is nothing to read the labels and the milestones from.
        repository = machine.OriginRepository ?? "";
        verified = machine.TrackerReachable && repository.Length > 0;
        if (!machine.TrackerReachable)
            notes.Add(new SetupNote(SetupTone.Warning, "GitHub cannot be read from here, so nothing is verified: your answers are taken as typed."));
        else if (!verified)
            notes.Add(new SetupNote(SetupTone.Warning, "The `origin` remote is not a GitHub repository, so nothing is verified: your answers are taken as typed."));

        var loaded = store.Load();
        if (loaded is { Settings: null })
        {
            notes.Add(new SetupNote(SetupTone.Warning, "The settings that exist do not validate:"));
            notes.AddRange(loaded.Errors.Select(error => new SetupNote(SetupTone.Failure, error.Describe(store.DisplayPath))));
        }

        current = loaded?.Settings;
        if (machine.TrackerReachable)
        {
            Show(0);
            if (await dialog.WaitAsync(ProjectLabel, ProjectHint, "Reading your projects from GitHub", stopping => boards.ReadAsync(machine.OriginRepository, stopping), cancellationToken) is not { } read)
                return new SetupResult(SetupOutcome.Left, []);
            known = read;

            // A new board is proposed for the owner of the repository when the user can make one there.
            known = known with { Owners = [.. known.Owners.OrderBy(owner => repository.StartsWith(owner.Login + "/", StringComparison.OrdinalIgnoreCase) ? 0 : 1)] };
        }

        var step = 0;
        while (step >= 0)
        {
            bool answered;
            switch (step)
            {
                case 0:
                    answered = await AskBoardAsync(cancellationToken);
                    break;
                case 1:
                    answered = await AskBlockingAsync(cancellationToken);
                    break;
                case 2:
                    answered = await AskTakeAsync(cancellationToken);
                    break;
                case 3:
                    answered = await AskOwnerAsync(cancellationToken);
                    break;
                case 4:
                    answered = await AskInterruptedAsync(cancellationToken);
                    break;
                default:
                    if (await ConcludeAsync(cancellationToken) is { } outcome)
                        return new SetupResult(outcome, Answers(Steps.Length), failure, written);
                    answered = false;
                    break;
            }

            step += answered ? 1 : -1;
        }

        return new SetupResult(SetupOutcome.Left, []);
    }

    async Task<bool> AskBoardAsync(CancellationToken cancellationToken)
    {
        SetupNote[] refused = [];
        while (true)
        {
            // What the project has now: the board that was chosen a moment ago, the one of the settings,
            // or one that is linked to the repository already.
            var address = board switch
            {
                BoardAnswer.Existing existing => existing.Address,
                null => current?.Tracker.Board ?? known.Boards.FirstOrDefault(known => known.Linked)?.Address,
                _ => null,
            };
            var now = address is null ? null : Known(address) ?? new SetupBoard(address, "", false);

            Show(0, refused);
            var answer = dialog.AskBoard(new BoardQuestion(
                ProjectLabel,
                ProjectHint,
                now,
                known.Boards,
                known.Owners,
                text => SettingsValidator.IsBoard(BoardSuggestions.AddressOf(text)) ? null : "That is not a project. Use https://github.com/orgs/<org>/projects/<number>.",
                title => string.IsNullOrWhiteSpace(title) ? "A project needs a title." : null));
            if (answer is null)
                return false;

            if (answer is BoardAnswer.Existing chosen)
            {
                answer = chosen = new BoardAnswer.Existing(BoardSuggestions.AddressOf(chosen.Address));
                if (verified && await tracker.RefuseBoardAsync(chosen.Address, cancellationToken) is { } reason)
                {
                    refused = [new SetupNote(SetupTone.Failure, reason)];
                    continue;
                }
            }

            board = answer;
            return true;
        }
    }

    async Task<bool> AskBlockingAsync(CancellationToken cancellationToken)
    {
        var proposed = blocking ?? current?.Queue.Labels.Blocking ?? [];
        if (!verified)
        {
            Show(1);
            var typed = dialog.Ask("Blocking labels", "Names separated by commas. A task with any of them is never taken.", string.Join(", ", proposed), _ => null);
            if (typed is not null)
                blocking = Names(typed);
            return typed is not null;
        }

        if (await AllLabelsAsync(1, BlockingLabel, BlockingHint, cancellationToken) is not { } all)
            return false;
        Show(1);
        var picked = dialog.ChooseMany(BlockingLabel, BlockingHint, all, Indexes(all, proposed));
        if (picked is not null)
            blocking = [.. picked.Select(index => all[index])];
        return picked is not null;
    }

    async Task<bool> AskTakeAsync(CancellationToken cancellationToken)
    {
        var proposed = take ?? current?.Queue.Labels.Take ?? [];
        if (!verified)
        {
            Show(2);
            var typed = dialog.Ask("Labels to take", "Names separated by commas. Leave empty to take any task.", string.Join(", ", proposed), text =>
                Names(text).Intersect(blocking!, StringComparer.OrdinalIgnoreCase).FirstOrDefault() is { } shared ? $"{shared} is a blocking label." : null);
            if (typed is not null)
                take = Names(typed);
            return typed is not null;
        }

        if (await AllLabelsAsync(2, TakeLabel, TakeHint, cancellationToken) is not { } all)
            return false;
        var rest = all.Except(blocking!, StringComparer.OrdinalIgnoreCase).ToList();
        Show(2);
        var picked = dialog.ChooseMany(TakeLabel, TakeHint, rest, Indexes(rest, proposed));
        if (picked is not null)
            take = [.. picked.Select(index => rest[index])];
        return picked is not null;
    }

    Task<bool> AskOwnerAsync(CancellationToken cancellationToken) => AskLabelAsync(
        3,
        OwnerLabel,
        "A task that cannot go on without you gets this label, and is not taken while it has it.",
        owner ?? current?.Queue.Labels.Owner ?? ProposedOwner,
        name => Has(take!, name) ? $"{name} is a label to take." : null,
        answer => owner = answer,
        cancellationToken);

    Task<bool> AskInterruptedAsync(CancellationToken cancellationToken) => AskLabelAsync(
        4,
        InterruptLabel,
        "A task that a run had to stop gets this label, and the next run takes it first.",
        interrupted ?? current?.Queue.Labels.Interrupted ?? ProposedInterrupted,
        name => Has(take!, name) ? $"{name} is a label to take."
            : Has(blocking!, name) ? $"{name} is a blocking label."
            : name.Equals(owner, StringComparison.OrdinalIgnoreCase) ? $"{name} is the label of the owner."
            : null,
        answer => interrupted = answer,
        cancellationToken);

    // One label that a run puts on a task: one of the labels of the repository that nothing speaks against, or a new
    // one, which is named here and made when the setup is confirmed. The proposed one is the label that was
    // answered, the one of the settings, or the name a first setup proposes.
    async Task<bool> AskLabelAsync(int step, string label, string hint, string proposed, Func<string, string?> refuse, Action<string> answer, CancellationToken cancellationToken)
    {
        string? Refuse(string text) => string.IsNullOrWhiteSpace(text) ? "A label needs a name." : refuse(text.Trim());

        if (!verified)
        {
            Show(step);
            var typed = dialog.Ask(label, hint, proposed, Refuse);
            if (typed is not null)
                answer(typed.Trim());
            return typed is not null;
        }

        if (await AllLabelsAsync(step, label, hint, cancellationToken) is not { } all)
            return false;
        var offered = all.Where(name => refuse(name) is null).ToList();
        while (true)
        {
            var at = offered.FindIndex(name => name.Equals(proposed, StringComparison.OrdinalIgnoreCase));
            Show(step);
            if (dialog.Choose(label, hint, [.. offered.Select(name => new SetupOption(name, "")), new SetupOption(NewLabel, "")], at < 0 ? offered.Count : at) is not { } picked)
                return false;
            if (picked < offered.Count)
            {
                answer(offered[picked]);
                return true;
            }

            // Going back from the name comes back to the list.
            Show(step);
            if (dialog.Ask(label, $"The name of the new label. It is made in {repository} when the setup is confirmed.", at < 0 ? proposed : "", Refuse) is { } named)
            {
                answer(named.Trim());
                return true;
            }
        }
    }

    // Labels of the settings stay among the choices even when the repository lost them:
    // accepting what is proposed must not change the file. Null when the user went back instead of waiting for
    // the labels to be read.
    async Task<List<string>?> AllLabelsAsync(int step, string label, string hint, CancellationToken cancellationToken)
    {
        if (labels is null)
        {
            Show(step);
            labels = await dialog.WaitAsync(label, hint, "Reading the labels", stopping => tracker.ReadLabelsAsync(repository, stopping), cancellationToken);
            if (labels is null)
                return null;
        }

        IEnumerable<string> ofSettings = current?.Queue.Labels is { } known ? [.. known.Blocking, .. known.Take, known.Owner, known.Interrupted] : [];
        return [.. labels.Concat(ofSettings).Distinct(StringComparer.OrdinalIgnoreCase)];
    }

    // Shows what a yes would do and, on a yes, does it. Null is going back.
    async Task<SetupOutcome?> ConcludeAsync(CancellationToken cancellationToken)
    {
        // The address of a board that is not made yet stands for it in the file that is shown.
        var made = board as BoardAnswer.Created;
        var unmade = made is null ? null : $"https://github.com/{(made.Owner.Organisation ? "orgs" : "users")}/{made.Owner.Login}/projects/0";
        var address = board is BoardAnswer.Existing existing ? existing.Address : unmade;
        var linking = verified && address is not null && Known(address) is not { Linked: true };

        // A label of the two that the repository does not have is made: a run could not put it on a task otherwise.
        List<string> missing = verified
            ? [.. new[] { owner!, interrupted! }.Where(name => !Has(labels!, name)).Distinct(StringComparer.OrdinalIgnoreCase)]
            : [];

        var preview = store.Preview(Settings(address));
        var writing = preview.Before != preview.After;
        if (!writing && !linking && missing.Count == 0)
            return SetupOutcome.NothingToChange;

        // What a yes will do, in the words of the questions: a first setup writes what was answered, and a setup
        // that exists says what changes in it, each setting as it was and as it will be.
        var coming = new List<SetupNote>();
        var changes = current is null ? [] : Changes(current, address, made);
        if (writing && changes.Count > 0)
        {
            coming.Add(new SetupNote(SetupTone.Plain, $"This will change in {store.DisplayPath}:"));
            coming.AddRange(changes.Select(change => new SetupNote(SetupTone.Change, change)));
        }
        else if (writing)
        {
            coming.Add(new SetupNote(SetupTone.Plain, $"The answers above will be written to {store.DisplayPath}."));
        }

        if (made is not null)
            coming.Add(new SetupNote(SetupTone.Plain, $"The project “{made.Title}” will be made for {made.Owner.Login}."));
        if (linking)
            coming.Add(new SetupNote(SetupTone.Plain, $"The project will be linked to {repository}."));
        coming.AddRange(missing.Select(name => new SetupNote(SetupTone.Plain, $"The label “{name}” will be made in {repository}.")));

        Show(Steps.Length, coming);
        var question = writing ? "Write the settings?"
            : missing.Count == 0 ? "Link the project?"
            : linking ? "Link the project and make the labels?"
            : "Make the labels?";
        if (dialog.Confirm(question) is not { } yes)
            return null;
        if (!yes)
            return SetupOutcome.Declined;

        // The settings are written last, so that they never name a board that could not be made.
        try
        {
            if (made is not null)
                address = await tracker.CreateBoardAsync(made.Owner, made.Title, cancellationToken);
            if (linking)
                await tracker.LinkBoardAsync(address!, repository, cancellationToken);
            foreach (var name in missing)
                await tracker.CreateLabelAsync(repository, name, cancellationToken);
        }
        catch (TrackerException refused)
        {
            failure = made is not null && address != unmade ? $"The project was made at {address}, but then: {refused.Message}" : refused.Message;
            return SetupOutcome.Failed;
        }

        if (writing)
            store.Save(Settings(address));
        written = writing;
        return SetupOutcome.Written;
    }

    // The settings that differ from those that exist, each as `name: before → after`.
    List<string> Changes(ProjectSettings before, string? address, BoardAnswer.Created? made)
    {
        static string Labels(IReadOnlyList<string> labels, string none) => labels.Count == 0 ? none : string.Join(", ", labels);

        (string Name, string Before, string After)[] settings =
        [
            ("Project", Board(before.Tracker.Board), made is null ? Board(address) : $"new: {made.Title} ({made.Owner.Login})"),
            ("Blocking labels", Labels(before.Queue.Labels.Blocking, "none"), Labels(blocking!, "none")),
            ("Labels to take", Labels(before.Queue.Labels.Take, "any task"), Labels(take!, "any task")),
            (OwnerLabel, before.Queue.Labels.Owner, owner!),
            (InterruptLabel, before.Queue.Labels.Interrupted, interrupted!),
        ];
        return [.. settings.Where(setting => setting.Before != setting.After).Select(setting => $"{setting.Name}: {setting.Before} → {setting.After}")];
    }

    // A board as people know it: by its title when it is known, with its address.
    string Board(string? address) =>
        address is null ? "none" : Known(address) is { Title.Length: > 0 } named ? $"{named.Title}  {named.Address}" : address;

    ProjectSettings Settings(string? address) => new(
        new TrackerSettings(SettingsKeys.GitHubTracker, address),
        new QueueSettings(new LabelSettings(blocking!, take!, owner!, interrupted!)),
        new AssistantSettings(SettingsKeys.ClaudeCodeAssistant));

    void Show(int step, IReadOnlyList<SetupNote>? more = null) =>
        dialog.Show(new SetupProgress([.. notes, .. more ?? []], Answers(step), Steps, step));

    // The answers of the steps before the given one, with the two that are never asked around them.
    List<SetupAnswer> Answers(int step)
    {
        var answers = new List<SetupAnswer> { new("Tracker", "GitHub") };
        if (step > 0)
        {
            answers.Add(new SetupAnswer("Project", board switch
            {
                BoardAnswer.Existing existing => Board(existing.Address),
                BoardAnswer.Created made => $"new: {made.Title} ({made.Owner.Login})",
                _ => "none",
            }));
        }

        if (step > 1)
            answers.Add(new SetupAnswer("Blocking labels", blocking!.Count == 0 ? "none" : string.Join(", ", blocking)));
        if (step > 2)
            answers.Add(new SetupAnswer("Labels to take", take!.Count == 0 ? "any task" : string.Join(", ", take)));
        if (step > 3)
            answers.Add(new SetupAnswer(OwnerLabel, owner!));
        if (step > 4)
        {
            answers.Add(new SetupAnswer(InterruptLabel, interrupted!));
            answers.Add(new SetupAnswer("Assistant", "Claude Code"));
        }

        return answers;
    }

    SetupBoard? Known(string address) => known.Boards.FirstOrDefault(board => board.Address.Equals(address, StringComparison.OrdinalIgnoreCase));

    static bool Has(IReadOnlyList<string> labels, string label) => labels.Contains(label, StringComparer.OrdinalIgnoreCase);

    static List<int> Indexes(List<string> options, IReadOnlyList<string> chosen) =>
        [.. Enumerable.Range(0, options.Count).Where(index => chosen.Contains(options[index], StringComparer.OrdinalIgnoreCase))];

    static List<string> Names(string text) =>
        [.. text.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries).Distinct(StringComparer.OrdinalIgnoreCase)];
}
