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
public sealed record SetupResult(SetupOutcome Outcome, IReadOnlyList<SetupAnswer> Answers, string? Failure = null, bool SettingsWritten = false)
{
    // What the setup said of the machine, of the settings that exist and of the options: a dialog has shown it,
    // and a setup that asked nothing has only this to tell it with.
    public IReadOnlyList<SetupNote> Notes { get; init; } = [];

    // What was done on GitHub, a line for each thing, in the order it was done; a setup that failed did these
    // before it was refused.
    public IReadOnlyList<string> Done { get; init; } = [];

    // The settings no question asks for, the commands around a task and the branch, that the options set, changed or
    // took away, each as it was and as it is now; they are not among the answers.
    public IReadOnlyList<SetupAnswer> Unasked { get; init; } = [];
}

// The steps of `toobusy init`: it asks what the settings need, shows what would change, and on a yes makes it so.
// With settings that exist every question proposes the current value, so that accepting them all changes nothing.
// From every step the user can go back to the one before; an answer that was given is proposed again.
// The proposals are what the options of the command answer: they are proposed before anything else is. Without a
// dialog nothing is asked: every question gets what it proposes, the last one a yes, and a proposal that does not
// pass the check of its question fails the setup before anything is changed.
public sealed class ProjectSetup(ISetupDialog? dialog, ISetupEnvironment environment, ISetupTracker tracker, ISetupBoards boards, ISettingsStore store, SetupProposals? proposals = null)
{
    // The steps that are asked; the confirmation comes when they are all done.
    const string ProjectLabel = "Project";
    const string ProjectHint = "The GitHub project of the tasks: their statuses are kept on its board.";
    const string StatusesLabel = "Statuses";
    const string BlockingLabel = "Blocking labels";
    const string BlockingHint = "Tasks with any of these labels are never taken. Choose none to block nothing.";
    const string TakeLabel = "Labels to take";
    const string TakeHint = "Only tasks with one of these labels are taken. Choose none to take any task.";
    const string OwnerLabel = "Owner's label";
    const string InterruptLabel = "Interrupt label";
    const string SkillLabel = "Skill";
    const string SkillHint = "A session starts with this skill of the project. Leave empty to start without one.";

    // The commands around a task and the branch, which no step asks for: the options name them.
    const string BeforeLabel = "Before a task";
    const string AfterLabel = "After a task";
    const string BranchLabel = "Branch";

    // The last choice of a label: one that the repository does not have yet.
    const string NewLabel = "New label…";

    // What a first setup proposes to call the two labels.
    internal const string ProposedOwner = "needs-owner";
    internal const string ProposedInterrupted = "interrupted";

    // What a first setup proposes to call the skill a session starts with.
    internal const string ProposedSkill = "task-auto";

    // The three questions about a board that is not named as GitHub names one, in the order a task goes through
    // the statuses. They belong to the step of the board: no board, no question.
    static readonly (string Label, string Hint)[] StatusQuestions =
    [
        ("Todo status", "A task is taken only from this status of the board."),
        ("In Progress status", "A run moves a task to this status before its session."),
        ("Done status", "A run moves a task to this status when it is done."),
    ];

    static readonly string[] Steps = [ProjectLabel, BlockingLabel, TakeLabel, OwnerLabel, InterruptLabel, SkillLabel];

    readonly ISetupDialog dialog = dialog ?? new Unasked();
    readonly bool unasked = dialog is null;
    readonly SetupProposals proposals = proposals ?? SetupProposals.None;
    readonly List<SetupNote> notes = [];
    readonly List<string> done = [];

    ProjectSettings? current;
    string repository = "";
    bool verified;
    SetupBoards known = SetupBoards.None;
    IReadOnlyList<string>? labels;

    // What the options say, once it is checked against what was read: the board when the boards are known, the
    // labels when the labels are.
    BoardAnswer? offeredBoard;
    List<string>? offeredBlocking;
    List<string>? offeredTake;

    // The answers so far; null is a step that was not answered yet.
    BoardAnswer? board;

    // What the board that was answered calls the statuses a run knows, the board they were named for, and whether
    // they were asked for: a board that was asked about is asked about again when the user comes back to it.
    BoardStatuses statuses = BoardStatuses.Default;
    string? named;
    bool asked;
    List<string>? blocking;
    List<string>? take;
    string? owner;
    string? interrupted;

    // The name of the skill; empty for a project that names none.
    string? skill;

    // The commands around a task and the branch: those of the settings, but for what the options say of them.
    TaskCommands commands = TaskCommands.None;
    string? branch;

    // What is said of a board that is chosen and lacks a status a run would know it by; empty when nothing is.
    IReadOnlyList<SetupNote> lacking = [];

    // What is said of a skill that the settings will name and nobody has written yet; empty when nothing is.
    IReadOnlyList<SetupNote> unwritten = [];

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

        // Settings that cannot be read propose nothing, and accepting what is proposed would put a first setup in
        // their place: nobody is there to see that, so a setup that asks nothing does not go on.
        if (unasked && loaded is { Settings: null })
            return new SetupResult(SetupOutcome.Failed, [], "The settings that exist do not validate.") { Notes = notes };

        current = loaded?.Settings;
        var step = 0;
        try
        {
            commands = new TaskCommands(
                Offered(proposals.Before, current?.Commands.Before, "--before"),
                Offered(proposals.After, current?.Commands.After, "--after"));
            branch = OfferedBranch(proposals.Branch, current?.Branch);
            if (machine.TrackerReachable)
            {
                Show(0);
                if (await dialog.WaitAsync(ProjectLabel, ProjectHint, "Reading your projects from GitHub", stopping => boards.ReadAsync(machine.OriginRepository, stopping), cancellationToken) is not { } read)
                    return new SetupResult(SetupOutcome.Left, []);
                known = read;

                // A new board is proposed for the owner of the repository when the user can make one there. Without
                // a repository to link it to no board is made, so there is nobody to make one for.
                known = known with { Owners = verified ? [.. known.Owners.OrderBy(owner => repository.StartsWith(owner.Login + "/", StringComparison.OrdinalIgnoreCase) ? 0 : 1)] : [] };
            }

            offeredBoard = OfferedBoard();
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
                    case 5:
                        answered = AskSkill();
                        break;
                    default:
                        if (await ConcludeAsync(cancellationToken) is { } outcome)
                            return new SetupResult(outcome, Answers(Steps.Length), failure, written) { Notes = [.. notes, .. lacking, .. unwritten], Done = done, Unasked = UnaskedChanges() };
                        answered = false;
                        break;
                }

                step += answered ? 1 : -1;
            }
        }
        catch (Refused refused)
        {
            // Nothing was changed: the questions come before everything that changes something.
            return new SetupResult(SetupOutcome.Failed, Answers(step), refused.Message) { Notes = notes };
        }

        return new SetupResult(SetupOutcome.Left, []);
    }

    // The board an option names, as an answer: an address is put the way the settings keep it, and a new board gets
    // the one it will belong to. Null when no option names a board, or when what it says cannot be.
    BoardAnswer? OfferedBoard()
    {
        switch (proposals.Board)
        {
            case BoardProposal.Existing existing:
                var address = BoardSuggestions.AddressOf(existing.Address);
                if (SettingsValidator.IsBoard(address))
                    return new BoardAnswer.Existing(address);
                Reject($"{ProjectLabel}: {existing.Address.Trim()} is not a project. Use https://github.com/orgs/<org>/projects/<number>.");
                return null;

            case BoardProposal.Created created:
                if (string.IsNullOrWhiteSpace(created.Title))
                    Reject($"{ProjectLabel}: A project needs a title.");
                else if (!verified)
                    Reject($"{ProjectLabel}: A new project cannot be made where nothing is verified.");
                else if (known.Owners.Count == 0)
                    Reject($"{ProjectLabel}: A new project cannot be made: GitHub named nobody it can be made for.");
                else if (created.Owner is null)
                    return new BoardAnswer.Created(known.Owners[0], created.Title.Trim());
                else if (known.Owners.FirstOrDefault(one => one.Login.Equals(created.Owner.Trim(), StringComparison.OrdinalIgnoreCase)) is { } whose)
                    return new BoardAnswer.Created(whose, created.Title.Trim());
                else
                    Reject($"{ProjectLabel}: A project cannot be made for {created.Owner.Trim()}, only for {string.Join(", ", known.Owners.Select(one => one.Login))}.");
                return null;

            case BoardProposal.None:
                return new BoardAnswer.None();

            default:
                return null;
        }
    }

    // A command around a task: the one its option names, or the one of the settings when no option speaks of it.
    // An empty one is refused as the settings refuse it, and the command stays as it is.
    string? Offered(CommandProposal? proposed, string? now, string option)
    {
        if (proposed is null)
            return now;
        if (proposed.Line is null || !string.IsNullOrWhiteSpace(proposed.Line))
            return proposed.Line?.Trim();
        Reject($"{option}: A command must not be empty: give --no-{option[2..]} to run none.");
        return now;
    }

    // The branch the tasks are done on: the one the option names, or the one of the settings when no option speaks
    // of it. A name git does not take is refused as the settings refuse it, and the branch stays as it is.
    string? OfferedBranch(BranchProposal? proposed, string? now)
    {
        if (proposed is null)
            return now;
        if (proposed.Name?.Trim() is not { } name)
            return null;
        if (name.Length == 0)
            Reject("--branch: A branch must not be empty: give --no-branch to name none.");
        else if (SettingsValidator.RefuseBranch(name) is { } reason)
            Reject($"--branch: “{name}” {reason}.");
        else
            return name;
        return now;
    }

    // What an option says cannot be taken. A setup that asks nothing stops there; one that asks says so and asks
    // its question as if the option were not given.
    void Reject(string reason)
    {
        if (unasked)
            throw new Refused(reason);
        if (!notes.Exists(note => note.Text == reason))
            notes.Add(new SetupNote(SetupTone.Warning, reason));
    }

    // The labels an option names, as the repository writes them. One that the repository does not have is rejected.
    List<string>? Among(List<string> all, IReadOnlyList<string>? named, string label)
    {
        if (named is null)
            return null;

        var found = new List<string>();
        foreach (var name in Names(string.Join(',', named)))
        {
            if (all.FirstOrDefault(one => one.Equals(name, StringComparison.OrdinalIgnoreCase)) is { } written)
                found.Add(written);
            else
                Reject($"{label}: {repository} has no label “{name}”.");
        }

        return found;
    }

    async Task<bool> AskBoardAsync(CancellationToken cancellationToken)
    {
        SetupNote[] refused = [];
        while (true)
        {
            // What the project has now: the board that was chosen a moment ago, the one of the settings,
            // or one that is linked to the repository already. Settings that name no board say that there is
            // none: only a first setup takes the linked one.
            var proposed = board ?? offeredBoard ?? (current is { Tracker.Board: null } ? new BoardAnswer.None() : null);
            var address = proposed switch
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
                title => string.IsNullOrWhiteSpace(title) ? "A project needs a title." : null,
                proposed is BoardAnswer.Existing ? null : proposed));
            if (answer is null)
                return false;

            if (answer is BoardAnswer.Existing chosen)
            {
                answer = chosen = new BoardAnswer.Existing(BoardSuggestions.AddressOf(chosen.Address));
                if (verified && await tracker.RefuseBoardAsync(chosen.Address, cancellationToken) is { } reason)
                {
                    if (unasked)
                        throw new Refused($"{ProjectLabel}: {reason}");
                    refused = [new SetupNote(SetupTone.Failure, reason)];
                    continue;
                }
            }

            // Nothing is made where nothing is verified, whatever the dialog answers.
            board = answer is BoardAnswer.Created && !verified ? new BoardAnswer.None() : answer;
            if (await AskStatusesAsync(cancellationToken))
                return true;

            // Going back from the statuses comes back to the board.
            refused = [];
        }
    }

    // Which status of the board is which: the one a task is taken from, and the two a run moves it to. A board
    // that has the three names GitHub gives is asked nothing about, unless the settings or an option call a status
    // otherwise; for another one each of the three is chosen among the statuses of the board, and typed where
    // nothing is verified. A new board has the names of GitHub, and a project without a board has none to name.
    // A board that cannot be asked about, with fewer than three statuses or with nobody to ask, is taken all the
    // same, and it is said: a run would take no task of it. One whose statuses cannot be read is left to the
    // doctor. False is going back from the first of the questions.
    async Task<bool> AskStatusesAsync(CancellationToken cancellationToken)
    {
        string?[] offered = [proposals.TodoStatus, proposals.InProgressStatus, proposals.DoneStatus];
        var given = BoardStatuses.Default.Names;
        lacking = [];
        if (board is not BoardAnswer.Existing taken)
        {
            for (var index = 0; index < offered.Length; index++)
            {
                if (offered[index] is not { } name)
                    continue;
                if (board is not BoardAnswer.Created)
                    Reject($"{StatusQuestions[index].Label}: A status is named for a project, and there is none.");
                else if (!BoardStatuses.Same(name, given[index]))
                    Reject($"{StatusQuestions[index].Label}: A new project has the statuses GitHub gives it: {string.Join(", ", given)}.");
            }

            (statuses, named, asked) = (BoardStatuses.Default, null, false);
            return true;
        }

        // What each question proposes: what was answered for this board before, else what the option says, else the
        // name of the settings, else the name GitHub gives.
        var again = taken.Address.Equals(named, StringComparison.OrdinalIgnoreCase);
        var kept = again ? statuses : current is { Tracker.Board: not null } ? current.Tracker.Statuses : BoardStatuses.Default;
        var wanted = kept.Names.ToArray();
        var optioned = new bool[wanted.Length];
        for (var index = 0; index < wanted.Length && !again; index++)
        {
            if (offered[index] is not { } name)
                continue;
            if (string.IsNullOrWhiteSpace(name))
                Reject($"{StatusQuestions[index].Label}: A status needs a name.");
            else
                (wanted[index], optioned[index]) = (name.Trim(), true);
        }

        // Two of the three must not be one status. What the options say is left out then, all of it.
        for (var index = 1; index < wanted.Length; index++)
        {
            if (Enumerable.Range(0, index).FirstOrDefault(before => BoardStatuses.Same(wanted[before], wanted[index]), -1) is >= 0 and var same)
            {
                Reject($"{StatusQuestions[index].Label}: {wanted[index]} is the {StatusQuestions[same].Label} too.");
                (wanted, optioned) = (kept.Names.ToArray(), new bool[wanted.Length]);
                break;
            }
        }

        var was = asked && again;
        (named, asked) = (taken.Address, false);
        if (!verified)
        {
            // Nothing is read, so nothing is chosen among: the names are typed, as the board calls them.
            var typed = wanted.ToArray();
            for (var at = 0; at < typed.Length;)
            {
                var index = at;
                Show(0, chosen: true);
                var answer = dialog.Ask(StatusQuestions[at].Label, $"{StatusQuestions[at].Hint} Type what the board calls it.", typed[at], text =>
                    string.IsNullOrWhiteSpace(text) ? "A status needs a name."
                    : Enumerable.Range(0, index).FirstOrDefault(before => BoardStatuses.Same(typed[before], text), -1) is >= 0 and var same ? $"{text.Trim()} is the {StatusQuestions[same].Label} too."
                    : null);
                if (answer is null && at == 0)
                    return false;
                if (answer is null)
                    at--;
                else
                    typed[at++] = answer.Trim();
            }

            (statuses, asked) = (new BoardStatuses(typed[0], typed[1], typed[2]), true);
            return true;
        }

        if (await tracker.ReadStatusesAsync(taken.Address, cancellationToken) is not { } has)
        {
            statuses = new BoardStatuses(wanted[0], wanted[1], wanted[2]);
            return true;
        }

        // A status of an option is one of the board, and is kept as the board writes it.
        var listed = has.Count == 0 ? "it has no statuses" : $"it has {string.Join(", ", has)}";
        for (var index = 0; index < wanted.Length; index++)
        {
            if (!optioned[index])
                continue;
            if (has.FirstOrDefault(one => BoardStatuses.Same(one, wanted[index])) is { } written)
                wanted[index] = written;
            else
                Reject($"{StatusQuestions[index].Label}: The board has no status “{wanted[index]}”: {listed}.");
        }

        statuses = new BoardStatuses(wanted[0], wanted[1], wanted[2]);
        var lack = statuses.Lack(has);
        var usual = !was && !optioned.Contains(true) && wanted.Zip(given).All(pair => BoardStatuses.Same(pair.First, pair.Second));
        if (lack is null && (unasked || usual))
            return true;

        if (has.Count < wanted.Length)
        {
            lacking =
            [
                new SetupNote(SetupTone.Warning, lack!),
                new SetupNote(SetupTone.Muted, "A run needs three statuses of a board: add them on GitHub and run `toobusy init` again. `toobusy doctor` fails until then."),
            ];
            return true;
        }

        if (unasked)
        {
            lacking =
            [
                new SetupNote(SetupTone.Warning, lack!),
                new SetupNote(SetupTone.Muted, "Say which of its statuses is which with `--todo-status`, `--in-progress-status` and `--done-status`, or with `toobusy init`: `toobusy doctor` fails until then."),
            ];
            return true;
        }

        var picked = wanted.ToArray();
        for (var at = 0; at < picked.Length;)
        {
            // A status that is one of the three already is not among the choices of the next. A board lists its
            // statuses in the order a task goes through them, so where it has no status of the name, what is
            // proposed is the first one for a task to do, the one after that for a task in progress, and the last
            // one for a task that is done.
            var rest = has.Where(one => !picked.Take(at).Any(before => BoardStatuses.Same(before, one))).ToList();
            var proposed = rest.FindIndex(one => BoardStatuses.Same(one, picked[at]));
            if (proposed < 0)
            {
                var after = at == 0 ? -1 : has.ToList().FindIndex(one => BoardStatuses.Same(one, picked[at - 1]));
                proposed = at == picked.Length - 1 ? rest.Count - 1 : Math.Max(0, rest.FindIndex(one => has.ToList().IndexOf(one) > after));
            }

            Show(0, chosen: true);
            var choice = dialog.Choose(StatusQuestions[at].Label, StatusQuestions[at].Hint, [.. rest.Select(one => new SetupOption(one, ""))], proposed);
            if (choice is null && at == 0)
                return false;
            if (choice is null)
                at--;
            else
                picked[at++] = rest[choice.Value];
        }

        (statuses, asked) = (new BoardStatuses(picked[0], picked[1], picked[2]), true);
        return true;
    }

    async Task<bool> AskBlockingAsync(CancellationToken cancellationToken)
    {
        if (!verified)
        {
            Show(1);
            var names = blocking ?? proposals.Blocking ?? current?.Queue.Labels.Blocking ?? [];
            var typed = dialog.Ask("Blocking labels", "Names separated by commas. A task with any of them is never taken.", string.Join(", ", names), _ => null);
            if (typed is not null)
                blocking = Names(typed);
            return typed is not null;
        }

        if (await AllLabelsAsync(1, BlockingLabel, BlockingHint, cancellationToken) is not { } all)
            return false;
        offeredBlocking ??= Among(all, proposals.Blocking, BlockingLabel);
        var proposed = blocking ?? offeredBlocking ?? current?.Queue.Labels.Blocking ?? [];
        Show(1);
        var picked = dialog.ChooseMany(BlockingLabel, BlockingHint, all, Indexes(all, proposed));
        if (picked is not null)
            blocking = [.. picked.Select(index => all[index])];
        return picked is not null;
    }

    async Task<bool> AskTakeAsync(CancellationToken cancellationToken)
    {
        if (!verified)
        {
            Show(2);
            var names = take ?? proposals.Take ?? current?.Queue.Labels.Take ?? [];
            var typed = dialog.Ask("Labels to take", "Names separated by commas. Leave empty to take any task.", string.Join(", ", names), text =>
                Names(text).Intersect(blocking!, StringComparer.OrdinalIgnoreCase).FirstOrDefault() is { } shared ? $"{shared} is a blocking label." : null);
            if (typed is not null)
                take = Names(typed);
            return typed is not null;
        }

        if (await AllLabelsAsync(2, TakeLabel, TakeHint, cancellationToken) is not { } all)
            return false;
        offeredTake ??= Among(all, proposals.Take, TakeLabel);
        var proposed = take ?? offeredTake ?? current?.Queue.Labels.Take ?? [];

        // A blocking label is not among the choices. Where nobody chooses, a proposed one would be dropped
        // without a word.
        if (unasked && proposed.FirstOrDefault(name => Has(blocking!, name)) is { } shared)
            throw new Refused($"{TakeLabel}: {shared} is a blocking label.");

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
        owner ?? proposals.Owner?.Trim() ?? current?.Queue.Labels.Owner ?? ProposedOwner,
        name => Has(take!, name) ? $"{name} is a label to take." : null,
        answer => owner = answer,
        cancellationToken);

    Task<bool> AskInterruptedAsync(CancellationToken cancellationToken) => AskLabelAsync(
        4,
        InterruptLabel,
        "A task that a run had to stop gets this label, and the next run takes it first.",
        interrupted ?? proposals.Interrupted?.Trim() ?? current?.Queue.Labels.Interrupted ?? ProposedInterrupted,
        name => Has(take!, name) ? $"{name} is a label to take."
            : Has(blocking!, name) ? $"{name} is a blocking label."
            : name.Equals(owner, StringComparison.OrdinalIgnoreCase) ? $"{name} is the label of the owner."
            : null,
        answer => interrupted = answer,
        cancellationToken);

    // The skill of the project a session starts with, by its name, or none: an empty answer. A first setup proposes
    // a name; settings that exist propose what they say, and none when they name none. The name is taken with the
    // slash of its command or without it.
    bool AskSkill()
    {
        static string Named(string text) => text.Trim().TrimStart('/');

        Show(5);
        var proposed = skill ?? proposals.Skill ?? (current is null ? ProposedSkill : current.Skill ?? "");
        var typed = dialog.Ask(SkillLabel, SkillHint, Named(proposed), text =>
            Named(text) is { Length: > 0 } name && SettingsValidator.RefuseSkill(name) is not null ? $"{name} is not the name of a skill: it is letters, digits and dashes, as {ProposedSkill}." : null);
        if (typed is not null)
            skill = Named(typed);
        return typed is not null;
    }

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

        // A skill that is named and not written yet is to be written before a run: a session that starts with a
        // skill the assistant does not have does nothing.
        unwritten = skill is { Length: > 0 } && environment.FindSkill(skill) is { Found: false } look
            ? [new SetupNote(SetupTone.Warning, $"There is no skill “{skill}” yet: write {look.Place} before a run."), new SetupNote(SetupTone.Muted, "`toobusy about` tells what the skill is to say.")]
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
            // No answer above names a command or the branch of the options: a first setup says them under them.
            coming.Add(new SetupNote(SetupTone.Plain, $"The answers above will be written to {store.DisplayPath}."));
            coming.AddRange(UnaskedChanges().Select(change => new SetupNote(SetupTone.Change, $"{change.Label}: {change.Value}")));
        }

        if (made is not null)
            coming.Add(new SetupNote(SetupTone.Plain, $"The project “{made.Title}” will be made for {made.Owner.Login}."));
        if (linking)
            coming.Add(new SetupNote(SetupTone.Plain, $"The project will be linked to {repository}."));
        coming.AddRange(missing.Select(name => new SetupNote(SetupTone.Plain, $"The label “{name}” will be made in {repository}.")));
        coming.AddRange(lacking);
        coming.AddRange(unwritten);

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
            {
                address = await tracker.CreateBoardAsync(made.Owner, made.Title, cancellationToken);
                done.Add($"The project “{made.Title}” was made for {made.Owner.Login}: {address}");
            }

            if (linking)
            {
                await tracker.LinkBoardAsync(address!, repository, cancellationToken);
                done.Add($"The project was linked to {repository}.");
            }

            foreach (var name in missing)
            {
                await tracker.CreateLabelAsync(repository, name, cancellationToken);
                done.Add($"The label “{name}” was made in {repository}.");
            }
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

            // The statuses are those of a board: a project that leaves its board says so in the line above.
            (StatusesLabel, Listed(before.Tracker.Statuses), Listed(address is null ? before.Tracker.Statuses : made is null ? statuses : BoardStatuses.Default)),
            (OwnerLabel, before.Queue.Labels.Owner, owner!),
            (InterruptLabel, before.Queue.Labels.Interrupted, interrupted!),
            (SkillLabel, before.Skill ?? "none", skill is { Length: > 0 } ? skill : "none"),
        ];
        return
        [
            .. settings.Where(setting => setting.Before != setting.After).Select(setting => $"{setting.Name}: {setting.Before} → {setting.After}"),
            .. UnaskedChanges().Select(change => $"{change.Label}: {change.Value}"),
        ];
    }

    // The commands around a task and the branch that differ from those of the settings, each as `before → after`.
    List<SetupAnswer> UnaskedChanges()
    {
        var before = current?.Commands ?? TaskCommands.None;
        (string Name, string? Before, string? After)[] unasked =
        [
            (BeforeLabel, before.Before, commands.Before),
            (AfterLabel, before.After, commands.After),
            (BranchLabel, current?.Branch, branch),
        ];
        return [.. unasked.Where(setting => setting.Before != setting.After).Select(setting => new SetupAnswer(setting.Name, $"{setting.Before ?? "none"} → {setting.After ?? "none"}"))];
    }

    // A board as people know it: by its title when it is known, with its address.
    string Board(string? address) =>
        address is null ? "none" : Known(address) is { Title.Length: > 0 } named ? $"{named.Title}  {named.Address}" : address;

    static string Listed(BoardStatuses statuses) => string.Join(", ", statuses.Names);

    ProjectSettings Settings(string? address) => new(
        new TrackerSettings(SettingsKeys.GitHubTracker, address) { Statuses = board is BoardAnswer.Existing ? statuses : BoardStatuses.Default },
        new QueueSettings(new LabelSettings(blocking!, take!, owner!, interrupted!)),
        new AssistantSettings(SettingsKeys.ClaudeCodeAssistant))
    {
        Skill = skill is { Length: > 0 } ? skill : null,

        // The commands around a task and the branch are not asked for: they are those of the file, but for what the
        // options say.
        Commands = commands,
        Branch = branch,
    };

    void Show(int step, IReadOnlyList<SetupNote>? more = null, bool chosen = false) =>
        dialog.Show(new SetupProgress([.. notes, .. more ?? []], Answers(step, chosen), Steps, step) { Later = chosen });

    // The answers of the steps before the given one, with the two that are never asked around them. The board is
    // among them once it is `chosen`, while its statuses are asked for; the statuses are once the step is over,
    // when the board does not call them as GitHub does.
    List<SetupAnswer> Answers(int step, bool chosen = false)
    {
        var answers = new List<SetupAnswer> { new("Tracker", "GitHub") };
        if (step > 0 || chosen)
        {
            answers.Add(new SetupAnswer("Project", board switch
            {
                BoardAnswer.Existing existing => Board(existing.Address),
                BoardAnswer.Created made => $"new: {made.Title} ({made.Owner.Login})",
                _ => "none",
            }));
        }

        if (step > 0 && board is BoardAnswer.Existing && statuses != BoardStatuses.Default)
            answers.Add(new SetupAnswer(StatusesLabel, Listed(statuses)));

        if (step > 1)
            answers.Add(new SetupAnswer("Blocking labels", blocking!.Count == 0 ? "none" : string.Join(", ", blocking)));
        if (step > 2)
            answers.Add(new SetupAnswer("Labels to take", take!.Count == 0 ? "any task" : string.Join(", ", take)));
        if (step > 3)
            answers.Add(new SetupAnswer(OwnerLabel, owner!));
        if (step > 4)
            answers.Add(new SetupAnswer(InterruptLabel, interrupted!));
        if (step > 5)
        {
            answers.Add(new SetupAnswer(SkillLabel, skill is { Length: > 0 } ? skill : "none"));
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

    // A proposal that did not pass the check of its question, where nobody can be asked again.
    sealed class Refused(string reason) : Exception(reason);

    // The dialog of a setup that asks nothing: every question is answered with what it proposes, the last one with
    // a yes, and nobody goes back. A proposed text that its question refuses cannot be asked for again.
    sealed class Unasked : ISetupDialog
    {
        public void Show(SetupProgress progress)
        {
        }

        public async Task<T?> WaitAsync<T>(string label, string hint, string text, Func<CancellationToken, Task<T>> work, CancellationToken cancellationToken)
            where T : class =>
            await work(cancellationToken);

        public int? Choose(string label, string hint, IReadOnlyList<SetupOption> options, int proposed) => proposed;

        public IReadOnlyList<int>? ChooseMany(string label, string hint, IReadOnlyList<string> options, IReadOnlyList<int> proposed) => proposed;

        public string? Ask(string label, string hint, string proposed, Func<string, string?> refuse) =>
            refuse(proposed) is { } reason ? throw new Refused($"{label}: {reason}") : proposed;

        public BoardAnswer? AskBoard(BoardQuestion question) =>
            question.Current is { } current ? new BoardAnswer.Existing(current.Address) : question.Proposed ?? new BoardAnswer.None();

        public bool? Confirm(string question) => true;
    }
}
