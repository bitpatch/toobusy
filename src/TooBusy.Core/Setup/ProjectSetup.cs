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
// SettingsWritten tells a setup that wrote the file from one that only linked the board.
public sealed record SetupResult(SetupOutcome Outcome, IReadOnlyList<SetupAnswer> Answers, string? Failure = null, bool SettingsWritten = false);

// The steps of `toobusy init`: it asks what the settings need, shows what would change, and on a yes makes it so.
// With settings that exist every question proposes the current value, so that accepting them all changes nothing.
// From every step the user can go back to the one before; an answer that was given is proposed again.
public sealed class ProjectSetup(ISetupDialog dialog, ISetupEnvironment environment, ISetupTracker tracker, ISetupBoards boards, ISettingsStore store)
{
    // The steps that are asked; the confirmation comes when they are all done.
    static readonly string[] Steps = ["Project", "Blocking labels", "Labels to take"];

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
            dialog.Wait("Reading your projects from GitHub…");
            known = await boards.ReadAsync(machine.OriginRepository, cancellationToken);

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
                "Project",
                "The GitHub project of the tasks: their statuses are kept on its board.",
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

        var all = await AllLabelsAsync(1, cancellationToken);
        Show(1);
        var picked = dialog.ChooseMany("Blocking labels", "Tasks with any of these labels are never taken. Choose none to block nothing.", all, Indexes(all, proposed));
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

        var rest = (await AllLabelsAsync(2, cancellationToken)).Except(blocking!, StringComparer.OrdinalIgnoreCase).ToList();
        Show(2);
        var picked = dialog.ChooseMany("Labels to take", "Only tasks with one of these labels are taken. Choose none to take any task.", rest, Indexes(rest, proposed));
        if (picked is not null)
            take = [.. picked.Select(index => rest[index])];
        return picked is not null;
    }

    // Labels of the settings stay among the choices even when the repository lost them:
    // accepting what is proposed must not change the file.
    async Task<List<string>> AllLabelsAsync(int step, CancellationToken cancellationToken)
    {
        if (labels is null)
        {
            Show(step);
            dialog.Wait("Reading the labels…");
            labels = await tracker.ReadLabelsAsync(repository, cancellationToken);
        }

        return [.. labels.Concat(current?.Queue.Labels.Blocking ?? []).Concat(current?.Queue.Labels.Take ?? []).Distinct(StringComparer.OrdinalIgnoreCase)];
    }

    // Shows what a yes would do and, on a yes, does it. Null is going back.
    async Task<SetupOutcome?> ConcludeAsync(CancellationToken cancellationToken)
    {
        // The address of a board that is not made yet stands for it in the file that is shown.
        var made = board as BoardAnswer.Created;
        var unmade = made is null ? null : $"https://github.com/{(made.Owner.Organisation ? "orgs" : "users")}/{made.Owner.Login}/projects/0";
        var address = board is BoardAnswer.Existing existing ? existing.Address : unmade;
        var linking = verified && address is not null && Known(address) is not { Linked: true };

        var preview = store.Preview(Settings(address));
        var writing = preview.Before != preview.After;
        if (!writing && !linking)
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

        Show(Steps.Length, coming);
        if (dialog.Confirm(writing ? "Write the settings?" : "Link the project?") is not { } yes)
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
        ];
        return [.. settings.Where(setting => setting.Before != setting.After).Select(setting => $"{setting.Name}: {setting.Before} → {setting.After}")];
    }

    // A board as people know it: by its title when it is known, with its address.
    string Board(string? address) =>
        address is null ? "none" : Known(address) is { Title.Length: > 0 } named ? $"{named.Title}  {named.Address}" : address;

    ProjectSettings Settings(string? address) => new(
        new TrackerSettings(SettingsKeys.GitHubTracker, address),
        new QueueSettings(new LabelSettings(blocking!, take!)),
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
        {
            answers.Add(new SetupAnswer("Labels to take", take!.Count == 0 ? "any task" : string.Join(", ", take)));
            answers.Add(new SetupAnswer("Assistant", "Claude Code"));
        }

        return answers;
    }

    SetupBoard? Known(string address) => known.Boards.FirstOrDefault(board => board.Address.Equals(address, StringComparison.OrdinalIgnoreCase));

    static List<int> Indexes(List<string> options, IReadOnlyList<string> chosen) =>
        [.. Enumerable.Range(0, options.Count).Where(index => chosen.Contains(options[index], StringComparer.OrdinalIgnoreCase))];

    static List<string> Names(string text) =>
        [.. text.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries).Distinct(StringComparer.OrdinalIgnoreCase)];
}
