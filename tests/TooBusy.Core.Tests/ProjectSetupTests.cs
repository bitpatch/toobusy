using TooBusy.Core.Queue;
using TooBusy.Core.Settings;
using TooBusy.Core.Setup;

namespace TooBusy.Core.Tests;

public class ProjectSetupTests
{
    const string Rocket = "https://github.com/orgs/acme/projects/1";
    const string Moon = "https://github.com/users/denis/projects/7";

    const string OwnerLabel = "Owner's label";
    const string InterruptLabel = "Interrupt label";
    const string NewLabel = "New label…";

    static readonly ProjectSettings Existing = new(
        new TrackerSettings("github", Rocket),
        new QueueSettings(new LabelSettings(["manual"], ["bug", "retired"], "manual", "interrupted")),
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
        Assert.Equal("needs-owner", store.Saved.Queue.Labels.Owner);
        Assert.Equal("interrupted", store.Saved.Queue.Labels.Interrupted);
        Assert.Equal(new AssistantSettings("claude-code"), store.Saved.Assistant);

        // The label of the owner is not one of the repository yet, so its name is asked for after the list.
        Assert.Equal(["Project", "Blocking labels", "Labels to take", OwnerLabel, OwnerLabel, InterruptLabel, "Write the settings?"], dialog.Asked);
    }

    [Fact]
    public async Task AFirstSetupSaysThatTheAnswersWillBeWritten()
    {
        await RunAsync();

        Assert.Equal(
            [
                new SetupNote(SetupTone.Plain, "The answers above will be written to .toobusy/settings.toml."),
                new SetupNote(SetupTone.Plain, "The label “needs-owner” will be made in acme/rocket."),
            ],
            dialog.Shown.Notes);
        Assert.Equal(7, dialog.Shown.Answers.Count);
    }

    [Fact]
    public async Task TheAnswersBecomeTheSettings()
    {
        dialog.Board(new BoardAnswer.Existing($" {Moon}/views/2?layout=board "));
        dialog.Answer("Blocking labels", "manual", "draft");
        dialog.Answer("Labels to take", "bug");
        dialog.Answer(OwnerLabel, "manual");
        dialog.Answer(InterruptLabel, "interrupted");

        var result = await RunAsync();

        Assert.Equal(new TrackerSettings("github", Moon), store.Saved!.Tracker);
        Assert.Equal(["draft", "manual"], store.Saved.Queue.Labels.Blocking);
        Assert.Equal(["bug"], store.Saved.Queue.Labels.Take);
        Assert.Equal("manual", store.Saved.Queue.Labels.Owner);
        Assert.Equal("interrupted", store.Saved.Queue.Labels.Interrupted);
        Assert.Equal(
            [
                new SetupAnswer("Tracker", "GitHub"),
                new SetupAnswer("Project", Moon),
                new SetupAnswer("Blocking labels", "draft, manual"),
                new SetupAnswer("Labels to take", "bug"),
                new SetupAnswer(OwnerLabel, "manual"),
                new SetupAnswer(InterruptLabel, "interrupted"),
                new SetupAnswer("Assistant", "Claude Code"),
            ],
            result.Answers);
    }

    [Fact]
    public async Task TheStepsAndThePlaceAmongThemAreShown()
    {
        await RunAsync();

        // At the confirmation every step is done.
        Assert.Equal(["Project", "Blocking labels", "Labels to take", OwnerLabel, InterruptLabel], dialog.Shown.Steps);
        Assert.Equal([0, 1, 2, 3, 4, 5], dialog.Places.Distinct());
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
    public async Task ALabelOfTheRepositoryIsChosenForTheOwnerAndNothingIsMade()
    {
        dialog.Answer(OwnerLabel, "manual");

        await RunAsync();

        Assert.Equal("manual", store.Saved!.Queue.Labels.Owner);
        Assert.Empty(tracker.Labelled);
        Assert.DoesNotContain(dialog.Shown.Notes, note => note.Text.Contains("will be made", StringComparison.Ordinal));
    }

    [Fact]
    public async Task ANewLabelIsNamedAndMadeOnlyWhenTheSetupIsConfirmed()
    {
        dialog.Answer(OwnerLabel, NewLabel);
        dialog.Answer(OwnerLabel, " ask the owner ");
        dialog.BeforeConfirm = () => Assert.Empty(tracker.Labelled);

        var result = await RunAsync();

        Assert.Contains(new SetupNote(SetupTone.Plain, "The label “ask the owner” will be made in acme/rocket."), dialog.Shown.Notes);
        Assert.Equal([("acme/rocket", "ask the owner")], tracker.Labelled);
        Assert.Equal("ask the owner", store.Saved!.Queue.Labels.Owner);
        Assert.Contains(new SetupAnswer(OwnerLabel, "ask the owner"), result.Answers);
    }

    [Fact]
    public async Task GoingBackFromTheNameOfANewLabelComesBackToTheLabels()
    {
        dialog.Answer(OwnerLabel, NewLabel);
        dialog.Back(OwnerLabel);
        dialog.Answer(OwnerLabel, "manual");

        await RunAsync();

        Assert.Equal([OwnerLabel, OwnerLabel, OwnerLabel, InterruptLabel], dialog.Asked.Skip(3).Take(4));
        Assert.Equal("manual", store.Saved!.Queue.Labels.Owner);
    }

    [Fact]
    public async Task ALabelToTakeIsNotOfferedForTheOwnerAndIsRefusedAsANewOne()
    {
        dialog.Answer("Labels to take", "bug");
        dialog.Answer(OwnerLabel, NewLabel);
        dialog.Answer(OwnerLabel, "Bug");
        dialog.Answer(OwnerLabel, "  ");
        dialog.Answer(OwnerLabel, "manual");

        await RunAsync();

        Assert.Equal(["draft", "feature", "interrupted", "manual", NewLabel], dialog.Offered[OwnerLabel]);
        Assert.Equal(["Bug", "  "], dialog.Refused);
        Assert.Equal("manual", store.Saved!.Queue.Labels.Owner);
    }

    [Fact]
    public async Task TheLabelOfInterruptedTasksIsNeitherBlockingNorTheOneOfTheOwner()
    {
        dialog.Answer("Blocking labels", "draft");
        dialog.Answer("Labels to take", "bug");
        dialog.Answer(OwnerLabel, "manual");

        await RunAsync();

        Assert.Equal(["feature", "interrupted", NewLabel], dialog.Offered[InterruptLabel]);
    }

    [Fact]
    public async Task ALabelThatCannotBeMadeFailsTheSetupAndWritesNothing()
    {
        tracker.RefuseToLabel = "The label “needs-owner” could not be made in acme/rocket.";

        var result = await RunAsync();

        Assert.Equal(SetupOutcome.Failed, result.Outcome);
        Assert.Equal("The label “needs-owner” could not be made in acme/rocket.", result.Failure);
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
        Assert.Equal([InterruptLabel, "Write the settings?", InterruptLabel, "Write the settings?"], dialog.Asked.Skip(5));
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
        dialog.Answer(OwnerLabel, "draft");
        dialog.Answer(InterruptLabel, "feature");

        await RunAsync();

        Assert.Equal(
            [
                $"Project: Rocket  {Rocket} → Moon  {Moon}",
                "Blocking labels: manual → none",
                "Labels to take: bug, retired → bug",
                "Owner's label: manual → draft",
                "Interrupt label: interrupted → feature",
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
        dialog.Answer(InterruptLabel, "needs-owner");
        dialog.Answer(InterruptLabel, " paused ");

        await RunAsync();

        Assert.Contains(dialog.Shown.Notes, note => note.Tone == SetupTone.Warning && note.Text.Contains("nothing is verified", StringComparison.Ordinal));
        Assert.Equal(0, tracker.Calls);
        Assert.Null(boards.Repository);
        Assert.Equal(Rocket, store.Saved!.Tracker.Board);
        Assert.Equal(["manual", "draft"], store.Saved.Queue.Labels.Blocking);
        Assert.Equal(["bug", "feature"], store.Saved.Queue.Labels.Take);
        Assert.Equal("needs-owner", store.Saved.Queue.Labels.Owner);
        Assert.Equal("paused", store.Saved.Queue.Labels.Interrupted);
        Assert.Equal(["draft", "needs-owner"], dialog.Refused);
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
        Assert.Equal("manual", store.Saved.Queue.Labels.Owner);
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
        Assert.Equal("draft", store.Saved.Queue.Labels.Owner);

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
            [
                new SetupNote(SetupTone.Warning, "Project: rocket is not a project. Use https://github.com/orgs/<org>/projects/<number>."),
                new SetupNote(SetupTone.Warning, "Blocking labels: acme/rocket has no label “urgent”."),
                new SetupNote(SetupTone.Warning, "Labels to take: acme/rocket has no label “nice”."),
            ],
            dialog.Shown.Notes.Where(note => note.Tone == SetupTone.Warning));
        Assert.Null(store.Saved!.Tracker.Board);
        Assert.Equal(["manual"], store.Saved.Queue.Labels.Blocking);
        Assert.Empty(store.Saved.Queue.Labels.Take);
    }

    [Fact]
    public async Task WithoutQuestionsAFirstSetupTakesWhatIsProposedAndSaysWhatItDid()
    {
        boards.Read = new SetupBoards([new(Moon, "Moon", false), new(Rocket, "Rocket", true)], []);

        var result = await RunUnaskedAsync(SetupProposals.None);

        Assert.Equal(SetupOutcome.Written, result.Outcome);
        Assert.True(result.SettingsWritten);
        Assert.Equal(Rocket, store.Saved!.Tracker.Board);
        Assert.Empty(store.Saved.Queue.Labels.Blocking);
        Assert.Empty(store.Saved.Queue.Labels.Take);
        Assert.Equal("needs-owner", store.Saved.Queue.Labels.Owner);
        Assert.Equal("interrupted", store.Saved.Queue.Labels.Interrupted);
        Assert.Equal(7, result.Answers.Count);
        Assert.Equal(["The label “needs-owner” was made in acme/rocket."], result.Done);
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
        Assert.Equal("manual", store.Saved.Queue.Labels.Owner);
        Assert.Equal("paused", store.Saved.Queue.Labels.Interrupted);
        Assert.Equal(["The project was linked to acme/rocket.", "The label “paused” was made in acme/rocket."], result.Done);
        Assert.Equal([(Moon, "acme/rocket")], tracker.Linked);
    }

    [Fact]
    public async Task WithoutQuestionsANewBoardIsMadeLinkedAndWritten()
    {
        boards.Read = new SetupBoards([], [new("acme", true)]);

        var result = await RunUnaskedAsync(new SetupProposals(new BoardProposal.Created("Rocket", null)));

        Assert.Equal(SetupOutcome.Written, result.Outcome);
        Assert.Equal("https://github.com/orgs/acme/projects/42", store.Saved!.Tracker.Board);
        Assert.Equal(
            [
                "The project “Rocket” was made for acme: https://github.com/orgs/acme/projects/42",
                "The project was linked to acme/rocket.",
                "The label “needs-owner” was made in acme/rocket.",
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
        Assert.Equal("manual", store.Saved.Queue.Labels.Owner);
        Assert.Empty(result.Done);
    }

    [Theory]
    [InlineData("board", "Project: https://github.com/orgs/acme/projects/1 cannot be read")]
    [InlineData("address", "Project: acme/rocket is not a project. Use https://github.com/orgs/<org>/projects/<number>.")]
    [InlineData("title", "Project: A project needs a title.")]
    [InlineData("whose", "Project: A project cannot be made for ann, only for acme, denis.")]
    [InlineData("blocking", "Blocking labels: acme/rocket has no label “urgent”.")]
    [InlineData("take", "Labels to take: acme/rocket has no label “nice”.")]
    [InlineData("shared", "Labels to take: bug is a blocking label.")]
    [InlineData("owner", "Owner's label: bug is a label to take.")]
    [InlineData("unnamed", "Owner's label: A label needs a name.")]
    [InlineData("interrupt", "Interrupt label: manual is the label of the owner.")]
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
            "owner" => new SetupProposals(Take: ["bug"], Owner: "bug"),
            "unnamed" => new SetupProposals(Owner: " "),
            _ => new SetupProposals(Owner: "manual", Interrupted: "manual"),
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
        Assert.Equal("waiting", store.Saved.Queue.Labels.Owner);
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

    Task<SetupResult> RunAsync(SetupProposals? proposals = null) => new ProjectSetup(dialog, machine, tracker, boards, store, proposals).RunAsync(TestContext.Current.CancellationToken);

    // A setup that asks nothing: it has no dialog.
    Task<SetupResult> RunUnaskedAsync(SetupProposals proposals) => new ProjectSetup(null, machine, tracker, boards, store, proposals).RunAsync(TestContext.Current.CancellationToken);

    static bool SameSettings(ProjectSettings? left, ProjectSettings? right) => FakeStore.Render(left!) == FakeStore.Render(right!);

    // Answers each question from its script and accepts what is proposed when the script has nothing for it.
    // A board that the project has is kept; without one the answer is the one that is proposed, or no board.
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

        // The options of a selection or of a multiple choice, under the label of the question.
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

        // What the user does not wait for, by the text of the wait.
        public HashSet<string> Unwaited { get; } = [];

        public async Task<T?> WaitAsync<T>(string label, string hint, string text, Func<CancellationToken, Task<T>> work, CancellationToken cancellationToken)
            where T : class
        {
            Waited.Add(text);
            return Unwaited.Remove(text) ? null : await work(cancellationToken);
        }

        public int? Choose(string label, string hint, IReadOnlyList<SetupOption> options, int proposed)
        {
            Asked.Add(label);
            Offered[label] = [.. options.Select(option => option.Name)];
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
            return question.Current is { } current ? new BoardAnswer.Existing(current.Address) : question.Proposed ?? new BoardAnswer.None();
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

        public List<(string Repository, string Name)> Labelled { get; } = [];

        public string? RefuseToLabel { get; set; }

        public Task<string?> RefuseBoardAsync(string board, CancellationToken cancellationToken)
        {
            Calls++;
            return Task.FromResult(Unreadable.Contains(board) ? $"{board} cannot be read" : null);
        }

        public Task<IReadOnlyList<string>> ReadLabelsAsync(string repository, CancellationToken cancellationToken)
        {
            Calls++;
            return Task.FromResult<IReadOnlyList<string>>(["bug", "draft", "feature", "interrupted", "manual"]);
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

        public Task CreateLabelAsync(string repository, string name, CancellationToken cancellationToken)
        {
            Calls++;
            if (RefuseToLabel is not null)
                throw new TrackerException(RefuseToLabel);
            Labelled.Add((repository, name));
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
            $"take = {string.Join(", ", settings.Queue.Labels.Take)}",
            $"owner = {settings.Queue.Labels.Owner}",
            $"interrupted = {settings.Queue.Labels.Interrupted}") + "\n";
    }
}
