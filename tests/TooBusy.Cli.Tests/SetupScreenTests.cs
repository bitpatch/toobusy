using TooBusy.Cli.Terminal;
using TooBusy.Core.Setup;

namespace TooBusy.Cli.Tests;

public sealed class SetupScreenTests : IDisposable
{
    const string Rocket = "https://github.com/orgs/acme/projects/1";
    const string Moon = "https://github.com/users/denis/projects/7";

    static readonly SetupOption[] Rules = [new("lowest-version", "→ v.0.2.0"), new("earliest-due", "→ Polish"), new("none", "")];

    static readonly SetupBoard[] Boards = [new(Rocket, "Rocket", true), new(Moon, "Moon base", false)];

    static readonly SetupOwner[] Owners = [new("acme", true), new("denis", false)];

    readonly TestTerminal terminal = new(width: 80, height: 20);
    readonly Page page;
    readonly SetupScreen screen;

    public SetupScreenTests()
    {
        page = terminal.Open();
        screen = new SetupScreen(page);
    }

    public void Dispose()
    {
        page.Dispose();
        terminal.Dispose();
    }

    [Fact]
    public async Task TheAnswersTheNotesAndTheStepsStandAroundTheQuestion()
    {
        screen.Show(new SetupProgress(
            [new SetupNote(SetupTone.Warning, "nothing is verified"), new SetupNote(SetupTone.Failure, "gh is missing"), new SetupNote(SetupTone.Change, "Project: none → Rocket")],
            [new SetupAnswer("Tracker", "GitHub"), new SetupAnswer("Project", "none")],
            ["Project", "Milestone", "Write"],
            1));
        await screen.WaitAsync("Milestone", "The milestone of the tasks.", "Reading the milestones", _ => Task.FromResult("read"), TestContext.Current.CancellationToken);

        Assert.Equal(
            [
                "  toobusy · ~/rocket",
                " ✔ Tracker          GitHub",
                " ✔ Project          none",
                " ! nothing is verified",
                " ✘ gh is missing",
                "   → Project: none → Rocket",
                " Milestone",
                " The milestone of the tasks.",
                " Reading the milestones",
                " •••••••••••",
                " ❯ Back  esc",
            ],
            terminal.Text.Split('\n').Take(11));
        Assert.Equal([" enter choose · ctrl+c exit", "", " ●──◉──○"], terminal.Frame[^3..]);
    }

    [Fact]
    public async Task WhenOnlyTheConfirmationIsLeftEveryStepIsDone()
    {
        screen.Show(new SetupProgress([], [], ["Project", "Blocking labels", "Labels to take"], 3));
        await screen.WaitAsync("Project", "The project.", "…", _ => Task.FromResult("read"), TestContext.Current.CancellationToken);

        Assert.Equal(" ●──●──●", terminal.Frame[^1]);
    }

    [Fact]
    public void ASelectionShowsItsOptionsWithThePointerOnWhatIsProposed()
    {
        terminal.Keys.Press(Keys.Enter);

        Assert.Equal(1, screen.Choose("Milestone rule", "Which milestone.", Rules, 1));
        terminal.AssertSaw(" Milestone rule\n Which milestone.\n   lowest-version  → v.0.2.0\n ❯ earliest-due    → Polish\n   none\n   Back            esc\n ↑↓ move · enter choose · ctrl+c exit");
    }

    [Fact]
    public void TheArrowsMoveThePointerAndWrapAround()
    {
        terminal.Keys.Press(Keys.Up, Keys.Up, Keys.Enter);
        Assert.Equal(2, screen.Choose("Milestone rule", "", Rules, 0));

        terminal.Keys.Press(Keys.Down, Keys.Down, Keys.Down, Keys.Down, Keys.Down, Keys.Enter);
        Assert.Equal(1, screen.Choose("Milestone rule", "", Rules, 0));
    }

    [Fact]
    public void TheLastRowOfAListGoesBackAsEscapeDoes()
    {
        terminal.Keys.Press(Keys.Up, Keys.Enter, Keys.Up, Keys.Enter, Keys.Down, Keys.Down, Keys.Enter, Keys.Down, Keys.Down, Keys.Enter);

        Assert.Null(screen.Choose("Milestone rule", "", Rules, 0));
        Assert.Null(screen.ChooseMany("Blocking labels", "", ["bug", "draft"], []));
        terminal.AssertSaw("   ◯ bug\n   ◯ draft\n ❯ Back  esc\n ↑↓ move · space select · enter confirm · ctrl+c exit");
        Assert.Null(screen.Confirm("Write the settings?"));
        Assert.Null(screen.AskBoard(Question(current: Boards[0])));
    }

    [Fact]
    public void TheListsOfTheBoardsEndWithBackToo()
    {
        terminal.Keys.Press(Keys.Up, Keys.Enter, Keys.ShiftTab, Keys.Down, Keys.Enter);

        Assert.Null(screen.AskBoard(Question(current: null)));
        Assert.Null(screen.AskBoard(Question(current: null)));
        terminal.AssertSaw("   Go on without a project\n ❯ Back  esc\n ↑↓ move · enter confirm · ctrl+c exit");
    }

    [Fact]
    public void WhereEscapeLeavesTheSetupThereIsNoBack()
    {
        screen.Show(new SetupProgress([], [], ["Project", "Write"], 0));
        terminal.Keys.Press(Keys.Up, Keys.Enter);

        Assert.Equal(2, screen.Choose("Milestone rule", "", Rules, 0));
        Assert.DoesNotContain("Back", terminal.Text, StringComparison.Ordinal);
    }

    [Fact]
    public void EscapeGoesBackFromEveryQuestion()
    {
        terminal.Keys.Press(Keys.Escape, Keys.Escape, Keys.Escape, Keys.Escape, Keys.Escape, Keys.Escape);

        Assert.Null(screen.Choose("Milestone rule", "", Rules, 0));
        Assert.Null(screen.ChooseMany("Blocking labels", "", ["bug"], []));
        Assert.Null(screen.Ask("Milestone", "", "v.1", _ => null));
        Assert.Null(screen.Confirm("Write the settings?"));
        Assert.Null(screen.AskBoard(Question(current: Boards[0])));
        Assert.Null(screen.AskBoard(Question(current: null)));
    }

    [Fact]
    public void FromTheFirstStepEscapeLeavesTheSetupAndAsksBeforeItDoes()
    {
        screen.Show(new SetupProgress([], [], ["Project", "Write"], 0));
        terminal.Keys.Press(Keys.Escape, Keys.Escape);

        Assert.Null(screen.AskBoard(Question(current: Boards[0])));
        terminal.AssertSaw(" ↑↓ move · enter choose · esc exit · ctrl+c exit");
        terminal.AssertSaw(" press esc again to exit");
    }

    [Fact]
    public void FromTheWaysToNameABoardEscapeComesBackWithoutAsking()
    {
        screen.Show(new SetupProgress([], [], ["Project", "Write"], 0));
        terminal.Keys.Press(Keys.Down, Keys.Enter, Keys.Escape, Keys.Enter);

        Assert.Equal(new BoardAnswer.Existing(Rocket), screen.AskBoard(Question(current: Boards[0])));
        terminal.AssertSaw("   Back  esc\n type to filter · ↑↓ move · enter choose · ctrl+c exit");
    }

    [Fact]
    public void AfterTheFirstStepEscapeGoesBackAtOnce()
    {
        screen.Show(new SetupProgress([], [], ["Project", "Write"], 1));
        terminal.Keys.Press(Keys.Escape);

        Assert.Null(screen.Confirm("Write the settings?"));
        terminal.AssertSaw("   Back  esc\n ↑↓ move · enter choose · ctrl+c exit");
    }

    [Fact]
    public void ALongListSaysHowMuchOfItIsBeyondTheWindow()
    {
        SetupOption[] many = [.. Enumerable.Range(1, 12).Select(number => new SetupOption($"v.{number}", ""))];
        terminal.Keys.Press(Keys.Enter);
        screen.Choose("Milestone", "", many, 0);
        terminal.AssertSaw(" ❯ v.1\n");
        terminal.AssertSaw("   v.8\n   ↓ 5 more\n");
        Assert.DoesNotContain("↑", terminal.Text.Replace("↑↓", "", StringComparison.Ordinal), StringComparison.Ordinal);

        terminal.Keys.Press([.. Enumerable.Repeat(Keys.Down, 9), Keys.Enter]);
        Assert.Equal(9, screen.Choose("Milestone", "", many, 0));
        terminal.AssertSaw("   ↑ 2 more\n   v.3\n");
        terminal.AssertSaw(" ❯ v.10\n   ↓ 3 more\n");
    }

    [Fact]
    public void TheSpaceBarMarksAndUnmarksLabels()
    {
        terminal.Keys.Press(Keys.Space, Keys.Down, Keys.Down, Keys.Space, Keys.Up, Keys.Space, Keys.Enter);

        var chosen = screen.ChooseMany("Blocking labels", "Never taken.", ["bug", "draft", "manual"], [0]);

        Assert.Equal([1, 2], chosen);
        terminal.AssertSaw(" Never taken.\n   ◯ bug\n ❯ ◉ draft\n   ◉ manual\n   Back  esc\n ↑↓ move · space select · enter confirm · ctrl+c exit");
    }

    [Fact]
    public void AMultipleChoiceOverNothingStillWaitsForAKey()
    {
        terminal.Keys.Press(Keys.Space, Keys.Down, Keys.Enter);

        Assert.Empty(screen.ChooseMany("Blocking labels", "", [], [])!);
        terminal.AssertSaw(" There is nothing to choose from.\n enter confirm");
    }

    [Fact]
    public void ATextIsEditedWhereTheCaretIs()
    {
        terminal.Keys.Press(Keys.Home).Type("the-").Press(Keys.Right, Keys.Right, Keys.Right, Keys.Right, Keys.Delete).Type("-").Press(Keys.End, Keys.Left, Keys.Backspace, Keys.Enter);

        Assert.Equal("the-acme-rockt", screen.Ask("Milestone", "Its title.", "acme/rocket", _ => null));
    }

    [Fact]
    public void TheCursorIsPutWhereTheTypingGoes()
    {
        terminal.Keys.Press(Keys.Left, Keys.Left, Keys.Enter);

        screen.Ask("Milestone", "Its title.", "v.0.2.0", _ => null);

        terminal.AssertSaw(" Milestone\n Its title.\n v.0.2.0\n enter confirm");

        // The line of the text is the third from the bottom, above a rule and the keys.
        Assert.Equal((18, 7), terminal.Caret);
    }

    [Fact]
    public void ARefusedAnswerStaysWithTheReasonInPlaceOfTheHint()
    {
        terminal.Keys.Press(Keys.Enter).Type(".1").Press(Keys.Enter);

        var answer = screen.Ask("Milestone", "Its title.", "v", text => text.Contains('.', StringComparison.Ordinal) ? null : "no dot");

        Assert.Equal("v.1", answer);
        terminal.AssertSaw(" Milestone\n no dot\n v.1\n");
    }

    [Fact]
    public void TheLastQuestionSavesOrLeavesWithoutSaving()
    {
        terminal.Keys.Press(Keys.Enter, Keys.Down, Keys.Enter, Keys.Down, Keys.Up, Keys.Enter);

        Assert.True(screen.Confirm("Write the settings?"));
        terminal.AssertSaw(" Write the settings?\n ❯ Save and exit\n   Exit without saving\n   Back  esc\n ↑↓ move · enter choose · ctrl+c exit");
        Assert.False(screen.Confirm("Write the settings?"));
        terminal.AssertSaw("   Save and exit\n ❯ Exit without saving");
        Assert.True(screen.Confirm("Write the settings?"));
    }

    [Fact]
    public void AProjectThatHasABoardKeepsItWithOneEnter()
    {
        terminal.Keys.Press(Keys.Enter);

        Assert.Equal(new BoardAnswer.Existing(Rocket), screen.AskBoard(Question(current: Boards[0])));
        terminal.AssertSaw($" Project\n The project of the tasks.\n ❯ Rocket  {Rocket}  linked to this repository\n   Choose another project\n   Back    esc\n ↑↓ move · enter choose · ctrl+c exit");
    }

    [Fact]
    public void ChangeOpensTheWaysToNameABoardAndEscapeComesBackToTheBoardThatIs()
    {
        terminal.Keys.Press(Keys.Down, Keys.Enter, Keys.Escape, Keys.Enter);

        Assert.Equal(new BoardAnswer.Existing(Rocket), screen.AskBoard(Question(current: Boards[0])));
        terminal.AssertSaw(" [Your projects]");
        terminal.AssertSaw($" ❯ Rocket  {Rocket}  linked to this repository\n   Choose another project");
    }

    [Fact]
    public void TheKnownBoardsAreListedWithThePointerOnTheOneThatIs()
    {
        terminal.Keys.Press(Keys.Down, Keys.Enter, Keys.Up, Keys.Enter);

        var answer = screen.AskBoard(Question(current: Boards[1]));

        Assert.Equal(new BoardAnswer.Existing(Rocket), answer);
    }

    [Fact]
    public void WithoutABoardTheWaysToNameOneAreTabs()
    {
        terminal.Keys.Press(Keys.Enter);

        Assert.Equal(new BoardAnswer.Existing(Rocket), screen.AskBoard(Question(current: null)));
        terminal.AssertSaw($" Project\n The project of the tasks.\n [Your projects]  By URL   New project   No project   press tab to switch\n Filter\n ❯ Rocket     {Rocket}  linked\n   Moon base  {Moon}\n   Back  esc\n type to filter · ↑↓ move · enter choose · ctrl+c exit");
    }

    [Fact]
    public void ThePlaceUnderTheTabsIsAsTallAsTheListWhateverTabIsOpen()
    {
        terminal.Keys.Press(Keys.Tab, Keys.Tab, Keys.Tab, Keys.Enter);

        screen.AskBoard(Question(current: null));

        // The tabs, an empty line, and six lines, as many as the tallest tab has: a list from their top, or what a
        // tab without a list has at their bottom.
        terminal.AssertSaw(" [Your projects]  By URL   New project   No project   press tab to switch\n Filter\n ❯ Rocket");
        Assert.Equal(
            [
                "  Your projects   By URL   New project  [No project]  press tab to switch",
                "",
                "",
                "",
                "",
                "   The tasks are taken from the repository, and no board keeps their statuses.",
                " ❯ Go on without a project",
                "   Back  esc",
            ],
            terminal.Frame[^10..^2]);
        Assert.Equal(4, terminal.Frames);
        Assert.Single(terminal.Output.ToString().Split("\u001b[H").Skip(1).Select(frame => frame.Split("\r\n").Length).Distinct());
    }

    [Fact]
    public void WithColoursTheTabsStandOnAGroundAndTheChosenOneOnTheAccent()
    {
        var palette = Palette.Dark;
        using var coloured = new TestTerminal(width: 80, height: 20);
        using var colouredPage = coloured.Open(palette: palette);
        coloured.Keys.Press(Keys.Enter);

        new SetupScreen(colouredPage).AskBoard(Question(current: null));

        Assert.Contains(
            $"{palette.Paint(Tone.Quiet, " Your projects ", Ground.Chosen)} {palette.Paint(Tone.Quiet, " By URL ", Ground.Bar)} ",
            coloured.Output.ToString(),
            StringComparison.Ordinal);
        Assert.Contains($"{palette.Muted(" press ")}{palette.Paint(Tone.Quiet, "tab")}{palette.Muted(" to switch")}", coloured.Output.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain("[Your projects]", coloured.Output.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void TypingNarrowsTheKnownBoards()
    {
        terminal.Keys.Type("moon").Press(Keys.Enter);

        Assert.Equal(new BoardAnswer.Existing(Moon), screen.AskBoard(Question(current: null)));
        terminal.AssertSaw($" Filter  moon\n ❯ Moon base  {Moon}\n   Back  esc\n type to filter");
    }

    [Fact]
    public void WhatFitsNoBoardChoosesNone()
    {
        terminal.Keys.Type("mars").Press(Keys.Enter, Keys.Control('u'), Keys.Enter);

        Assert.Equal(new BoardAnswer.Existing(Rocket), screen.AskBoard(Question(current: null)));
        terminal.AssertSaw(" Filter  mars\n   No project fits.\n");
    }

    [Fact]
    public void NoMoreThanFiveBoardsAreShownAndTheRestIsCounted()
    {
        SetupBoard[] many = [.. Enumerable.Range(1, 8).Select(number => new SetupBoard($"https://github.com/users/denis/projects/{number}", $"Board {number}", false))];
        terminal.Keys.Press([.. Enumerable.Repeat(Keys.Down, 5), Keys.Enter]);

        var answer = screen.AskBoard(Question(current: null) with { Known = many });

        Assert.Equal(new BoardAnswer.Existing("https://github.com/users/denis/projects/6"), answer);
        Assert.Equal(
            ["  ↑ 1 more", "  Board 2", "  Board 3", "  Board 4", "  Board 5", "❯ Board 6", "  ↓ 3 more"],
            terminal.Frame.Where(line => line.Contains("Board ", StringComparison.Ordinal) || line.Contains(" more", StringComparison.Ordinal)).Select(line => line[1..].Split("  https")[0]));
    }

    [Fact]
    public void ABoardIsNamedByItsAddress()
    {
        terminal.Keys.Press(Keys.Tab).Type("moon").Press(Keys.Enter, Keys.Control('u')).Type(Moon + "/views/1").Press(Keys.Enter);

        var answer = screen.AskBoard(Question(current: null));

        Assert.Equal(new BoardAnswer.Existing(Moon + "/views/1"), answer);
        terminal.AssertSaw("  Your projects  [By URL]  New project   No project   press tab to switch\n not an address\n moon\n enter confirm");
    }

    [Fact]
    public void ANewBoardHasAnOwnerChosenFromAListAndATitle()
    {
        terminal.Keys.Press(Keys.Tab, Keys.Tab, Keys.Enter, Keys.Down).Type("Rocket two").Press(Keys.Enter);

        var answer = screen.AskBoard(Question(current: null));

        Assert.Equal(new BoardAnswer.Created(Owners[1], "Rocket two"), answer);

        // The owners are a list at the top, as the boards are; the title is typed under them.
        terminal.AssertSaw(
            "  Your projects   By URL  [New project]  No project   press tab to switch\n Whose project it will be:\n ❯ acme   organisation\n   denis  your account\n Title\n It is made when the setup is confirmed, and linked to the repository.\n ↑↓ owner · enter confirm");
        terminal.AssertSaw(" Whose project it will be:\n ❯ acme   organisation\n   denis  your account\n Title\n no title\n");
        terminal.AssertSaw(" Whose project it will be:\n   acme   organisation\n ❯ denis  your account\n Title   Rocket two\n");
    }

    [Fact]
    public void InANewBoardTheOwnerIsTheChosenLineAndTheTitleHasTheCursor()
    {
        var palette = Palette.Dark;
        using var coloured = new TestTerminal(width: 80, height: 20);
        using var colouredPage = coloured.Open(palette: palette);
        coloured.Keys.Press(Keys.Tab, Keys.Tab).Type("R").Press(Keys.Enter);

        new SetupScreen(colouredPage).AskBoard(Question(current: null));

        // The place of the tab is six lines under the tabs and an empty line: the owners from its top, the title at its bottom.
        Assert.EndsWith(
            "\u001b[14;2H" + palette.Glow("❯ acme ", 1) + "\u001b[17;11H" + palette.Cursor(" ", 1),
            coloured.Output.ToString().Split(Screen.Leave)[0],
            StringComparison.Ordinal);
    }

    [Fact]
    public void NoBoardIsAnAnswerToo()
    {
        terminal.Keys.Press(Keys.ShiftTab, Keys.Enter);

        Assert.Equal(new BoardAnswer.None(), screen.AskBoard(Question(current: null)));
    }

    [Fact]
    public void WithoutKnownBoardsAndOwnersOnlyTheAddressAndNoBoardAreLeft()
    {
        terminal.Keys.Press(Keys.Tab, Keys.Enter);

        var answer = screen.AskBoard(Question(current: null) with { Known = [], Owners = [] });

        Assert.Equal(new BoardAnswer.None(), answer);
        terminal.AssertSaw("  By URL  [No project]  press tab to switch\n");
    }

    static BoardQuestion Question(SetupBoard? current) => new(
        "Project",
        "The project of the tasks.",
        current,
        Boards,
        Owners,
        text => text.StartsWith("https://", StringComparison.Ordinal) ? null : "not an address",
        title => title.Length > 0 ? null : "no title");
}
