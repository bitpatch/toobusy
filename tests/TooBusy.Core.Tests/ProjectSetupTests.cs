using TooBusy.Core.Settings;
using TooBusy.Core.Setup;

namespace TooBusy.Core.Tests;

public class ProjectSetupTests
{
    const string Rocket = "https://github.com/orgs/acme/projects/1";
    const string Moon = "https://github.com/users/denis/projects/7";

    static readonly ProjectSettings Existing = new(
        new TrackerSettings("github", Rocket),
        new QueueSettings(new LabelSettings(["manual"], ["bug", "retired"])),
        new AssistantSettings("claude-code"));

    readonly ScriptedDialog dialog = new();
    readonly FakeMachine machine = new();
    readonly FakeTracker tracker = new();
    readonly FakeBoards boards = new();
    readonly FakeStore store = new();

    [Fact]
    public async Task AFirstSetupProposesWhatItFindsAndWritesIt()
    {
        var result = await RunAsync();

        Assert.Equal(SetupOutcome.Written, result.Outcome);
        Assert.Equal(new TrackerSettings("github", null), store.Saved!.Tracker);
        Assert.Empty(store.Saved.Queue.Labels.Blocking);
        Assert.Empty(store.Saved.Queue.Labels.Take);
        Assert.Equal(new AssistantSettings("claude-code"), store.Saved.Assistant);
        Assert.Equal(["Project", "Blocking labels", "Labels to take", "Write the settings?"], dialog.Asked);
    }

    [Fact]
    public async Task AFirstSetupSaysThatTheAnswersWillBeWritten()
    {
        await RunAsync();

        Assert.Equal([new SetupNote(SetupTone.Plain, "The answers above will be written to .toobusy/settings.toml.")], dialog.Shown.Notes);
        Assert.Equal(5, dialog.Shown.Answers.Count);
    }

    [Fact]
    public async Task TheAnswersBecomeTheSettings()
    {
        dialog.Board(new BoardAnswer.Existing($" {Moon}/views/2?layout=board "));
        dialog.Answer("Blocking labels", "manual", "draft");
        dialog.Answer("Labels to take", "bug");

        var result = await RunAsync();

        Assert.Equal(new TrackerSettings("github", Moon), store.Saved!.Tracker);
        Assert.Equal(["draft", "manual"], store.Saved.Queue.Labels.Blocking);
        Assert.Equal(["bug"], store.Saved.Queue.Labels.Take);
        Assert.Equal(
            [
                new SetupAnswer("Tracker", "GitHub"),
                new SetupAnswer("Project", Moon),
                new SetupAnswer("Blocking labels", "draft, manual"),
                new SetupAnswer("Labels to take", "bug"),
                new SetupAnswer("Assistant", "Claude Code"),
            ],
            result.Answers);
    }

    [Fact]
    public async Task TheStepsAndThePlaceAmongThemAreShown()
    {
        await RunAsync();

        // At the confirmation every step is done.
        Assert.Equal(["Project", "Blocking labels", "Labels to take"], dialog.Shown.Steps);
        Assert.Equal([0, 1, 2, 3], dialog.Places.Distinct());
        Assert.Equal(dialog.Shown.Steps.Count, dialog.Shown.Step);
    }

    [Fact]
    public async Task TheBoardQuestionOffersTheBoardsAndTheOwnersThatWereRead()
    {
        boards.Read = new SetupBoards([new(Rocket, "Rocket", false), new(Moon, "Moon", false)], [new("denis", false), new("acme", true)]);

        await RunAsync();

        Assert.Equal("acme/rocket", boards.Repository);
        Assert.Equal(boards.Read.Boards, dialog.Question!.Known);
        Assert.Equal([new SetupOwner("acme", true), new SetupOwner("denis", false)], dialog.Question.Owners);
        Assert.Null(dialog.Question.Current);
    }

    [Fact]
    public async Task ABoardThatIsLinkedToTheRepositoryIsTheOneTheProjectHas()
    {
        boards.Read = new SetupBoards([new(Moon, "Moon", false), new(Rocket, "Rocket", true)], []);

        var result = await RunAsync();

        Assert.Equal(new SetupBoard(Rocket, "Rocket", true), dialog.Question!.Current);
        Assert.Equal(Rocket, store.Saved!.Tracker.Board);
        Assert.Contains(new SetupAnswer("Project", $"Rocket  {Rocket}"), result.Answers);
        Assert.Empty(tracker.Linked);
    }

    [Fact]
    public async Task ABoardThatIsNotLinkedYetIsLinkedWhenTheSetupIsConfirmed()
    {
        boards.Read = new SetupBoards([new(Moon, "Moon", false)], []);
        dialog.Board(new BoardAnswer.Existing(Moon));

        await RunAsync();

        Assert.Contains(new SetupNote(SetupTone.Plain, "The project will be linked to acme/rocket."), dialog.Shown.Notes);
        Assert.Equal([(Moon, "acme/rocket")], tracker.Linked);
    }

    [Fact]
    public async Task ANewBoardIsMadeAndLinkedOnlyWhenTheSetupIsConfirmed()
    {
        dialog.Board(new BoardAnswer.Created(new SetupOwner("acme", true), "Rocket"));
        tracker.OnConfirm = () => Assert.Empty(tracker.Made);
        dialog.BeforeConfirm = tracker.OnConfirm;

        var result = await RunAsync();

        Assert.Contains(new SetupNote(SetupTone.Plain, "The project “Rocket” will be made for acme."), dialog.Shown.Notes);
        Assert.Contains(new SetupAnswer("Project", "new: Rocket (acme)"), dialog.Shown.Answers);
        Assert.Equal([(new SetupOwner("acme", true), "Rocket")], tracker.Made);
        Assert.Equal([("https://github.com/orgs/acme/projects/42", "acme/rocket")], tracker.Linked);
        Assert.Equal("https://github.com/orgs/acme/projects/42", store.Saved!.Tracker.Board);
        Assert.Contains(new SetupAnswer("Project", "new: Rocket (acme)"), result.Answers);
    }

    [Fact]
    public async Task ABoardThatCannotBeMadeFailsTheSetupAndWritesNothing()
    {
        dialog.Board(new BoardAnswer.Created(new SetupOwner("acme", true), "Rocket"));
        tracker.RefuseToMake = "acme does not let you make projects.";

        var result = await RunAsync();

        Assert.Equal(SetupOutcome.Failed, result.Outcome);
        Assert.Equal("acme does not let you make projects.", result.Failure);
        Assert.Empty(tracker.Linked);
        Assert.Null(store.Saved);
    }

    [Fact]
    public async Task ABoardThatIsMadeButCannotBeLinkedIsNamedInTheFailure()
    {
        dialog.Board(new BoardAnswer.Created(new SetupOwner("acme", true), "Rocket"));
        tracker.RefuseToLink = "The repository cannot be changed.";

        var result = await RunAsync();

        Assert.Equal(SetupOutcome.Failed, result.Outcome);
        Assert.Equal("The project was made at https://github.com/orgs/acme/projects/42, but then: The repository cannot be changed.", result.Failure);
        Assert.Null(store.Saved);
    }

    [Fact]
    public async Task DecliningMakesNothingAndWritesNothing()
    {
        dialog.Board(new BoardAnswer.Created(new SetupOwner("acme", true), "Rocket"));
        dialog.Confirm(false);

        var result = await RunAsync();

        Assert.Equal(SetupOutcome.Declined, result.Outcome);
        Assert.Empty(tracker.Made);
        Assert.Empty(tracker.Linked);
        Assert.Null(store.Saved);
    }

    [Fact]
    public async Task ABlockingLabelIsNotOfferedToTake()
    {
        dialog.Answer("Blocking labels", "manual");

        await RunAsync();

        Assert.Equal(["bug", "draft", "feature"], dialog.Offered["Labels to take"]);
    }

    [Fact]
    public async Task GoingBackAsksTheStepBeforeAndProposesWhatWasAnswered()
    {
        dialog.Answer("Blocking labels", "manual");
        dialog.Back("Labels to take");

        await RunAsync();

        Assert.Equal(["Project", "Blocking labels", "Labels to take", "Blocking labels", "Labels to take", "Write the settings?"], dialog.Asked);
        Assert.Equal(["manual"], store.Saved!.Queue.Labels.Blocking);
    }

    [Fact]
    public async Task GoingBackFromTheConfirmationAsksTheLastStepAgain()
    {
        dialog.Confirm(null, true);

        var result = await RunAsync();

        Assert.Equal(SetupOutcome.Written, result.Outcome);
        Assert.Equal(["Labels to take", "Write the settings?", "Labels to take", "Write the settings?"], dialog.Asked.Skip(2));
    }

    [Fact]
    public async Task GoingBackFromTheFirstStepLeavesTheSetup()
    {
        dialog.Back("Blocking labels");
        dialog.Board(null);

        var result = await RunAsync();

        Assert.Equal(new SetupResult(SetupOutcome.Left, []), result, (left, right) => left!.Outcome == right!.Outcome && left.Answers.Count == right.Answers.Count);
        Assert.Null(store.Saved);
    }

    [Fact]
    public async Task ABoardAddressThatCannotBeRightIsRefusedByTheQuestion()
    {
        await RunAsync();

        Assert.NotNull(dialog.Question!.RefuseAddress("https://example.com/board"));
        Assert.Null(dialog.Question.RefuseAddress($"{Moon}/views/1"));
        Assert.NotNull(dialog.Question.RefuseTitle("  "));
        Assert.Null(dialog.Question.RefuseTitle("Rocket"));
    }

    [Fact]
    public async Task ABoardThatCannotBeReadIsAskedAgainWithTheReason()
    {
        tracker.Unreadable.Add(Rocket);
        dialog.Board(new BoardAnswer.Existing(Rocket));
        dialog.Board(new BoardAnswer.Existing(Moon));

        await RunAsync();

        Assert.Contains(dialog.Seen, progress => progress.Step == 0 && progress.Notes.Contains(new SetupNote(SetupTone.Failure, $"{Rocket} cannot be read")));
        Assert.Equal(Moon, store.Saved!.Tracker.Board);
    }

    [Fact]
    public async Task AnExistingSetupProposesItsValuesSoThatAcceptingThemChangesNothing()
    {
        store.Current = Existing;
        boards.Read = new SetupBoards([new(Rocket, "Rocket", true)], []);

        var result = await RunAsync();

        Assert.Equal(SetupOutcome.NothingToChange, result.Outcome);
        Assert.Null(store.Saved);
        Assert.DoesNotContain("Write the settings?", dialog.Asked);
    }

    [Fact]
    public async Task AnExistingSetupWhoseBoardIsNotLinkedOffersOnlyToLinkIt()
    {
        store.Current = Existing;

        var result = await RunAsync();

        Assert.Equal(SetupOutcome.Written, result.Outcome);
        Assert.Equal("Link the project?", dialog.Asked[^1]);
        Assert.Equal([(Rocket, "acme/rocket")], tracker.Linked);
        Assert.Null(store.Saved);
    }

    [Fact]
    public async Task AnExistingSetupShowsOnlyWhatChanges()
    {
        store.Current = Existing;
        dialog.Board(new BoardAnswer.None());

        await RunAsync();

        Assert.Equal(
            [
                new SetupNote(SetupTone.Plain, "This will change in .toobusy/settings.toml:"),
                new SetupNote(SetupTone.Change, $"Project: {Rocket} → none"),
            ],
            dialog.Shown.Notes);
        Assert.Equal(Existing with { Tracker = Existing.Tracker with { Board = null } }, store.Saved, SameSettings);
    }

    [Fact]
    public async Task EverySettingThatChangesIsSaidAsItWasAndAsItWillBe()
    {
        store.Current = Existing;
        boards.Read = new SetupBoards([new(Rocket, "Rocket", true), new(Moon, "Moon", true)], []);
        dialog.Board(new BoardAnswer.Existing(Moon));
        dialog.Answer("Blocking labels");
        dialog.Answer("Labels to take", "bug");

        await RunAsync();

        Assert.Equal(
            [
                $"Project: Rocket  {Rocket} → Moon  {Moon}",
                "Blocking labels: manual → none",
                "Labels to take: bug, retired → bug",
            ],
            dialog.Shown.Notes.Where(note => note.Tone == SetupTone.Change).Select(note => note.Text));
    }

    [Fact]
    public async Task SettingsThatDoNotValidateAreReported()
    {
        store.Errors = [new SettingsError("tracker.type", 5, "is missing")];

        var result = await RunAsync();

        Assert.Contains(new SetupNote(SetupTone.Failure, ".toobusy/settings.toml:5: tracker.type: is missing"), dialog.Shown.Notes);
        Assert.Equal(SetupOutcome.Written, result.Outcome);
    }

    [Fact]
    public async Task ProblemsOfTheMachineAreShownWithTheirFixesAndDoNotStopTheSetup()
    {
        machine.Problems = [new SetupProblem("Claude Code is not installed", "curl -fsSL https://claude.ai/install.sh | bash")];

        var result = await RunAsync();

        Assert.Equal(
            [new SetupNote(SetupTone.Failure, "Claude Code is not installed"), new SetupNote(SetupTone.Muted, "fix: curl -fsSL https://claude.ai/install.sh | bash")],
            dialog.Shown.Notes.Take(2));
        Assert.Equal(SetupOutcome.Written, result.Outcome);
    }

    [Fact]
    public async Task WithoutTheTrackerTheAnswersAreTypedAndNothingIsVerified()
    {
        machine.TrackerReachable = false;
        tracker.Unreadable.Add(Rocket);
        dialog.Board(new BoardAnswer.Existing(Rocket));
        dialog.Answer("Blocking labels", "manual, draft,");
        dialog.Answer("Labels to take", "draft");
        dialog.Answer("Labels to take", "bug, feature");

        await RunAsync();

        Assert.Contains(dialog.Shown.Notes, note => note.Tone == SetupTone.Warning && note.Text.Contains("nothing is verified", StringComparison.Ordinal));
        Assert.Equal(0, tracker.Calls);
        Assert.Null(boards.Repository);
        Assert.Equal(Rocket, store.Saved!.Tracker.Board);
        Assert.Equal(["manual", "draft"], store.Saved.Queue.Labels.Blocking);
        Assert.Equal(["bug", "feature"], store.Saved.Queue.Labels.Take);
        Assert.Equal(["draft"], dialog.Refused);
    }

    [Fact]
    public async Task WithoutAGitHubOriginTheAnswersAreTypedAndNothingIsVerified()
    {
        machine.Origin = null;
        dialog.Board(new BoardAnswer.Existing(Rocket));
        dialog.Answer("Blocking labels", "manual");

        await RunAsync();

        Assert.Contains(dialog.Shown.Notes, note => note.Tone == SetupTone.Warning && note.Text.Contains("`origin`", StringComparison.Ordinal));
        Assert.Equal(0, tracker.Calls);
        Assert.Empty(tracker.Linked);
        Assert.Equal(["manual"], store.Saved!.Queue.Labels.Blocking);
    }

    [Fact]
    public async Task WhatTakesAMomentIsSaid()
    {
        await RunAsync();

        Assert.Equal(["Reading your projects from GitHub…", "Reading the labels…"], dialog.Waited);
    }

    Task<SetupResult> RunAsync() => new ProjectSetup(dialog, machine, tracker, boards, store).RunAsync(TestContext.Current.CancellationToken);

    static bool SameSettings(ProjectSettings? left, ProjectSettings? right) => FakeStore.Render(left!) == FakeStore.Render(right!);

    // Answers each question from its script and accepts what is proposed when the script has nothing for it.
    // A board that the project has is kept; without one the answer is no board.
    sealed class ScriptedDialog : ISetupDialog
    {
        readonly Dictionary<string, Queue<string[]?>> answers = [];
        readonly Queue<BoardAnswer?> boards = new();
        readonly Queue<bool?> confirmations = new();

        // The names of the questions in the order they were asked.
        public List<string> Asked { get; } = [];

        public List<SetupProgress> Seen { get; } = [];

        public SetupProgress Shown => Seen[^1];

        // The step that was shown, for each time something was shown.
        public IEnumerable<int> Places => Seen.Select(progress => progress.Step);

        public List<string> Waited { get; } = [];

        public List<string> Refused { get; } = [];

        // The details of a selection, or the options of a multiple choice, under the label of the question.
        public Dictionary<string, string[]> Offered { get; } = [];

        public BoardQuestion? Question { get; private set; }

        public Action? BeforeConfirm { get; set; }

        public void Answer(string label, params string[] answer) => Script(label).Enqueue(answer);

        public void Back(string label) => Script(label).Enqueue(null);

        public void Board(BoardAnswer? answer) => boards.Enqueue(answer);

        public void Confirm(params bool?[] answers)
        {
            foreach (var answer in answers)
                confirmations.Enqueue(answer);
        }

        public void Show(SetupProgress progress) => Seen.Add(progress);

        public void Wait(string text) => Waited.Add(text);

        public int? Choose(string label, string hint, IReadOnlyList<SetupOption> options, int proposed)
        {
            Asked.Add(label);
            Offered[label] = [.. options.Select(option => option.Detail)];
            if (!Next(label, out var answer))
                return proposed;
            return answer is null ? null : options.Select(option => option.Name).ToList().IndexOf(answer[0]);
        }

        public IReadOnlyList<int>? ChooseMany(string label, string hint, IReadOnlyList<string> options, IReadOnlyList<int> proposed)
        {
            Asked.Add(label);
            Offered[label] = [.. options];
            if (!Next(label, out var answer))
                return proposed;
            return answer is null ? null : [.. answer.Select(name => options.ToList().IndexOf(name)).Order()];
        }

        public string? Ask(string label, string hint, string proposed, Func<string, string?> refuse)
        {
            Asked.Add(label);
            while (true)
            {
                var scripted = Next(label, out var answer);
                if (scripted && answer is null)
                    return null;

                var text = scripted ? answer![0] : proposed;
                if (refuse(text) is null)
                    return text;
                Refused.Add(text);
            }
        }

        public BoardAnswer? AskBoard(BoardQuestion question)
        {
            Asked.Add(question.Label);
            Question = question;
            if (boards.Count > 0)
                return boards.Dequeue();
            return question.Current is { } current ? new BoardAnswer.Existing(current.Address) : new BoardAnswer.None();
        }

        public bool? Confirm(string question)
        {
            Asked.Add(question);
            BeforeConfirm?.Invoke();
            return confirmations.Count > 0 ? confirmations.Dequeue() : true;
        }

        Queue<string[]?> Script(string label)
        {
            if (!answers.TryGetValue(label, out var queue))
                answers.Add(label, queue = new Queue<string[]?>());
            return queue;
        }

        // False when the script has nothing for the question; a null answer is going back.
        bool Next(string label, out string[]? answer)
        {
            answer = null;
            return answers.TryGetValue(label, out var queue) && queue.TryDequeue(out answer);
        }
    }

    sealed class FakeMachine : ISetupEnvironment
    {
        public IReadOnlyList<SetupProblem> Problems { get; set; } = [];

        public bool TrackerReachable { get; set; } = true;

        public string? Origin { get; set; } = "acme/rocket";

        public Task<SetupEnvironment> InspectAsync(CancellationToken cancellationToken) =>
            Task.FromResult(new SetupEnvironment(Problems, TrackerReachable, Origin));
    }

    sealed class FakeBoards : ISetupBoards
    {
        public SetupBoards Read { get; set; } = SetupBoards.None;

        // The repository the boards were read for; null when they were not read.
        public string? Repository { get; private set; }

        public Task<SetupBoards> ReadAsync(string? repository, CancellationToken cancellationToken)
        {
            Repository = repository;
            return Task.FromResult(Read);
        }
    }

    sealed class FakeTracker : ISetupTracker
    {
        public HashSet<string> Unreadable { get; } = [];

        public int Calls { get; private set; }

        public List<(SetupOwner Owner, string Title)> Made { get; } = [];

        public List<(string Board, string Repository)> Linked { get; } = [];

        public Action? OnConfirm { get; set; }

        public string? RefuseToMake { get; set; }

        public string? RefuseToLink { get; set; }

        public Task<string?> RefuseBoardAsync(string board, CancellationToken cancellationToken)
        {
            Calls++;
            return Task.FromResult(Unreadable.Contains(board) ? $"{board} cannot be read" : null);
        }

        public Task<IReadOnlyList<string>> ReadLabelsAsync(string repository, CancellationToken cancellationToken)
        {
            Calls++;
            return Task.FromResult<IReadOnlyList<string>>(["bug", "draft", "feature", "manual"]);
        }

        public Task<string> CreateBoardAsync(SetupOwner owner, string title, CancellationToken cancellationToken)
        {
            if (RefuseToMake is not null)
                throw new TrackerException(RefuseToMake);
            Made.Add((owner, title));
            return Task.FromResult($"https://github.com/orgs/{owner.Login}/projects/42");
        }

        public Task LinkBoardAsync(string board, string repository, CancellationToken cancellationToken)
        {
            if (RefuseToLink is not null)
                throw new TrackerException(RefuseToLink);
            Linked.Add((board, repository));
            return Task.CompletedTask;
        }
    }

    // Keeps the settings in memory and shows them as one `key = value` line each.
    sealed class FakeStore : ISettingsStore
    {
        public ProjectSettings? Current { get; set; }

        public IReadOnlyList<SettingsError>? Errors { get; set; }

        public ProjectSettings? Saved { get; private set; }

        public string DisplayPath => ".toobusy/settings.toml";

        public SettingsValidation? Load() =>
            Errors is not null ? new SettingsValidation(null, Errors) : Current is null ? null : new SettingsValidation(Current, []);

        public SettingsPreview Preview(ProjectSettings settings) => new(Current is null ? "" : Render(Current), Render(settings));

        public void Save(ProjectSettings settings) => Saved = settings;

        public static string Render(ProjectSettings settings) => string.Join('\n',
            $"board = {settings.Tracker.Board}",
            $"blocking = {string.Join(", ", settings.Queue.Labels.Blocking)}",
            $"take = {string.Join(", ", settings.Queue.Labels.Take)}") + "\n";
    }
}
