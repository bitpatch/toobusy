using TooBusy.Core.Queue;
using TooBusy.Core.Settings;
using TooBusy.Core.Setup;

namespace TooBusy.Core.Tests;

public class ProjectSetupTests
{
    const string Rocket = "https://github.com/orgs/acme/projects/1";
    const string Moon = "https://github.com/users/denis/projects/7";

    const string ManualLabel = "Manual label";
    const string InterruptLabel = "Interrupt label";
    const string NewLabel = "New label…";
    const string TodoStatus = "Todo status";
    const string InProgressStatus = "In Progress status";
    const string DoneStatus = "Done status";
    const string BoardsUnanswered = "GitHub did not answer, so your projects cannot be listed.";

    static readonly ProjectSettings Existing = new(
        new TrackerSettings("github", Rocket),
        new QueueSettings(new LabelSettings(["manual"], ["bug", "retired"], "manual", "interrupted")),
        new AssistantSettings("claude-code"));

    // Settings whose manual label is not a blocking one too, as the menu leaves them.
    static readonly ProjectSettings Kept = Existing with { Queue = new QueueSettings(new LabelSettings(["draft"], ["bug", "retired"], "manual", "interrupted")) };

    readonly ScriptedDialog dialog = new();
    readonly FakeEnvironment machine = new();
    readonly FakeSetupTracker tracker = new();
    readonly FakeBoards boards = new();
    readonly FakeSettingsStore store = new();

    [Fact]
    public async Task AFirstSetupProposesWhatItFindsAndWritesIt()
    {
        var result = await RunAsync();

        Assert.Equal(SetupOutcome.Written, result.Outcome);
        Assert.Equal(new TrackerSettings("github", null), store.Saved!.Tracker);
        Assert.Empty(store.Saved.Queue.Labels.Blocking);
        Assert.Empty(store.Saved.Queue.Labels.Take);
        Assert.Equal("manual", store.Saved.Queue.Labels.Manual);
        Assert.Equal("interrupted", store.Saved.Queue.Labels.Interrupted);
        Assert.Equal(new AssistantSettings("claude-code"), store.Saved.Assistant);

        // The manual label is one of the repository, so the list points at it.
        Assert.Equal(["Project", "Blocking labels", "Labels to take", ManualLabel, InterruptLabel, "Skill", "Write the settings?"], dialog.Asked);
        Assert.Equal("task-auto", store.Saved.Skill);
        Assert.Single(dialog.Shown.Notes);
    }

    [Fact]
    public async Task AFirstSetupSaysThatTheAnswersWillBeWritten()
    {
        tracker.Labels = ["bug", "draft", "feature", "interrupted"];

        await RunAsync();

        Assert.Equal(
            [
                new SetupNote(SetupTone.Plain, "The answers above will be written to .toobusy/settings.toml."),
                new SetupNote(SetupTone.Plain, "The label “manual” will be made in acme/rocket."),
            ],
            dialog.Shown.Notes);
        Assert.Equal(8, dialog.Shown.Answers.Count);

        // The manual label is not one of the repository yet, so its name is asked for after the list.
        Assert.Equal(["Project", "Blocking labels", "Labels to take", ManualLabel, ManualLabel, InterruptLabel, "Skill", "Write the settings?"], dialog.Asked);
        Assert.Equal("manual", store.Saved!.Queue.Labels.Manual);
    }

    [Fact]
    public async Task TheAnswersBecomeTheSettings()
    {
        dialog.Board(new BoardAnswer.Existing($" {Moon}/views/2?layout=board "));
        dialog.Answer("Blocking labels", "manual", "draft");
        dialog.Answer("Labels to take", "bug");
        dialog.Answer(ManualLabel, "manual");
        dialog.Answer(InterruptLabel, "interrupted");

        var result = await RunAsync();

        Assert.Equal(new TrackerSettings("github", Moon), store.Saved!.Tracker);
        Assert.Equal(["draft", "manual"], store.Saved.Queue.Labels.Blocking);
        Assert.Equal(["bug"], store.Saved.Queue.Labels.Take);
        Assert.Equal("manual", store.Saved.Queue.Labels.Manual);
        Assert.Equal("interrupted", store.Saved.Queue.Labels.Interrupted);
        Assert.Equal(
            [
                new SetupAnswer("Tracker", "GitHub"),
                new SetupAnswer("Project", Moon),
                new SetupAnswer("Blocking labels", "draft, manual"),
                new SetupAnswer("Labels to take", "bug"),
                new SetupAnswer(ManualLabel, "manual"),
                new SetupAnswer(InterruptLabel, "interrupted"),
                new SetupAnswer("Skill", "task-auto"),
                new SetupAnswer("Assistant", "Claude Code"),
            ],
            result.Answers);
    }

    [Fact]
    public async Task TheStepsAndThePlaceAmongThemAreShown()
    {
        await RunAsync();

        // At the confirmation every step is done.
        Assert.Equal(["Project", "Blocking labels", "Labels to take", ManualLabel, InterruptLabel, "Skill"], dialog.Shown.Steps);
        Assert.Equal([0, 1, 2, 3, 4, 5, 6], dialog.Places.Distinct());
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
        Assert.Null(dialog.Question.Unread);
    }

    // A GitHub that did not answer in time for the boards is not one that has none: the question says so.
    [Fact]
    public async Task BoardsThatGitHubDidNotAnswerForAreSaidInTheQuestion()
    {
        boards.Read = SetupBoards.NoAnswer;

        await RunAsync();

        Assert.Empty(dialog.Question!.Known);
        Assert.Empty(dialog.Question.Owners);
        Assert.Equal(BoardsUnanswered, dialog.Question.Unread);
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
        Assert.Empty(tracker.Labelled);
        Assert.Null(store.Saved);
    }

    [Fact]
    public async Task ABlockingLabelIsNotOfferedToTake()
    {
        dialog.Answer("Blocking labels", "manual");

        await RunAsync();

        Assert.Equal(["bug", "draft", "feature", "interrupted"], dialog.Offered["Labels to take"]);
    }

    [Fact]
    public async Task ANewLabelIsNamedAndMadeOnlyWhenTheSetupIsConfirmed()
    {
        dialog.Answer(ManualLabel, NewLabel);
        dialog.Answer(ManualLabel, " ask the owner ");
        dialog.BeforeConfirm = () => Assert.Empty(tracker.Labelled);

        var result = await RunAsync();

        Assert.Contains(new SetupNote(SetupTone.Plain, "The label “ask the owner” will be made in acme/rocket."), dialog.Shown.Notes);
        Assert.Equal([("acme/rocket", "ask the owner")], tracker.Labelled);
        Assert.Equal("ask the owner", store.Saved!.Queue.Labels.Manual);
        Assert.Contains(new SetupAnswer(ManualLabel, "ask the owner"), result.Answers);
    }

    [Fact]
    public async Task GoingBackFromTheNameOfANewLabelComesBackToTheLabels()
    {
        dialog.Answer(ManualLabel, NewLabel);
        dialog.Back(ManualLabel);
        dialog.Answer(ManualLabel, "manual");

        await RunAsync();

        Assert.Equal([ManualLabel, ManualLabel, ManualLabel, InterruptLabel], dialog.Asked.Skip(3).Take(4));
        Assert.Equal("manual", store.Saved!.Queue.Labels.Manual);
    }

    [Fact]
    public async Task ALabelToTakeIsNotOfferedForTheOwnerAndIsRefusedAsANewOne()
    {
        dialog.Answer("Labels to take", "bug");
        dialog.Answer(ManualLabel, NewLabel);
        dialog.Answer(ManualLabel, "Bug");
        dialog.Answer(ManualLabel, "  ");
        dialog.Answer(ManualLabel, "manual");

        await RunAsync();

        Assert.Equal(["draft", "feature", "interrupted", "manual", NewLabel], dialog.Offered[ManualLabel]);
        Assert.Equal(["Bug", "  "], dialog.Refused);
        Assert.Equal("manual", store.Saved!.Queue.Labels.Manual);
    }

    [Fact]
    public async Task TheLabelOfInterruptedTasksIsNeitherBlockingNorTheOneOfTheOwner()
    {
        dialog.Answer("Blocking labels", "draft");
        dialog.Answer("Labels to take", "bug");
        dialog.Answer(ManualLabel, "manual");

        await RunAsync();

        Assert.Equal(["feature", "interrupted", NewLabel], dialog.Offered[InterruptLabel]);
    }

    [Fact]
    public async Task ALabelThatCannotBeMadeFailsTheSetupAndWritesNothing()
    {
        tracker.Labels = ["bug", "draft", "feature", "interrupted"];
        tracker.RefuseToLabel = "The label “manual” could not be made in acme/rocket.";

        var result = await RunAsync();

        Assert.Equal(SetupOutcome.Failed, result.Outcome);
        Assert.Equal("The label “manual” could not be made in acme/rocket.", result.Failure);
        Assert.Null(store.Saved);
    }

    [Fact]
    public async Task AnExistingSetupWhoseLabelTheRepositoryLostOffersOnlyToMakeIt()
    {
        store.Current = Existing with { Queue = new QueueSettings(Existing.Queue.Labels with { Interrupted = "paused" }) };
        boards.Read = new SetupBoards([new(Rocket, "Rocket", true)], []);

        var result = await RunAsync();

        Assert.Equal(SetupOutcome.Written, result.Outcome);
        Assert.False(result.SettingsWritten);
        Assert.Equal("Make the labels?", dialog.Asked[^1]);
        Assert.Equal([("acme/rocket", "paused")], tracker.Labelled);
        Assert.Null(store.Saved);
    }

    [Fact]
    public async Task GoingBackAsksTheStepBeforeAndProposesWhatWasAnswered()
    {
        dialog.Answer("Blocking labels", "manual");
        dialog.Back("Labels to take");

        await RunAsync();

        Assert.Equal(["Project", "Blocking labels", "Labels to take", "Blocking labels", "Labels to take"], dialog.Asked.Take(5));
        Assert.Equal(["manual"], store.Saved!.Queue.Labels.Blocking);
    }

    [Fact]
    public async Task GoingBackFromTheConfirmationAsksTheLastStepAgain()
    {
        dialog.Confirm(null, true);

        var result = await RunAsync();

        Assert.Equal(SetupOutcome.Written, result.Outcome);
        Assert.Equal(["Skill", "Write the settings?", "Skill", "Write the settings?"], dialog.Asked.Skip(5));
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

        // Settings that name no skill propose none.
        Assert.Contains(new SetupAnswer("Skill", "none"), result.Answers);
    }

    [Fact]
    public async Task AnExistingSetupWithoutABoardProposesNoneAndNotTheLinkedOne()
    {
        store.Current = Existing with { Tracker = new TrackerSettings("github", null) };
        boards.Read = new SetupBoards([new(Rocket, "Rocket", true), new(Moon, "Moon", false)], []);

        var result = await RunAsync();

        Assert.Null(dialog.Question!.Current);
        Assert.Equal(new BoardAnswer.None(), dialog.Question.Proposed);
        Assert.Equal(boards.Read.Boards, dialog.Question.Known);
        Assert.Equal(SetupOutcome.NothingToChange, result.Outcome);
        Assert.Null(store.Saved);
        Assert.Empty(tracker.Linked);
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
        dialog.Answer(ManualLabel, "draft");
        dialog.Answer(InterruptLabel, "feature");
        dialog.Answer("Skill", "task-auto");

        await RunAsync();

        Assert.Equal(
            [
                $"Project: Rocket  {Rocket} → Moon  {Moon}",
                "Blocking labels: manual → none",
                "Labels to take: bug, retired → bug",
                "Manual label: manual → draft",
                "Interrupt label: interrupted → feature",
                "Skill: none → task-auto",
            ],
            dialog.Shown.Notes.Where(note => note.Tone == SetupTone.Change).Select(note => note.Text));
        Assert.Equal("task-auto", store.Saved!.Skill);
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
        dialog.Answer(InterruptLabel, "manual");
        dialog.Answer(InterruptLabel, " paused ");

        await RunAsync();

        Assert.Contains(dialog.Shown.Notes, note => note.Tone == SetupTone.Warning && note.Text.Contains("nothing is verified", StringComparison.Ordinal));
        Assert.Equal(0, tracker.Calls);
        Assert.Null(boards.Repository);
        Assert.Equal(Rocket, store.Saved!.Tracker.Board);
        Assert.Equal(["manual", "draft"], store.Saved.Queue.Labels.Blocking);
        Assert.Equal(["bug", "feature"], store.Saved.Queue.Labels.Take);
        Assert.Equal("manual", store.Saved.Queue.Labels.Manual);
        Assert.Equal("paused", store.Saved.Queue.Labels.Interrupted);
        Assert.Equal(["draft", "manual"], dialog.Refused);
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
    public async Task WithoutAGitHubOriginTheBoardsAreOfferedAndANewOneIsNot()
    {
        machine.Origin = null;
        boards.Read = new SetupBoards([new(Moon, "Moon", false)], [new("denis", false), new("acme", true)]);

        await RunAsync();

        Assert.Equal(boards.Read.Boards, dialog.Question!.Known);
        Assert.Empty(dialog.Question.Owners);
    }

    [Fact]
    public async Task WithoutAGitHubOriginANewBoardOfAnOptionIsAWarningAndTheQuestionIsAskedWithoutIt()
    {
        machine.Origin = null;
        boards.Read = new SetupBoards([], [new("denis", false)]);

        var result = await RunAsync(new SetupProposals(new BoardProposal.Created("Rocket", null)));

        Assert.Contains(new SetupNote(SetupTone.Warning, "Project: A new project cannot be made where nothing is verified."), dialog.Shown.Notes);
        Assert.Null(dialog.Question!.Proposed);
        Assert.Empty(dialog.Question.Owners);
        Assert.Equal(SetupOutcome.Written, result.Outcome);
        Assert.Null(store.Saved!.Tracker.Board);
        Assert.Empty(tracker.Made);
    }

    [Fact]
    public async Task WithoutAGitHubOriginNoBoardIsMadeWhateverIsAnswered()
    {
        machine.Origin = null;
        dialog.Board(new BoardAnswer.Created(new SetupOwner("denis", false), "Rocket"));

        var result = await RunAsync();

        Assert.Equal(new SetupAnswer("Project", "none"), result.Answers[1]);
        Assert.Null(store.Saved!.Tracker.Board);
        Assert.Empty(tracker.Made);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task WithoutQuestionsANewBoardIsAnErrorWhereNothingIsVerified(bool reachable)
    {
        machine.TrackerReachable = reachable;
        machine.Origin = reachable ? null : machine.Origin;
        boards.Read = new SetupBoards([], [new("denis", false)]);

        var result = await RunUnaskedAsync(new SetupProposals(new BoardProposal.Created("Rocket", null)));

        Assert.Equal(SetupOutcome.Failed, result.Outcome);
        Assert.Equal("Project: A new project cannot be made where nothing is verified.", result.Failure);
        Assert.Null(store.Saved);
        Assert.Empty(tracker.Made);
    }

    [Fact]
    public async Task WhatTakesAMomentIsSaid()
    {
        await RunAsync();

        Assert.Equal(["Reading your projects from GitHub", "Reading the labels"], dialog.Waited);
    }

    [Fact]
    public async Task GoingBackWhileTheProjectsAreReadLeavesTheSetup()
    {
        dialog.Unwaited.Add("Reading your projects from GitHub");

        var result = await RunAsync();

        Assert.Equal(SetupOutcome.Left, result.Outcome);
        Assert.Empty(dialog.Asked);
        Assert.Null(store.Saved);
    }

    [Fact]
    public async Task GoingBackWhileTheLabelsAreReadGoesToTheStepBefore()
    {
        dialog.Unwaited.Add("Reading the labels");

        await RunAsync();

        // The labels are asked for again when the step comes back.
        Assert.Equal(["Reading your projects from GitHub", "Reading the labels", "Reading the labels"], dialog.Waited);
        Assert.Equal([0, 0, 1, 0, 1], dialog.Places.Take(5));
        Assert.NotNull(store.Saved);
    }

    [Fact]
    public async Task TheOptionsAreTheProposedAnswersOfTheQuestions()
    {
        boards.Read = new SetupBoards([new(Moon, "Moon", false)], []);

        var result = await RunAsync(new SetupProposals(new BoardProposal.Existing($"{Moon}/views/2"), ["Draft"], ["bug", "feature"], "manual", "paused"));

        // Nothing is answered here: the dialog accepts what every question proposes.
        Assert.Equal(SetupOutcome.Written, result.Outcome);
        Assert.Equal(new SetupBoard(Moon, "Moon", false), dialog.Question!.Current);
        Assert.Equal(new TrackerSettings("github", Moon), store.Saved!.Tracker);
        Assert.Equal(["draft"], store.Saved.Queue.Labels.Blocking);
        Assert.Equal(["bug", "feature"], store.Saved.Queue.Labels.Take);
        Assert.Equal("manual", store.Saved.Queue.Labels.Manual);
        Assert.Equal("paused", store.Saved.Queue.Labels.Interrupted);
        Assert.Equal([("acme/rocket", "paused")], tracker.Labelled);
    }

    [Fact]
    public async Task AnOptionIsProposedBeforeTheSettingsThatExist()
    {
        store.Current = Existing;
        boards.Read = new SetupBoards([new(Rocket, "Rocket", true)], []);

        await RunAsync(new SetupProposals(new BoardProposal.None(), [], ["feature"], "draft"));

        Assert.Null(dialog.Question!.Current);
        Assert.Equal(new BoardAnswer.None(), dialog.Question.Proposed);
        Assert.Null(store.Saved!.Tracker.Board);
        Assert.Empty(store.Saved.Queue.Labels.Blocking);
        Assert.Equal(["feature"], store.Saved.Queue.Labels.Take);
        Assert.Equal("draft", store.Saved.Queue.Labels.Manual);

        // No option names the interrupt label: the one of the settings stays.
        Assert.Equal("interrupted", store.Saved.Queue.Labels.Interrupted);
    }

    [Fact]
    public async Task ANewBoardOfAnOptionIsProposedForItsOwnerOrForTheOwnerOfTheRepository()
    {
        boards.Read = new SetupBoards([], [new("denis", false), new("acme", true)]);

        await RunAsync(new SetupProposals(new BoardProposal.Created(" Rocket ", null)));
        Assert.Equal(new BoardAnswer.Created(new SetupOwner("acme", true), "Rocket"), dialog.Question!.Proposed);
        Assert.Equal([(new SetupOwner("acme", true), "Rocket")], tracker.Made);

        var again = new ScriptedDialog();
        await new ProjectSetup(again, machine, tracker, boards, store, new SetupProposals(new BoardProposal.Created("Moon", "Denis"))).RunAsync(TestContext.Current.CancellationToken);
        Assert.Equal(new BoardAnswer.Created(new SetupOwner("denis", false), "Moon"), again.Question!.Proposed);
    }

    [Fact]
    public async Task AnAnswerThatNamesNoBoardIsProposedWhenTheQuestionIsAskedAgain()
    {
        dialog.Board(new BoardAnswer.None());
        dialog.Back("Blocking labels");

        await RunAsync();

        Assert.Equal(new BoardAnswer.None(), dialog.Question!.Proposed);
    }

    [Fact]
    public async Task AnOptionThatCannotBeTakenIsSaidAndTheQuestionIsAskedWithoutIt()
    {
        var result = await RunAsync(new SetupProposals(new BoardProposal.Existing("rocket"), ["manual", "urgent"], ["nice"]));

        Assert.Equal(SetupOutcome.Written, result.Outcome);
        Assert.Equal(
            ["Project", "Blocking labels", "Labels to take"],
            dialog.Shown.Notes.Where(note => note.Tone == SetupTone.Warning).Select(note => note.Text.Split(':')[0]));
        Assert.Null(store.Saved!.Tracker.Board);
        Assert.Equal(["manual"], store.Saved.Queue.Labels.Blocking);
        Assert.Empty(store.Saved.Queue.Labels.Take);
    }

    [Fact]
    public async Task WithoutQuestionsAFirstSetupTakesWhatIsProposedAndSaysWhatItDid()
    {
        tracker.Labels = ["bug", "draft", "feature", "interrupted"];
        boards.Read = new SetupBoards([new(Moon, "Moon", false), new(Rocket, "Rocket", true)], []);

        var result = await RunUnaskedAsync(SetupProposals.None);

        Assert.Equal(SetupOutcome.Written, result.Outcome);
        Assert.True(result.SettingsWritten);
        Assert.Equal(Rocket, store.Saved!.Tracker.Board);
        Assert.Empty(store.Saved.Queue.Labels.Blocking);
        Assert.Empty(store.Saved.Queue.Labels.Take);
        Assert.Equal("manual", store.Saved.Queue.Labels.Manual);
        Assert.Equal("interrupted", store.Saved.Queue.Labels.Interrupted);
        Assert.Equal(8, result.Answers.Count);
        Assert.Equal(["The label “manual” was made in acme/rocket."], result.Done);
        Assert.Empty(result.Notes);
    }

    [Fact]
    public async Task WithoutQuestionsEveryOptionIsTheAnswer()
    {
        var result = await RunUnaskedAsync(new SetupProposals(new BoardProposal.Existing(Moon), ["manual", "draft"], ["bug"], "manual", "paused"));

        Assert.Equal(SetupOutcome.Written, result.Outcome);
        Assert.Equal(new TrackerSettings("github", Moon), store.Saved!.Tracker);

        // The labels are written in the order the repository lists them, as a choice among them is.
        Assert.Equal(["draft", "manual"], store.Saved.Queue.Labels.Blocking);
        Assert.Equal(["bug"], store.Saved.Queue.Labels.Take);
        Assert.Equal("manual", store.Saved.Queue.Labels.Manual);
        Assert.Equal("paused", store.Saved.Queue.Labels.Interrupted);
        Assert.Equal(["The project was linked to acme/rocket.", "The label “paused” was made in acme/rocket."], result.Done);
        Assert.Equal([(Moon, "acme/rocket")], tracker.Linked);
    }

    [Fact]
    public async Task WithoutQuestionsANewBoardIsMadeLinkedAndWritten()
    {
        tracker.Labels = ["bug", "draft", "feature", "interrupted"];
        boards.Read = new SetupBoards([], [new("acme", true)]);

        var result = await RunUnaskedAsync(new SetupProposals(new BoardProposal.Created("Rocket", null)));

        Assert.Equal(SetupOutcome.Written, result.Outcome);
        Assert.Equal("https://github.com/orgs/acme/projects/42", store.Saved!.Tracker.Board);
        Assert.Equal(
            [
                "The project “Rocket” was made for acme: https://github.com/orgs/acme/projects/42",
                "The project was linked to acme/rocket.",
                "The label “manual” was made in acme/rocket.",
            ],
            result.Done);
    }

    [Fact]
    public async Task WithoutQuestionsAnExistingSetupIsLeftAsItIsAndAnOptionChangesIt()
    {
        store.Current = Existing;
        boards.Read = new SetupBoards([new(Rocket, "Rocket", true)], []);

        Assert.Equal(SetupOutcome.NothingToChange, (await RunUnaskedAsync(SetupProposals.None)).Outcome);
        Assert.Null(store.Saved);

        var result = await RunUnaskedAsync(new SetupProposals(new BoardProposal.None(), Take: []));

        Assert.Equal(SetupOutcome.Written, result.Outcome);
        Assert.Null(store.Saved!.Tracker.Board);
        Assert.Equal(["manual"], store.Saved.Queue.Labels.Blocking);
        Assert.Empty(store.Saved.Queue.Labels.Take);
        Assert.Equal("manual", store.Saved.Queue.Labels.Manual);
        Assert.Empty(result.Done);
    }

    [Fact]
    public async Task WithoutQuestionsAnExistingSetupWithoutABoardStaysWithoutOne()
    {
        store.Current = Existing with { Tracker = new TrackerSettings("github", null) };
        boards.Read = new SetupBoards([new(Rocket, "Rocket", true)], []);

        var result = await RunUnaskedAsync(SetupProposals.None);

        Assert.Equal(SetupOutcome.NothingToChange, result.Outcome);
        Assert.Contains(new SetupAnswer("Project", "none"), result.Answers);
        Assert.Null(store.Saved);
        Assert.Empty(tracker.Linked);

        // An option still names a board.
        result = await RunUnaskedAsync(new SetupProposals(new BoardProposal.Existing(Rocket)));

        Assert.Equal(SetupOutcome.Written, result.Outcome);
        Assert.Equal(Rocket, store.Saved!.Tracker.Board);
    }

    [Theory]
    [InlineData("board", "Project: https://github.com/orgs/acme/projects/1 cannot be read")]
    [InlineData("address", "Project: acme/rocket is not a project. Use https://github.com/orgs/<org>/projects/<number>.")]
    [InlineData("title", "Project: A project needs a title.")]
    [InlineData("whose", "Project: A project cannot be made for ann, only for acme, denis.")]
    [InlineData("blocking", "Blocking labels: acme/rocket has no label “urgent”.")]
    [InlineData("take", "Labels to take: acme/rocket has no label “nice”.")]
    [InlineData("shared", "Labels to take: bug is a blocking label.")]
    [InlineData("manual", "Manual label: bug is a label to take.")]
    [InlineData("unnamed", "Manual label: A label needs a name.")]
    [InlineData("interrupt", "Interrupt label: manual is the manual label.")]
    public async Task WithoutQuestionsAnOptionThatFailsItsCheckFailsTheSetupAndNothingIsChanged(string wrong, string failure)
    {
        boards.Read = new SetupBoards([], [new("denis", false), new("acme", true)]);
        tracker.Unreadable.Add(Rocket);
        var proposals = wrong switch
        {
            "board" => new SetupProposals(new BoardProposal.Existing(Rocket)),
            "address" => new SetupProposals(new BoardProposal.Existing("acme/rocket")),
            "title" => new SetupProposals(new BoardProposal.Created(" ", null)),
            "whose" => new SetupProposals(new BoardProposal.Created("Rocket", "ann")),
            "blocking" => new SetupProposals(Blocking: ["manual", "urgent"]),
            "take" => new SetupProposals(Take: ["nice"]),
            "shared" => new SetupProposals(Blocking: ["bug"], Take: ["bug"]),
            "manual" => new SetupProposals(Take: ["bug"], Manual: "bug"),
            "unnamed" => new SetupProposals(Manual: " "),
            _ => new SetupProposals(Manual: "manual", Interrupted: "manual"),
        };

        var result = await RunUnaskedAsync(proposals);

        Assert.Equal(SetupOutcome.Failed, result.Outcome);
        Assert.Equal(failure, result.Failure);
        Assert.Null(store.Saved);
        Assert.Empty(tracker.Made);
        Assert.Empty(tracker.Linked);
        Assert.Empty(tracker.Labelled);
    }

    [Fact]
    public async Task WithoutQuestionsANewBoardNeedsSomebodyToMakeItFor()
    {
        var result = await RunUnaskedAsync(new SetupProposals(new BoardProposal.Created("Rocket", null)));

        Assert.Equal(SetupOutcome.Failed, result.Outcome);
        Assert.Equal("Project: A new project cannot be made: GitHub named nobody it can be made for.", result.Failure);
        Assert.Null(store.Saved);
    }

    [Fact]
    public async Task WithoutQuestionsANewBoardIsNotMadeWhereGitHubDidNotAnswerForWhoseItCanBe()
    {
        boards.Read = SetupBoards.NoAnswer;

        var result = await RunUnaskedAsync(new SetupProposals(new BoardProposal.Created("Rocket", null)));

        Assert.Equal(SetupOutcome.Failed, result.Outcome);
        Assert.Equal("Project: A new project cannot be made: GitHub did not answer, so nobody is known it can be made for.", result.Failure);
        Assert.Null(store.Saved);
    }

    [Fact]
    public async Task WithoutQuestionsAFailureKeepsTheAnswersBeforeIt()
    {
        var result = await RunUnaskedAsync(new SetupProposals(new BoardProposal.None(), ["manual"], ["nice"]));

        Assert.Equal(
            [new SetupAnswer("Tracker", "GitHub"), new SetupAnswer("Project", "none"), new SetupAnswer("Blocking labels", "manual")],
            result.Answers);
    }

    [Fact]
    public async Task WithoutQuestionsALabelOfTheSettingsThatBecomesBlockingFailsTheSetup()
    {
        store.Current = Existing;

        var result = await RunUnaskedAsync(new SetupProposals(Blocking: ["bug"]));

        Assert.Equal(SetupOutcome.Failed, result.Outcome);
        Assert.Equal("Labels to take: bug is a blocking label.", result.Failure);
        Assert.Null(store.Saved);
    }

    [Fact]
    public async Task WithoutQuestionsSettingsThatDoNotValidateAreNotReplaced()
    {
        store.Errors = [new SettingsError("tracker.type", 5, "is missing")];

        var result = await RunUnaskedAsync(SetupProposals.None);

        Assert.Equal(SetupOutcome.Failed, result.Outcome);
        Assert.Equal("The settings that exist do not validate.", result.Failure);
        Assert.Contains(new SetupNote(SetupTone.Failure, ".toobusy/settings.toml:5: tracker.type: is missing"), result.Notes);
        Assert.Null(store.Saved);
        Assert.Equal(0, tracker.Calls);
    }

    [Fact]
    public async Task WithoutQuestionsWhatGitHubRefusesFailsTheSetupAfterWhatWasDone()
    {
        tracker.Labels = ["bug", "draft", "feature", "interrupted"];
        boards.Read = new SetupBoards([new(Moon, "Moon", false)], []);
        tracker.RefuseToLabel = "labels cannot be made";

        var result = await RunUnaskedAsync(new SetupProposals(new BoardProposal.Existing(Moon)));

        Assert.Equal(SetupOutcome.Failed, result.Outcome);
        Assert.Equal("labels cannot be made", result.Failure);
        Assert.Equal(["The project was linked to acme/rocket."], result.Done);
        Assert.Null(store.Saved);
    }

    [Fact]
    public async Task WithoutQuestionsAndWithoutTheTrackerTheOptionsAreTakenAsTheyAreAndThatIsSaid()
    {
        machine.TrackerReachable = false;
        machine.Problems = [new SetupProblem("gh is not installed", "brew install gh")];

        var result = await RunUnaskedAsync(new SetupProposals(new BoardProposal.Existing(Rocket), ["manual", "urgent"], ["nice"], "waiting"));

        Assert.Equal(SetupOutcome.Written, result.Outcome);
        Assert.Equal(Rocket, store.Saved!.Tracker.Board);
        Assert.Equal(["manual", "urgent"], store.Saved.Queue.Labels.Blocking);
        Assert.Equal(["nice"], store.Saved.Queue.Labels.Take);
        Assert.Equal("waiting", store.Saved.Queue.Labels.Manual);
        Assert.Equal(0, tracker.Calls);
        Assert.Empty(result.Done);
        Assert.Equal([SetupTone.Failure, SetupTone.Muted, SetupTone.Warning], result.Notes.Select(note => note.Tone));
        Assert.Contains("nothing is verified", result.Notes[2].Text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task WithoutQuestionsAndWithoutTheTrackerATypedLabelIsStillCheckedAgainstTheOthers()
    {
        machine.TrackerReachable = false;

        var result = await RunUnaskedAsync(new SetupProposals(Blocking: ["manual"], Take: ["manual"]));

        Assert.Equal(SetupOutcome.Failed, result.Outcome);
        Assert.Equal("Labels to take: manual is a blocking label.", result.Failure);
        Assert.Null(store.Saved);
    }

    [Fact]
    public async Task TheSkillIsTakenWithTheSlashOfItsCommandOrWithoutIt()
    {
        dialog.Answer("Skill", " /night-shift ");

        var result = await RunAsync();

        Assert.Equal("night-shift", store.Saved!.Skill);
        Assert.Contains(new SetupAnswer("Skill", "night-shift"), result.Answers);
    }

    [Fact]
    public async Task AnEmptyAnswerIsAProjectWithoutASkill()
    {
        dialog.Answer("Skill", "  ");

        var result = await RunAsync();

        Assert.Null(store.Saved!.Skill);
        Assert.Contains(new SetupAnswer("Skill", "none"), result.Answers);
        Assert.Empty(dialog.Refused);
    }

    [Fact]
    public async Task ATextThatIsNotTheNameOfASkillIsAskedForAgain()
    {
        dialog.Answer("Skill", "task auto");
        dialog.Answer("Skill", "task-auto");

        await RunAsync();

        Assert.Equal(["task auto"], dialog.Refused);
        Assert.Equal("task-auto", store.Saved!.Skill);
    }

    [Fact]
    public async Task SettingsThatNameASkillProposeIt()
    {
        boards.Read = new SetupBoards([new(Rocket, "Rocket", true)], []);
        store.Current = Existing with { Skill = "night-shift" };
        machine.Skills = ["night-shift"];
        var result = await RunAsync();

        Assert.Equal(SetupOutcome.NothingToChange, result.Outcome);
        Assert.Contains(new SetupAnswer("Skill", "night-shift"), result.Answers);
    }

    [Fact]
    public async Task TheCommandsTheBranchAndThePermissionModeAreNotAskedForAndAreKept()
    {
        var commands = new TaskCommands("scripts/dev.sh ports free", "scripts/dev.sh server stop");
        store.Current = Existing with { Commands = commands, Branch = "develop", Assistant = new AssistantSettings("claude-code") { PermissionMode = "dontAsk" } };
        dialog.Answer("Skill", "task-auto");
        machine.Skills = ["task-auto"];

        var result = await RunAsync();

        Assert.Equal(SetupOutcome.Written, result.Outcome);
        Assert.Equal(commands, store.Saved!.Commands);
        Assert.Equal("develop", store.Saved.Branch);
        Assert.Equal("dontAsk", store.Saved.Assistant.PermissionMode);
        Assert.Empty(result.Unasked);
        Assert.DoesNotContain(result.Answers, answer => answer.Value.Contains("dev.sh", StringComparison.Ordinal));
        Assert.DoesNotContain(dialog.Asked, label => label.Contains("ranch", StringComparison.Ordinal) || label.Contains("ermission", StringComparison.Ordinal));
    }

    [Fact]
    public async Task AFirstSetupHasNoCommandsUnlessTheOptionsNameThem()
    {
        var without = await RunAsync();
        Assert.Equal(TaskCommands.None, store.Saved!.Commands);
        Assert.Empty(without.Unasked);

        var result = await RunAsync(new SetupProposals(Before: new("scripts/dev.sh ports free"), After: new(" scripts/dev.sh server stop ")));

        Assert.Equal(new TaskCommands("scripts/dev.sh ports free", "scripts/dev.sh server stop"), store.Saved!.Commands);
        Assert.Equal([new SetupAnswer("Before a task", "none → scripts/dev.sh ports free"), new SetupAnswer("After a task", "none → scripts/dev.sh server stop")], result.Unasked);

        // No question asks for them: they stand on the last page, under what is said of the answers.
        Assert.DoesNotContain(dialog.Asked, label => label.Contains("task", StringComparison.Ordinal));
        Assert.Equal(
            [
                new SetupNote(SetupTone.Plain, "The answers above will be written to .toobusy/settings.toml."),
                new SetupNote(SetupTone.Change, "Before a task: none → scripts/dev.sh ports free"),
                new SetupNote(SetupTone.Change, "After a task: none → scripts/dev.sh server stop"),
            ],
            dialog.Shown.Notes.Take(3));
        Assert.DoesNotContain(result.Answers, answer => answer.Value.Contains("dev.sh", StringComparison.Ordinal));
    }

    [Fact]
    public async Task TheOptionsSetChangeAndTakeAwayTheCommandsOfSettingsThatExist()
    {
        boards.Read = new SetupBoards([new(Rocket, "Rocket", true)], []);
        store.Current = Existing;

        // Set: nothing else changes, and the settings are written for the command alone.
        var set = await RunAsync(new SetupProposals(Before: new("make free")));

        Assert.Equal(SetupOutcome.Written, set.Outcome);
        Assert.Equal(new TaskCommands("make free", null), store.Saved!.Commands);
        Assert.Equal(["Before a task: none → make free"], dialog.Shown.Notes.Where(note => note.Tone == SetupTone.Change).Select(note => note.Text));
        Assert.Equal([new SetupAnswer("Before a task", "none → make free")], set.Unasked);

        // Changed and taken away, each as it was and as it will be; the one no option speaks of is kept.
        store.Current = Existing with { Commands = new("make free", "make stop") };
        var changed = await RunAsync(new SetupProposals(Before: new("make ports"), After: new(null)));

        Assert.Equal(new TaskCommands("make ports", null), store.Saved!.Commands);
        Assert.Equal(
            [new SetupNote(SetupTone.Change, "Before a task: make free → make ports"), new SetupNote(SetupTone.Change, "After a task: make stop → none")],
            dialog.Shown.Notes.Skip(1));
        Assert.Equal([new SetupAnswer("Before a task", "make free → make ports"), new SetupAnswer("After a task", "make stop → none")], changed.Unasked);

        var kept = await RunAsync(new SetupProposals(After: new(null)));

        Assert.Equal(new TaskCommands("make free", null), store.Saved!.Commands);
        Assert.Equal([new SetupAnswer("After a task", "make stop → none")], kept.Unasked);
    }

    [Fact]
    public async Task OptionsThatSayWhatTheSettingsSayOfTheCommandsChangeNothing()
    {
        boards.Read = new SetupBoards([new(Rocket, "Rocket", true)], []);
        store.Current = Existing with { Commands = new("make free", null) };

        var result = await RunUnaskedAsync(new SetupProposals(Before: new("make free"), After: new(null)));

        Assert.Equal(SetupOutcome.NothingToChange, result.Outcome);
        Assert.Empty(result.Unasked);
        Assert.Null(store.Saved);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public async Task WithoutQuestionsAnEmptyCommandFailsBeforeAnythingIsChanged(string empty)
    {
        var before = await RunUnaskedAsync(new SetupProposals(Before: new(empty)));

        Assert.Equal(SetupOutcome.Failed, before.Outcome);
        Assert.Equal("--before: A command must not be empty: give --no-before to run none.", before.Failure);
        Assert.Empty(before.Unasked);

        var after = await RunUnaskedAsync(new SetupProposals(After: new(empty)));

        Assert.Equal("--after: A command must not be empty: give --no-after to run none.", after.Failure);
        Assert.Null(store.Saved);
        Assert.Empty(tracker.Labelled);
    }

    [Fact]
    public async Task OnTheScreenAnEmptyCommandIsSaidAndTheCommandStaysAsItIs()
    {
        boards.Read = new SetupBoards([new(Rocket, "Rocket", true)], []);
        store.Current = Existing with { Commands = new("make free", null) };

        var result = await RunAsync(new SetupProposals(Before: new(" ")));

        Assert.Equal(SetupOutcome.NothingToChange, result.Outcome);
        Assert.Contains(new SetupNote(SetupTone.Warning, "--before: A command must not be empty: give --no-before to run none."), result.Notes);
    }

    [Fact]
    public async Task TheOptionsNameTheBranchChangeItAndTakeItAway()
    {
        var first = await RunAsync(new SetupProposals { Branch = new(" develop ") });

        Assert.Equal("develop", store.Saved!.Branch);
        Assert.Equal([new SetupAnswer("Branch", "none → develop")], first.Unasked);
        Assert.Contains(new SetupNote(SetupTone.Change, "Branch: none → develop"), dialog.Shown.Notes);

        boards.Read = new SetupBoards([new(Rocket, "Rocket", true)], []);
        store.Current = Existing with { Branch = "develop" };
        var changed = await RunAsync(new SetupProposals(Before: new("make free")) { Branch = new("main") });

        Assert.Equal("main", store.Saved!.Branch);
        Assert.Equal([new SetupAnswer("Before a task", "none → make free"), new SetupAnswer("Branch", "develop → main")], changed.Unasked);
        Assert.Equal(
            [new SetupNote(SetupTone.Change, "Before a task: none → make free"), new SetupNote(SetupTone.Change, "Branch: develop → main")],
            dialog.Shown.Notes.Skip(1));

        var gone = await RunUnaskedAsync(new SetupProposals { Branch = new(null) });

        Assert.Equal(SetupOutcome.Written, gone.Outcome);
        Assert.Null(store.Saved!.Branch);
        Assert.Equal([new SetupAnswer("Branch", "develop → none")], gone.Unasked);

        var same = await RunUnaskedAsync(new SetupProposals { Branch = new("develop") });

        Assert.Equal(SetupOutcome.NothingToChange, same.Outcome);
        Assert.Empty(same.Unasked);
    }

    [Theory]
    [InlineData("  ", "--branch: A branch must not be empty: give --no-branch to name none.")]
    [InlineData("my branch", "--branch: “my branch” must be the name of a branch as git takes it, as `develop`: no space, none of `~^:?*[\\`, no `..` and no `@{`, and neither `/` nor `.` at either end.")]
    public async Task ABranchThatGitDoesNotTakeFailsWithoutQuestionsAndIsSaidOnTheScreen(string name, string said)
    {
        var unasked = await RunUnaskedAsync(new SetupProposals { Branch = new(name) });

        Assert.Equal(SetupOutcome.Failed, unasked.Outcome);
        Assert.Equal(said, unasked.Failure);
        Assert.Null(store.Saved);

        boards.Read = new SetupBoards([new(Rocket, "Rocket", true)], []);
        store.Current = Existing with { Branch = "develop" };
        var asked = await RunAsync(new SetupProposals { Branch = new(name) });

        Assert.Equal(SetupOutcome.NothingToChange, asked.Outcome);
        Assert.Contains(new SetupNote(SetupTone.Warning, said), asked.Notes);
    }

    // A mode that is not named is the one a session is started in then, so it is not said to be none.
    [Fact]
    public async Task TheOptionNamesThePermissionModeAsClaudeCodeWritesIt()
    {
        var first = await RunAsync(new SetupProposals { PermissionMode = " DONTASK " });

        Assert.Equal("dontAsk", store.Saved!.Assistant.PermissionMode);
        Assert.Equal([new SetupAnswer("Permission mode", "auto by default → dontAsk")], first.Unasked);
        Assert.Contains(new SetupNote(SetupTone.Change, "Permission mode: auto by default → dontAsk"), dialog.Shown.Notes);

        boards.Read = new SetupBoards([new(Rocket, "Rocket", true)], []);
        store.Current = Existing with { Assistant = new AssistantSettings("claude-code") { PermissionMode = "dontAsk" } };
        var changed = await RunUnaskedAsync(new SetupProposals { PermissionMode = "auto" });

        Assert.Equal(SetupOutcome.Written, changed.Outcome);
        Assert.Equal("auto", store.Saved!.Assistant.PermissionMode);
        Assert.Equal([new SetupAnswer("Permission mode", "dontAsk → auto")], changed.Unasked);

        var same = await RunUnaskedAsync(new SetupProposals { PermissionMode = "dontAsk" });

        Assert.Equal(SetupOutcome.NothingToChange, same.Outcome);
        Assert.Empty(same.Unasked);
    }

    [Theory]
    [InlineData("plan", "--permission-mode: “plan” must be `auto` or `dontAsk`: in `plan` a session waits for an answer, and nobody answers a session of a run.")]
    public async Task AModeASessionCannotBeStartedInFailsWithoutQuestionsAndIsSaidOnTheScreen(string mode, string said)
    {
        var unasked = await RunUnaskedAsync(new SetupProposals { PermissionMode = mode });

        Assert.Equal(SetupOutcome.Failed, unasked.Outcome);
        Assert.Equal(said, unasked.Failure);
        Assert.Null(store.Saved);

        boards.Read = new SetupBoards([new(Rocket, "Rocket", true)], []);
        store.Current = Existing with { Assistant = new AssistantSettings("claude-code") { PermissionMode = "dontAsk" } };
        var asked = await RunAsync(new SetupProposals { PermissionMode = mode });

        Assert.Equal(SetupOutcome.NothingToChange, asked.Outcome);
        Assert.Contains(new SetupNote(SetupTone.Warning, said), asked.Notes);
    }

    [Fact]
    public async Task ASkillThatIsNotWrittenYetIsSaidOnTheLastPageAndInTheResult()
    {
        machine.Skills = [];

        var result = await RunAsync();

        SetupNote[] said =
        [
            new(SetupTone.Warning, "There is no skill “task-auto” yet: write .claude/skills/task-auto/SKILL.md before a run."),
            new(SetupTone.Muted, "`toobusy about` tells what the skill is to say."),
        ];
        Assert.Equal(said, dialog.Shown.Notes.TakeLast(2));
        Assert.Equal(said, result.Notes);
        Assert.Equal(SetupOutcome.Written, result.Outcome);
        Assert.Equal("task-auto", store.Saved!.Skill);
    }

    [Fact]
    public async Task ABoardThatIsNotNamedAsGitHubNamesOneIsAskedWhichOfItsStatusesIsWhich()
    {
        tracker.Statuses = ["Backlog", "Ready", "Doing", "Review", "Shipped"];
        dialog.Board(new BoardAnswer.Existing(Moon));
        dialog.Answer(TodoStatus, "Ready");
        dialog.Answer(InProgressStatus, "Doing");
        dialog.Answer(DoneStatus, "Shipped");

        var result = await RunAsync();

        Assert.Equal(SetupOutcome.Written, result.Outcome);
        Assert.Equal(new TrackerSettings("github", Moon) { Statuses = new BoardStatuses("Ready", "Doing", "Shipped") }, store.Saved!.Tracker);
        Assert.Equal(["Project", TodoStatus, InProgressStatus, DoneStatus, "Blocking labels"], dialog.Asked.Take(5));

        // A status that is one of the three already is not among the choices of the next.
        Assert.Equal(["Backlog", "Ready", "Doing", "Review", "Shipped"], dialog.Offered[TodoStatus]);
        Assert.Equal(["Backlog", "Doing", "Review", "Shipped"], dialog.Offered[InProgressStatus]);
        Assert.Equal(["Backlog", "Review", "Shipped"], dialog.Offered[DoneStatus]);
        Assert.Contains(new SetupAnswer("Statuses", "Ready, Doing, Shipped"), result.Answers);
        Assert.Empty(result.Notes);
        Assert.Equal([Moon], tracker.StatusesOf);
    }

    [Fact]
    public async Task WhileTheStatusesAreAskedForTheBoardIsAmongTheAnswers()
    {
        tracker.Statuses = ["Ready", "Doing", "Shipped"];
        dialog.Board(new BoardAnswer.Existing(Moon));

        await RunAsync();

        var first = dialog.Seen.First(progress => progress.Answers.Count == 2);
        Assert.Equal(0, first.Step);
        Assert.Equal(new SetupAnswer("Project", Moon), first.Answers[1]);

        // Going back from a status stays in the setup, so the screen is told that it is not the first question.
        Assert.True(first.Later);
        Assert.False(dialog.Seen[0].Later);
    }

    [Fact]
    public async Task AStatusThatTheBoardDoesNotNameAsGitHubDoesIsProposedByItsPlace()
    {
        tracker.Statuses = ["Ready", "In progress", "Review", "Shipped"];
        dialog.Board(new BoardAnswer.Existing(Moon));

        await RunAsync();

        // The first that is left for a task to do, the name of GitHub where the board has it, and the last one
        // for a task that is done.
        Assert.Equal(new BoardStatuses("Ready", "In progress", "Shipped"), store.Saved!.Tracker.Statuses);
    }

    [Fact]
    public async Task GoingBackFromAStatusComesBackToTheOneBeforeAndFromTheFirstToTheBoard()
    {
        tracker.Statuses = ["Ready", "Doing", "Shipped"];
        dialog.Board(new BoardAnswer.Existing(Moon));
        dialog.Answer(TodoStatus, "Doing");
        dialog.Back(InProgressStatus);
        dialog.Back(TodoStatus);
        dialog.Board(new BoardAnswer.Existing(Moon));
        dialog.Answer(TodoStatus, "Ready");

        await RunAsync();

        Assert.Equal(["Project", TodoStatus, InProgressStatus, TodoStatus, "Project", TodoStatus, InProgressStatus, DoneStatus], dialog.Asked.Take(8));
        Assert.Equal(new BoardStatuses("Ready", "Doing", "Shipped"), store.Saved!.Tracker.Statuses);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task GoingBackFromTheStatusesLeavesWhatTheyProposeAsItWas(bool verified)
    {
        machine.TrackerReachable = verified;
        store.Current = Existing with { Tracker = Existing.Tracker with { Statuses = new BoardStatuses("Ready", "Doing", "Shipped") } };
        boards.Read = new SetupBoards([new(Rocket, "Rocket", true)], []);
        tracker.Statuses = ["Backlog", "Ready", "Doing", "Shipped"];
        dialog.Back(TodoStatus);

        var result = await RunAsync();

        // The board that is chosen again proposes the statuses of the settings again, so accepting them changes nothing.
        Assert.Equal(["Project", TodoStatus, "Project", TodoStatus, InProgressStatus, DoneStatus], dialog.Asked.Take(6));
        Assert.Equal(SetupOutcome.NothingToChange, result.Outcome);
    }

    [Fact]
    public async Task ABoardThatWasAskedAboutIsAskedAboutAgainWithWhatWasAnswered()
    {
        tracker.Statuses = ["Todo", "In Progress", "Done", "Shipped"];
        dialog.Board(new BoardAnswer.Existing(Moon));
        dialog.Back("Blocking labels");

        await RunAsync(new SetupProposals { DoneStatus = "shipped" });

        Assert.Equal(["Project", TodoStatus, InProgressStatus, DoneStatus, "Blocking labels", "Project", TodoStatus, InProgressStatus, DoneStatus, "Blocking labels"], dialog.Asked.Take(10));
        Assert.Equal(new BoardStatuses("Todo", "In Progress", "Shipped"), store.Saved!.Tracker.Statuses);
    }

    [Fact]
    public async Task TheStatusesOfTheSettingsAreProposedAndAcceptingThemChangesNothing()
    {
        store.Current = Existing with { Tracker = Existing.Tracker with { Statuses = new BoardStatuses("Ready", "Doing", "Shipped") } };
        boards.Read = new SetupBoards([new(Rocket, "Rocket", true)], []);
        tracker.Statuses = ["Backlog", "Ready", "Doing", "Shipped"];

        var result = await RunAsync();

        Assert.Equal(SetupOutcome.NothingToChange, result.Outcome);
        Assert.Equal(["Project", TodoStatus, InProgressStatus, DoneStatus], dialog.Asked.Take(4));
    }

    [Fact]
    public async Task AChangeOfTheStatusesIsALineOfTheLastPage()
    {
        store.Current = Existing;
        tracker.Statuses = ["Ready", "Doing", "Shipped"];

        await RunAsync();

        Assert.Contains(new SetupNote(SetupTone.Change, "Statuses: Todo, In Progress, Done → Ready, Doing, Shipped"), dialog.Shown.Notes);
        Assert.Equal(new BoardStatuses("Ready", "Doing", "Shipped"), store.Saved!.Tracker.Statuses);
    }

    [Fact]
    public async Task ABoardWithFewerThanThreeStatusesIsTakenAndSaidOnTheLastPageAndInTheResult()
    {
        tracker.Statuses = ["Ready", "Done"];
        dialog.Board(new BoardAnswer.Existing(Moon));

        var result = await RunAsync();

        SetupNote[] said =
        [
            new(SetupTone.Warning, "The board has no statuses “Todo”, “In Progress”: it has Ready, Done."),
            new(SetupTone.Muted, "A run needs three statuses of a board: add them on GitHub and run `toobusy init` again. `toobusy doctor` fails until then."),
        ];
        Assert.Equal(said, dialog.Shown.Notes.TakeLast(2));
        Assert.Equal(said, result.Notes);
        Assert.Equal(SetupOutcome.Written, result.Outcome);
        Assert.Equal(new TrackerSettings("github", Moon), store.Saved!.Tracker);
        Assert.DoesNotContain(TodoStatus, dialog.Asked);
    }

    [Fact]
    public async Task AnOptionOfAStatusIsProposedAndAStatusTheBoardDoesNotHaveIsSaid()
    {
        tracker.Statuses = ["Todo", "In Progress", "Done", "Shipped"];
        dialog.Board(new BoardAnswer.Existing(Moon));

        var result = await RunAsync(new SetupProposals { TodoStatus = "Ready", DoneStatus = " shipped " });

        Assert.Equal(new SetupNote(SetupTone.Warning, "Todo status: The board has no status “Ready”: it has Todo, In Progress, Done, Shipped."), result.Notes[0]);
        Assert.Equal(new BoardStatuses("Todo", "In Progress", "Shipped"), store.Saved!.Tracker.Statuses);
    }

    [Fact]
    public async Task WhereNothingIsVerifiedTheStatusesAreTyped()
    {
        machine.TrackerReachable = false;
        dialog.Board(new BoardAnswer.Existing(Moon));
        dialog.Answer(TodoStatus, " Ready ");
        dialog.Answer(InProgressStatus, "ready");
        dialog.Answer(InProgressStatus, " ");
        dialog.Answer(InProgressStatus, "Doing");

        await RunAsync();

        Assert.Equal(["ready", " "], dialog.Refused);
        Assert.Equal(new BoardStatuses("Ready", "Doing", "Done"), store.Saved!.Tracker.Statuses);
        Assert.Empty(tracker.StatusesOf);
    }

    [Fact]
    public async Task AProjectWithoutABoardAndANewOneAreAskedNothingOfStatuses()
    {
        boards.Read = new SetupBoards([], [new SetupOwner("acme", true)]);
        dialog.Board(new BoardAnswer.Created(new SetupOwner("acme", true), "Rocket"));

        await RunAsync();

        Assert.DoesNotContain(TodoStatus, dialog.Asked);
        Assert.Equal(BoardStatuses.Default, store.Saved!.Tracker.Statuses);
        Assert.Empty(tracker.StatusesOf);
    }

    [Fact]
    public async Task WithoutQuestionsTheOptionsNameTheStatusesAsTheBoardWritesThem()
    {
        tracker.Statuses = ["Backlog", "Ready", "Doing", "Shipped"];

        var result = await RunUnaskedAsync(new SetupProposals(Board: new BoardProposal.Existing(Moon)) { TodoStatus = "ready", InProgressStatus = "DOING", DoneStatus = "Shipped" });

        Assert.Equal(SetupOutcome.Written, result.Outcome);
        Assert.Equal(new BoardStatuses("Ready", "Doing", "Shipped"), store.Saved!.Tracker.Statuses);
        Assert.Contains(new SetupAnswer("Statuses", "Ready, Doing, Shipped"), result.Answers);
        Assert.Empty(result.Notes);
    }

    [Fact]
    public async Task WithoutQuestionsAStatusThatIsNotNamedStaysAsTheSettingsCallIt()
    {
        store.Current = Existing with { Tracker = Existing.Tracker with { Statuses = new BoardStatuses("Ready", "Doing", "Done") } };
        tracker.Statuses = ["Ready", "Doing", "Done", "Shipped"];

        await RunUnaskedAsync(new SetupProposals { DoneStatus = "Shipped" });

        Assert.Equal(new BoardStatuses("Ready", "Doing", "Shipped"), store.Saved!.Tracker.Statuses);
    }

    [Theory]
    [InlineData("Ready", "Doing", "Closed", "Done status: The board has no status “Closed”: it has Backlog, Ready, Doing, Shipped.")]
    [InlineData("Ready", "ready", "Shipped", "In Progress status: ready is the Todo status too.")]
    [InlineData("Ready", " ", "Shipped", "In Progress status: A status needs a name.")]
    [InlineData("Done", null, null, "Done status: Done is the Todo status too.")]
    public async Task WithoutQuestionsAStatusThatCannotBeFailsBeforeAnythingIsChanged(string? todo, string? progress, string? done, string failure)
    {
        tracker.Statuses = ["Backlog", "Ready", "Doing", "Shipped"];

        var result = await RunUnaskedAsync(new SetupProposals(Board: new BoardProposal.Existing(Moon)) { TodoStatus = todo, InProgressStatus = progress, DoneStatus = done });

        Assert.Equal(SetupOutcome.Failed, result.Outcome);
        Assert.Equal(failure, result.Failure);
        Assert.Null(store.Saved);
    }

    [Fact]
    public async Task WithoutQuestionsAStatusIsNotNamedForAProjectWithoutABoardNorOtherwiseForANewOne()
    {
        var none = await RunUnaskedAsync(new SetupProposals(Board: new BoardProposal.None()) { TodoStatus = "Ready" });
        Assert.Equal("Todo status: A status is named for a project, and there is none.", none.Failure);

        boards.Read = new SetupBoards([], [new SetupOwner("acme", true)]);
        var made = await RunUnaskedAsync(new SetupProposals(Board: new BoardProposal.Created("Rocket", null)) { TodoStatus = "Ready" });
        Assert.Equal("Todo status: A new project has the statuses GitHub gives it: Todo, In Progress, Done.", made.Failure);
        Assert.Empty(tracker.Made);
        Assert.Null(store.Saved);
    }

    [Fact]
    public async Task WithoutQuestionsABoardThatIsNotNamedAsGitHubNamesOneIsTakenAndItIsSaidWhatToName()
    {
        tracker.Statuses = ["Ready", "Doing", "Shipped"];

        var result = await RunUnaskedAsync(new SetupProposals(Board: new BoardProposal.Existing(Moon)));

        Assert.Equal(SetupOutcome.Written, result.Outcome);
        Assert.Equal(new TrackerSettings("github", Moon), store.Saved!.Tracker);
        Assert.Equal([SetupTone.Warning, SetupTone.Muted], result.Notes.Select(note => note.Tone));
        Assert.Contains("“Todo”, “In Progress”, “Done”", result.Notes[0].Text, StringComparison.Ordinal);
        Assert.Equal("Say which of its statuses is which with `--todo-status`, `--in-progress-status` and `--done-status`, or with `toobusy init`: `toobusy doctor` fails until then.", result.Notes[1].Text);
    }

    [Fact]
    public async Task ABoardThatHasTheStatusesOfARunIsNotSpokenOf()
    {
        tracker.Statuses = ["todo", "in progress", "DONE", "Shipped"];
        dialog.Board(new BoardAnswer.Existing(Moon));

        Assert.Empty((await RunAsync()).Notes);
        Assert.Equal([Moon], tracker.StatusesOf);
    }

    [Fact]
    public async Task StatusesThatCannotBeReadAreLeftToTheDoctor()
    {
        tracker.Statuses = null;
        dialog.Board(new BoardAnswer.Existing(Moon));

        var result = await RunAsync();

        Assert.Empty(result.Notes);
        Assert.Equal(Moon, store.Saved!.Tracker.Board);
    }

    [Fact]
    public async Task ABoardThatIsChangedForAnotherIsNotSpokenOfAnyMore()
    {
        tracker.Statuses = [];
        dialog.Board(new BoardAnswer.Existing(Moon));
        dialog.Back("Blocking labels");
        dialog.Board(new BoardAnswer.None());

        Assert.Empty((await RunAsync()).Notes);
    }

    [Fact]
    public async Task ASkillThatCannotBeLookedForIsNotSpokenOf()
    {
        machine.Skills = null;

        Assert.Empty((await RunAsync()).Notes);
    }

    [Fact]
    public async Task GoingBackFromTheSkillAsksTheInterruptLabelAgain()
    {
        dialog.Back("Skill");

        await RunAsync();

        Assert.Equal([InterruptLabel, "Skill", InterruptLabel, "Skill", "Write the settings?"], dialog.Asked.Skip(4));
    }

    [Fact]
    public async Task TheOptionsOfTheSkillAreProposedBeforeWhatTheSettingsSay()
    {
        store.Current = Existing with { Skill = "night-shift" };
        machine.Skills = ["night-shift", "task-auto"];

        await RunAsync(new SetupProposals(Skill: "/task-auto"));
        Assert.Equal("task-auto", store.Saved!.Skill);

        await RunAsync(new SetupProposals(Skill: ""));
        Assert.Null(store.Saved!.Skill);
        Assert.Contains(new SetupNote(SetupTone.Change, "Skill: night-shift → none"), dialog.Shown.Notes);
    }

    [Fact]
    public async Task WithoutQuestionsASkillThatIsNotANameFailsBeforeAnythingIsChanged()
    {
        var result = await RunUnaskedAsync(new SetupProposals(Skill: "task auto"));

        Assert.Equal(SetupOutcome.Failed, result.Outcome);
        Assert.Equal("Skill: task auto is not the name of a skill: it is letters, digits and dashes, as task-auto.", result.Failure);
        Assert.Null(store.Saved);
        Assert.Empty(tracker.Labelled);
    }

    [Fact]
    public async Task TheMenuListsEverySettingWithItsValue()
    {
        store.Current = Existing with { Skill = "task-auto", Branch = "develop" };

        var setup = await OpenAsync();

        Assert.Equal(
            [
                new SetupValue(SetupSetting.Project, "Project", Rocket, SetupLook.Set),
                new SetupValue(SetupSetting.Statuses, "Statuses", "Todo: Todo · In Progress: In Progress · Done: Done", SetupLook.Set),
                new SetupValue(SetupSetting.Blocking, "Blocking labels", "manual", SetupLook.KeepsOut),
                new SetupValue(SetupSetting.Take, "Labels to take", "bug, retired", SetupLook.Set),
                new SetupValue(SetupSetting.Manual, ManualLabel, "manual", SetupLook.Set),
                new SetupValue(SetupSetting.Interrupted, InterruptLabel, "interrupted", SetupLook.Set),
                new SetupValue(SetupSetting.Skill, "Skill", "task-auto", SetupLook.Set),
                new SetupValue(SetupSetting.Before, "Before a task", "none", SetupLook.Unset),
                new SetupValue(SetupSetting.After, "After a task", "none", SetupLook.Unset),
                new SetupValue(SetupSetting.Branch, "Branch", "develop", SetupLook.Set),
                new SetupValue(SetupSetting.PermissionMode, "Permission mode", "auto by default", SetupLook.Set),
            ],
            setup.Values);
        Assert.Empty(dialog.Asked);
        Assert.Null(setup.Changed);
    }

    [Fact]
    public async Task TheMenuSaysThatNothingBlocksAndThatAnyTaskIsTaken()
    {
        store.Current = Existing with { Queue = new QueueSettings(new LabelSettings([], [], "manual", "interrupted")) };

        var values = (await OpenAsync()).Values;

        Assert.Equal(new SetupValue(SetupSetting.Blocking, "Blocking labels", "none", SetupLook.Unset), Value(values, SetupSetting.Blocking));
        Assert.Equal(new SetupValue(SetupSetting.Take, "Labels to take", "any task", SetupLook.Set), Value(values, SetupSetting.Take));
    }

    [Fact]
    public async Task SettingsThatDoNotValidateAreNotOpenedForTheMenu()
    {
        store.Errors = [new SettingsError("version", 1, "must be 1")];

        Assert.False(await new ProjectSetup(dialog, machine, tracker, boards, store).OpenAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task TheMenuSavesTheChangeOfOneSettingWhenTheUserSaysSo()
    {
        store.Current = Kept;
        var setup = await OpenAsync();
        dialog.Answer("Blocking labels", "draft", "feature");

        var result = await setup.ChangeAsync(SetupSetting.Blocking, TestContext.Current.CancellationToken);

        Assert.Equal(SetupOutcome.Written, result.Outcome);
        Assert.True(SameSettings(Kept with { Queue = new QueueSettings(Kept.Queue.Labels with { Blocking = ["draft", "feature"] }) }, store.Saved));
        Assert.Equal(["Blocking labels", "Save the change?"], dialog.Asked);
        Assert.Equal(["Blocking labels: draft → draft, feature"], dialog.Shown.Notes.Where(note => note.Tone == SetupTone.Change).Select(note => note.Text));
        Assert.True(dialog.Shown.Menu);
        Assert.Empty(dialog.Shown.Steps);
        Assert.Empty(dialog.Shown.Answers);
        Assert.Equal([new SetupAnswer("Blocking labels", "draft, feature")], result.Answers);
        Assert.Equal("draft, feature", Value(setup.Values, SetupSetting.Blocking).Value);
        Assert.Empty(tracker.Labelled);
        Assert.Empty(tracker.Linked);
    }

    [Fact]
    public async Task AnAnswerThatChangesNothingAsksNothingMore()
    {
        store.Current = Kept;
        var setup = await OpenAsync();

        var result = await setup.ChangeAsync(SetupSetting.Blocking, TestContext.Current.CancellationToken);

        Assert.Equal(SetupOutcome.NothingToChange, result.Outcome);
        Assert.Equal(["Blocking labels"], dialog.Asked);
        Assert.Null(store.Saved);
        Assert.Null(setup.Changed);
    }

    [Fact]
    public async Task GoingBackFromASettingChangesNothing()
    {
        store.Current = Existing;
        var setup = await OpenAsync();
        dialog.Back("Skill");

        var result = await setup.ChangeAsync(SetupSetting.Skill, TestContext.Current.CancellationToken);

        Assert.Equal(SetupOutcome.Left, result.Outcome);
        Assert.Equal(["Skill"], dialog.Asked);
        Assert.Null(store.Saved);
    }

    [Fact]
    public async Task AChangeThatIsNotSavedIsNotProposedAgain()
    {
        store.Current = Existing;
        var setup = await OpenAsync();
        dialog.Answer("Skill", "night-shift");
        dialog.Confirm(false);

        var declined = await setup.ChangeAsync(SetupSetting.Skill, TestContext.Current.CancellationToken);
        var again = await setup.ChangeAsync(SetupSetting.Skill, TestContext.Current.CancellationToken);

        Assert.Equal(SetupOutcome.Declined, declined.Outcome);
        Assert.Equal(SetupOutcome.NothingToChange, again.Outcome);
        Assert.Null(store.Saved);
        Assert.Null(setup.Changed);
    }

    [Fact]
    public async Task GoingBackFromTheChangeAsksTheSettingAgainWithTheAnswer()
    {
        store.Current = Existing;
        var setup = await OpenAsync();
        dialog.Answer("Skill", "night-shift");
        dialog.Confirm(null, true);

        var result = await setup.ChangeAsync(SetupSetting.Skill, TestContext.Current.CancellationToken);

        Assert.Equal(SetupOutcome.Written, result.Outcome);
        Assert.Equal(["Skill", "Save the change?", "Skill", "Save the change?"], dialog.Asked);
        Assert.Equal("night-shift", store.Saved!.Skill);
    }

    [Fact]
    public async Task LeavingASettingAfterGoingBackFromItsChangeShowsTheChangeAgain()
    {
        store.Current = Existing;
        var setup = await OpenAsync();
        dialog.Answer("Permission mode", "dontAsk");
        dialog.Back("Permission mode");
        dialog.Confirm(null, false);

        var result = await setup.ChangeAsync(SetupSetting.PermissionMode, TestContext.Current.CancellationToken);

        Assert.Equal(SetupOutcome.Declined, result.Outcome);
        Assert.Equal(["Permission mode", "Save the change?", "Permission mode", "Save the change?"], dialog.Asked);
        Assert.Contains(new SetupNote(SetupTone.Change, "Permission mode: auto by default → dontAsk"), dialog.Shown.Notes);
        Assert.Null(store.Saved);
    }

    [Fact]
    public async Task TheLabelsOfTheMenuShowWhatTheOtherSettingsHoldAndWhatIsSaved()
    {
        store.Current = Kept;
        var setup = await OpenAsync();

        foreach (var setting in (SetupSetting[])[SetupSetting.Blocking, SetupSetting.Take, SetupSetting.Manual, SetupSetting.Interrupted])
            await setup.ChangeAsync(setting, TestContext.Current.CancellationToken);

        // `bug` and `retired` are the labels to take, `draft` the blocking one, `manual` the manual label and
        // `interrupted` the interrupt label. The manual label may be a blocking one, and the other way round.
        Assert.Equal(["bug: to take", "interrupted: interrupt label", "manual: manual label", "retired: to take"], dialog.Held["Blocking labels"]);
        Assert.Equal(["draft: blocking", "interrupted: interrupt label", "manual: manual label"], dialog.Held["Labels to take"]);
        Assert.Equal(["bug: to take", "interrupted: interrupt label", "retired: to take"], dialog.Held[ManualLabel]);
        Assert.Equal(["bug: to take", "draft: blocking", "manual: manual label", "retired: to take"], dialog.Held[InterruptLabel]);
        Assert.Equal(["draft"], dialog.Saved["Blocking labels"]);
        Assert.Equal(["bug", "retired"], dialog.Saved["Labels to take"]);
        Assert.Null(store.Saved);
    }

    [Fact]
    public async Task TheManualLabelLeavesTheBlockingLabelsWhenTheyAreSaved()
    {
        // The settings name `manual` both as the manual label and as a blocking one, which keeps out nothing more.
        store.Current = Existing;
        var setup = await OpenAsync();

        var result = await setup.ChangeAsync(SetupSetting.Blocking, TestContext.Current.CancellationToken);

        Assert.Equal(SetupOutcome.Written, result.Outcome);
        Assert.Empty(store.Saved!.Queue.Labels.Blocking);
        Assert.Equal("manual", store.Saved.Queue.Labels.Manual);
        Assert.Contains("manual: manual label", dialog.Held["Blocking labels"]);
        Assert.Equal(["manual"], dialog.Saved["Blocking labels"]);
        Assert.Contains(new SetupNote(SetupTone.Change, "Blocking labels: manual → none"), dialog.Shown.Notes);
    }

    [Fact]
    public async Task ABlockingLabelThatIsChosenAsTheManualOneLeavesTheBlockingLabels()
    {
        store.Current = Kept;
        var setup = await OpenAsync();
        dialog.Answer(ManualLabel, "draft");

        var result = await setup.ChangeAsync(SetupSetting.Manual, TestContext.Current.CancellationToken);

        Assert.Equal(SetupOutcome.Written, result.Outcome);
        Assert.Equal("draft", store.Saved!.Queue.Labels.Manual);
        Assert.Empty(store.Saved.Queue.Labels.Blocking);
        Assert.Contains(new SetupNote(SetupTone.Change, "Blocking labels: draft → none"), dialog.Shown.Notes);
        Assert.Contains(new SetupNote(SetupTone.Change, "Manual label: manual → draft"), dialog.Shown.Notes);
    }

    [Fact]
    public async Task TheWalkLeavesTheManualLabelAmongTheBlockingOnes()
    {
        store.Current = Existing;

        await RunAsync();

        Assert.Equal(["manual"], (store.Saved ?? store.Current!).Queue.Labels.Blocking);
        Assert.Empty(dialog.Held["Blocking labels"]);
        Assert.Equal(["manual: blocking"], dialog.Held["Labels to take"]);
    }

    [Fact]
    public async Task TheTypedLabelsOfTheMenuRefuseWhatTheOtherSettingsHold()
    {
        machine.TrackerReachable = false;
        store.Current = Existing;
        var setup = await OpenAsync();
        dialog.Answer("Blocking labels", "draft, bug");
        dialog.Answer("Blocking labels", "draft, interrupted");
        dialog.Answer("Blocking labels", "draft");
        dialog.Answer("Labels to take", "bug, manual");
        dialog.Answer("Labels to take", "bug");

        await setup.ChangeAsync(SetupSetting.Blocking, TestContext.Current.CancellationToken);
        await setup.ChangeAsync(SetupSetting.Take, TestContext.Current.CancellationToken);

        Assert.Equal(["draft, bug", "draft, interrupted", "bug, manual"], dialog.Refused);
        Assert.Equal(["draft"], store.Saved!.Queue.Labels.Blocking);
        Assert.Equal(["bug"], store.Saved.Queue.Labels.Take);
    }

    [Fact]
    public async Task TheProjectOfTheMenuReadsTheBoardsOnceAndLinksTheOneThatIsChosen()
    {
        boards.Read = new SetupBoards([new SetupBoard(Rocket, "Rocket", true), new SetupBoard(Moon, "Moon base", false)], [new SetupOwner("acme", true)]);
        store.Current = Existing;
        var setup = await OpenAsync();
        Assert.Empty(dialog.Waited);
        dialog.Board(new BoardAnswer.Existing(Moon));

        var result = await setup.ChangeAsync(SetupSetting.Project, TestContext.Current.CancellationToken);
        var again = await setup.ChangeAsync(SetupSetting.Project, TestContext.Current.CancellationToken);

        Assert.Equal(SetupOutcome.Written, result.Outcome);
        Assert.Equal(Moon, store.Saved!.Tracker.Board);
        Assert.Equal([(Moon, "acme/rocket")], tracker.Linked);
        Assert.Equal([new SetupAnswer("Project", $"Moon base  {Moon}")], result.Answers);
        Assert.Equal(SetupOutcome.NothingToChange, again.Outcome);
        Assert.Equal(["Reading your projects from GitHub"], dialog.Waited);
        Assert.Single(tracker.Linked);
    }

    // The menu asks GitHub again for the boards it did not answer for, the next time the project is asked about.
    [Fact]
    public async Task TheProjectOfTheMenuReadsTheBoardsAgainWhenGitHubDidNotAnswer()
    {
        boards.Read = SetupBoards.NoAnswer;
        store.Current = Existing;
        var setup = await OpenAsync();

        await setup.ChangeAsync(SetupSetting.Project, TestContext.Current.CancellationToken);
        Assert.Equal(BoardsUnanswered, dialog.Question!.Unread);

        boards.Read = new SetupBoards([new SetupBoard(Rocket, "Rocket", true), new SetupBoard(Moon, "Moon base", false)], []);
        await setup.ChangeAsync(SetupSetting.Project, TestContext.Current.CancellationToken);
        await setup.ChangeAsync(SetupSetting.Project, TestContext.Current.CancellationToken);

        Assert.Null(dialog.Question.Unread);
        Assert.Equal(boards.Read.Boards, dialog.Question.Known);
        Assert.Equal(["Reading your projects from GitHub", "Reading your projects from GitHub"], dialog.Waited);
    }

    [Fact]
    public async Task ANewProjectOfTheMenuIsMadeWhenItIsSaved()
    {
        boards.Read = new SetupBoards([], [new SetupOwner("acme", true)]);
        store.Current = Existing;
        var setup = await OpenAsync();
        dialog.Board(new BoardAnswer.Created(new SetupOwner("acme", true), "Rocket two"));

        var result = await setup.ChangeAsync(SetupSetting.Project, TestContext.Current.CancellationToken);

        Assert.Equal(SetupOutcome.Written, result.Outcome);
        Assert.Equal([(new SetupOwner("acme", true), "Rocket two")], tracker.Made);
        Assert.Equal("https://github.com/orgs/acme/projects/42", store.Saved!.Tracker.Board);
        Assert.Equal("https://github.com/orgs/acme/projects/42", setup.Values[0].Value);
    }

    [Fact]
    public async Task TheStatusesOfABoardOfTheMenuAreAskedUnderIt()
    {
        tracker.Statuses = ["Ready", "Doing", "Shipped"];
        store.Current = Existing;
        var setup = await OpenAsync();

        var result = await setup.ChangeAsync(SetupSetting.Project, TestContext.Current.CancellationToken);

        Assert.Equal(SetupOutcome.Written, result.Outcome);
        Assert.Equal(new BoardStatuses("Ready", "Doing", "Shipped"), store.Saved!.Tracker.Statuses);
        Assert.Contains(dialog.Seen, progress => progress is { Menu: true, Later: true } && progress.Answers.SequenceEqual([new SetupAnswer("Project", Rocket)]));
        Assert.Contains(dialog.Shown.Notes, note => note.Tone == SetupTone.Change && note.Text.StartsWith("Statuses: ", StringComparison.Ordinal));
    }

    [Fact]
    public async Task OnlyTheLabelThatIsChangedInTheMenuIsMade()
    {
        tracker.Labels = ["bug", "draft", "feature", "manual"];
        store.Current = Existing;
        var setup = await OpenAsync();
        dialog.Answer(ManualLabel, NewLabel);
        dialog.Answer(ManualLabel, "ask");

        var result = await setup.ChangeAsync(SetupSetting.Manual, TestContext.Current.CancellationToken);

        Assert.Equal(SetupOutcome.Written, result.Outcome);
        Assert.Equal([("acme/rocket", "ask")], tracker.Labelled);
        Assert.Equal("ask", store.Saved!.Queue.Labels.Manual);
        Assert.Contains(new SetupNote(SetupTone.Plain, "The label “ask” will be made in acme/rocket."), dialog.Shown.Notes);
    }

    [Fact]
    public async Task TheMenuAsksForTheCommandsTheBranchAndThePermissionMode()
    {
        store.Current = Existing with { Commands = new TaskCommands("scripts/up", "scripts/down") };
        var setup = await OpenAsync();
        dialog.Answer("Before a task", " scripts/dev.sh ports free ");
        dialog.Answer("After a task", "  ");
        dialog.Answer("Branch", "my branch");
        dialog.Answer("Branch", "develop");
        dialog.Answer("Permission mode", "dontAsk");

        foreach (var setting in (SetupSetting[])[SetupSetting.Before, SetupSetting.After, SetupSetting.Branch, SetupSetting.PermissionMode])
            Assert.Equal(SetupOutcome.Written, (await setup.ChangeAsync(setting, TestContext.Current.CancellationToken)).Outcome);

        Assert.Equal(new TaskCommands("scripts/dev.sh ports free", null), store.Saved!.Commands);
        Assert.Equal("develop", store.Saved.Branch);
        Assert.Equal(["my branch"], dialog.Refused);
        Assert.Equal("dontAsk", store.Saved.Assistant.PermissionMode);
        Assert.Equal(["auto", "dontAsk"], dialog.Offered["Permission mode"]);
        Assert.Contains(dialog.Shown.Notes, note => note.Tone == SetupTone.Change && note.Text.StartsWith("Permission mode: ", StringComparison.Ordinal));
    }

    [Fact]
    public async Task TheModeOfASessionWithoutOneNamedStaysUnnamed()
    {
        store.Current = Existing;
        var setup = await OpenAsync();
        dialog.Answer("Permission mode", "auto");

        var result = await setup.ChangeAsync(SetupSetting.PermissionMode, TestContext.Current.CancellationToken);

        Assert.Equal(SetupOutcome.NothingToChange, result.Outcome);
        Assert.Null(store.Saved);
    }

    [Fact]
    public async Task WhatTheMenuChangedIsKeptForTheReportInTheOrderOfTheMenu()
    {
        store.Current = Existing;
        var setup = await OpenAsync();
        dialog.Answer("Skill", "night-shift");
        dialog.Answer("Skill", "task-auto");
        dialog.Answer("Blocking labels", "draft");

        await setup.ChangeAsync(SetupSetting.Skill, TestContext.Current.CancellationToken);
        await setup.ChangeAsync(SetupSetting.Blocking, TestContext.Current.CancellationToken);
        await setup.ChangeAsync(SetupSetting.Skill, TestContext.Current.CancellationToken);

        Assert.Equal(SetupOutcome.Written, setup.Changed!.Outcome);
        Assert.True(setup.Changed.SettingsWritten);
        Assert.Equal([new SetupAnswer("Blocking labels", "draft"), new SetupAnswer("Skill", "task-auto")], setup.Changed.Answers);
    }

    [Fact]
    public async Task WhatGitHubRefusesInTheMenuIsKeptForTheReport()
    {
        tracker.RefuseToLabel = "GitHub said no";
        store.Current = Existing;
        var setup = await OpenAsync();
        dialog.Answer(ManualLabel, NewLabel);
        dialog.Answer(ManualLabel, "ask");

        var result = await setup.ChangeAsync(SetupSetting.Manual, TestContext.Current.CancellationToken);

        Assert.Equal(SetupOutcome.Failed, result.Outcome);
        Assert.Equal("GitHub said no", result.Failure);
        Assert.Null(store.Saved);
        Assert.Equal(SetupOutcome.Failed, setup.Changed!.Outcome);
        Assert.Equal("manual", Value(setup.Values, SetupSetting.Manual).Value);
    }

    [Fact]
    public async Task AProjectWithoutABoardHasNoRowOfItsStatuses()
    {
        store.Current = Existing with { Tracker = new TrackerSettings("github", null) };

        var values = (await OpenAsync()).Values;

        Assert.DoesNotContain(values, value => value.Setting == SetupSetting.Statuses);
        Assert.Equal(new SetupValue(SetupSetting.Project, "Project", "none", SetupLook.Unset), values[0]);
    }

    [Fact]
    public async Task TheStatusesOfTheMenuAreAskedWhateverTheBoardCallsThem()
    {
        tracker.Statuses = ["Todo", "Ready", "In Progress", "Done"];
        store.Current = Existing;
        var setup = await OpenAsync();
        dialog.Answer(TodoStatus, "Ready");

        var result = await setup.ChangeAsync(SetupSetting.Statuses, TestContext.Current.CancellationToken);

        Assert.Equal(SetupOutcome.Written, result.Outcome);
        Assert.Equal([TodoStatus, InProgressStatus, DoneStatus, "Save the change?"], dialog.Asked);
        Assert.Equal(new BoardStatuses("Ready", "In Progress", "Done"), store.Saved!.Tracker.Statuses);
        Assert.Equal(Rocket, store.Saved.Tracker.Board);
        Assert.Equal([new SetupAnswer("Statuses", "Todo: Ready · In Progress: In Progress · Done: Done")], result.Answers);
        Assert.Empty(dialog.Waited);
    }

    [Fact]
    public async Task StatusesThatAreKeptAskNothingMore()
    {
        store.Current = Existing;
        var setup = await OpenAsync();

        var result = await setup.ChangeAsync(SetupSetting.Statuses, TestContext.Current.CancellationToken);

        Assert.Equal(SetupOutcome.NothingToChange, result.Outcome);
        Assert.Equal([TodoStatus, InProgressStatus, DoneStatus], dialog.Asked);
        Assert.Empty(result.Notes);
        Assert.Null(store.Saved);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task WhatKeepsTheStatusesOfABoardFromBeingAskedIsSaid(bool unreadable)
    {
        tracker.Statuses = unreadable ? null : ["Open", "Done"];
        store.Current = Existing;
        var setup = await OpenAsync();

        var result = await setup.ChangeAsync(SetupSetting.Statuses, TestContext.Current.CancellationToken);

        Assert.Equal(SetupOutcome.NothingToChange, result.Outcome);
        Assert.Empty(dialog.Asked);
        Assert.Equal(
            unreadable ? "The statuses of the board cannot be read: `toobusy doctor` says why." : "The board has no statuses “Todo”, “In Progress”: it has Open, Done.",
            result.Notes[0].Text);
        Assert.Equal(SetupTone.Warning, result.Notes[0].Tone);
    }

    [Fact]
    public async Task TheQuestionsMarkWhatTheSettingsHaveNow()
    {
        tracker.Statuses = ["Ready", "Todo", "In Progress", "Done"];
        store.Current = Kept with { Assistant = new AssistantSettings("claude-code") { PermissionMode = "dontAsk" } };
        var setup = await OpenAsync();
        dialog.Answer(TodoStatus, "Ready");
        dialog.Confirm(false);

        foreach (var setting in (SetupSetting[])[SetupSetting.Manual, SetupSetting.Interrupted, SetupSetting.PermissionMode, SetupSetting.Statuses])
            await setup.ChangeAsync(setting, TestContext.Current.CancellationToken);
        await setup.ChangeAsync(SetupSetting.Project, TestContext.Current.CancellationToken);

        Assert.Equal("manual", dialog.Current[ManualLabel]);
        Assert.Equal("interrupted", dialog.Current[InterruptLabel]);
        Assert.Equal("dontAsk", dialog.Current["Permission mode"]);
        Assert.Equal("Todo", dialog.Current[TodoStatus]);
        Assert.Equal("In Progress", dialog.Current[InProgressStatus]);
        Assert.Equal(Rocket, dialog.Question!.Saved);
    }

    [Fact]
    public async Task AFirstSetupMarksNothing()
    {
        dialog.Board(new BoardAnswer.Existing(Rocket));

        await RunAsync();

        Assert.All(dialog.Current.Values, Assert.Null);
        Assert.Null(dialog.Question!.Saved);
    }

    [Fact]
    public async Task TheStatusesOfAnotherBoardAreNotMarked()
    {
        tracker.Statuses = ["Ready", "Doing", "Shipped"];
        store.Current = Existing;
        dialog.Board(new BoardAnswer.Existing(Moon));

        await RunAsync();

        Assert.Null(dialog.Current[TodoStatus]);
    }

    async Task<ProjectSetup> OpenAsync()
    {
        var setup = new ProjectSetup(dialog, machine, tracker, boards, store);
        Assert.True(await setup.OpenAsync(TestContext.Current.CancellationToken));
        return setup;
    }

    static SetupValue Value(IReadOnlyList<SetupValue> values, SetupSetting setting) => values.Single(value => value.Setting == setting);

    Task<SetupResult> RunAsync(SetupProposals? proposals = null) => new ProjectSetup(dialog, machine, tracker, boards, store, proposals).RunAsync(TestContext.Current.CancellationToken);

    // A setup that asks nothing: it has no dialog.
    Task<SetupResult> RunUnaskedAsync(SetupProposals proposals) => new ProjectSetup(null, machine, tracker, boards, store, proposals).RunAsync(TestContext.Current.CancellationToken);

    static bool SameSettings(ProjectSettings? left, ProjectSettings? right) => FakeSettingsStore.Render(left!) == FakeSettingsStore.Render(right!);
}
