using TooBusy.Cli.Terminal;

namespace TooBusy.Cli.Tests;

public sealed class PageTests : IDisposable
{
    static readonly string Rule = " " + new string('─', 58);

    readonly TestTerminal terminal = new(width: 60, height: 12);

    public void Dispose() => terminal.Dispose();

    // The dots of a wait as they are drawn at a step of the light.
    static string Dots(Palette palette, int step) =>
        string.Concat(Enumerable.Range(0, Screen.Dots).Select(dot => palette.Spark("•", Screen.Spark(dot, Screen.Dots, step))));

    void Tick(int beats)
    {
        for (var beat = 0; beat < beats; beat++)
            terminal.Tick!();
    }

    [Fact]
    public void APageIsAScreenOfItsOwnThatGivesTheTerminalBack()
    {
        using (var page = terminal.Open())
        {
            // The screen is entered with the first thing that is drawn, and not before.
            Assert.Equal("", terminal.Output.ToString());
            Assert.True(terminal.ControlCTaken);

            page.Draw([], [], "");
            Assert.StartsWith("\u001b[?1049h", terminal.Output.ToString(), StringComparison.Ordinal);
            Assert.Equal(Screen.Leave, page.Held);
        }

        Assert.EndsWith("\u001b[?25h\u001b[?1049l", terminal.Output.ToString(), StringComparison.Ordinal);
        Assert.False(terminal.ControlCTaken);
    }

    [Fact]
    public void APageThatDrewNothingLeavesTheTerminalAsItIs()
    {
        using (var page = terminal.Open())
            Assert.Equal("", page.Held);

        Assert.Equal("", terminal.Output.ToString());
        Assert.False(terminal.ControlCTaken);
    }

    [Fact]
    public void TheCursorBlinksWhileThePageIsOpenAndIsGivenBackAsItWas()
    {
        using (var page = terminal.Open())
        {
            page.Draw([], [], "");
            Assert.StartsWith("\u001b[?1049h\u001b[1 q", terminal.Output.ToString(), StringComparison.Ordinal);
        }

        Assert.Contains("\u001b[0 q", terminal.Output.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void TheQuestionTheChoiceAndTheKeysAreRuledOffBetweenTheBarAndTheFoot()
    {
        using var page = terminal.Open();
        page.Body = [Line.Of("✔ Tracker GitHub")];
        page.Status = "Setting up · demo";
        page.Keys = "esc back";
        page.Foot = Line.Of("● Project ─ ○ Write");

        page.Draw([Line.Of("Project")], [Line.Of("❯ Keep"), Line.Of("  Change")], "↑↓ move", chosen: 0);

        Assert.Equal(
            [
                "",
                "  toobusy · ~/rocket                     Setting up · demo",
                "",
                "",
                " Project",
                Rule,
                " ❯ Keep",
                "   Change",
                Rule,
                " ↑↓ move · esc back · ctrl+c exit",
                "",
                " ● Project ─ ○ Write",
            ],
            terminal.Frame);
    }

    [Fact]
    public void WhereATextIsTypedABlockGlowsAndFadesAwayToNothing()
    {
        var palette = Palette.Dark;
        using var page = terminal.Open(palette: palette);

        page.Draw([Line.Of("Title")], [Line.Of("hint"), Line.Of("Rocket")], "", new Caret(1, 6));

        // After the text the block is an empty cell; the cursor of the terminal stays hidden.
        // Without a foot the choice ends three lines above the bottom; its columns start after one of the edge.
        Assert.EndsWith("\u001b[10;8H" + palette.Cursor(" ", 1), terminal.Output.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain("\u001b[?25h", terminal.Output.ToString(), StringComparison.Ordinal);
        Assert.Equal(TimeSpan.FromMilliseconds(80), terminal.Beat);

        Tick(5);
        Assert.EndsWith("\u001b[10;8H" + palette.Cursor(" ", 0.5), terminal.Output.ToString(), StringComparison.Ordinal);
        Tick(5);
        Assert.EndsWith("\u001b[10;8H ", terminal.Output.ToString(), StringComparison.Ordinal);
        Tick(10);
        Assert.EndsWith("\u001b[10;8H" + palette.Cursor(" ", 1), terminal.Output.ToString(), StringComparison.Ordinal);
        Assert.Equal(1, terminal.Frames);
    }

    [Fact]
    public void InsideATextTheBlockShowsTheCharacterItStandsOn()
    {
        var palette = Palette.Dark;
        using var page = terminal.Open(palette: palette);

        page.Draw([], [Line.Of("v.12")], "", new Caret(0, 1));

        Assert.EndsWith("\u001b[10;3H" + palette.Cursor(".", 1), terminal.Output.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void AKeyDoesNotStartTheBlinkAgain()
    {
        var palette = Palette.Dark;
        using var page = terminal.Open(palette: palette);
        page.Draw([], [Line.Of("Filter  moon"), Line.Of("❯ Moon base"), Line.Of("  Rocket")], "", new Caret(0, 12), chosen: 1);
        Tick(10);

        // The blink is at its faintest, and drawing the page with another line chosen leaves it there.
        page.Draw([], [Line.Of("Filter  moon"), Line.Of("  Moon base"), Line.Of("❯ Rocket")], "", new Caret(0, 12), chosen: 2);
        Assert.EndsWith("\u001b[10;2H❯ Rocket\u001b[8;14H ", terminal.Output.ToString(), StringComparison.Ordinal);

        Tick(10);
        Assert.EndsWith("\u001b[10;2H" + palette.Glow("❯ Rocket", 1) + "\u001b[8;14H" + palette.Cursor(" ", 1), terminal.Output.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void TheNameOfTheChosenLineFadesBetweenTheAccentAndTheColourOfAnyTextAndTheRestStays()
    {
        var palette = Palette.Dark;
        using var page = terminal.Open(palette: palette);

        page.Draw([], [Line.Of("  Keep"), new Line(new Part("❯ Change", Tone.Accent), new Part("  to another", Tone.Muted))], "", chosen: 1);

        Assert.Contains(palette.Muted("  to another"), terminal.Output.ToString(), StringComparison.Ordinal);
        Assert.EndsWith("\u001b[10;2H" + palette.Accent("❯ Change"), terminal.Output.ToString(), StringComparison.Ordinal);

        Tick(5);
        Assert.EndsWith("\u001b[10;2H" + palette.Glow("❯ Change", 0.5), terminal.Output.ToString(), StringComparison.Ordinal);
        Tick(5);
        Assert.EndsWith("\u001b[10;2H❯ Change", terminal.Output.ToString(), StringComparison.Ordinal);
        Assert.Equal(1, terminal.Frames);
    }

    [Fact]
    public void AListThatEndsWithBackNamesTheKeyThereAndNotAmongTheKeys()
    {
        using var page = terminal.Open();
        page.Keys = "esc back";

        page.Draw([], [Picker.BackRow(true)], "↑↓ move", chosen: 0, back: true);

        Assert.Equal([" ❯ Back  esc", Rule, " ↑↓ move · ctrl+c exit"], terminal.Frame[^3..]);
    }

    [Fact]
    public void TheNamesOfTheKeysAreALittleLighterThanWhatTheyDo()
    {
        var palette = Palette.Dark;
        using var page = terminal.Open(palette: palette);
        page.Keys = "esc back";

        page.Draw([], [], "↑↓ move");

        Assert.Contains(
            $"{palette.Paint(Tone.Quiet, "↑↓")}{palette.Muted(" move")}{palette.Muted(" · ")}{palette.Paint(Tone.Quiet, "esc")}{palette.Muted(" back")}{palette.Muted(" · ")}{palette.Paint(Tone.Quiet, "ctrl+c")}{palette.Muted(" exit")}",
            terminal.Output.ToString(),
            StringComparison.Ordinal);
    }

    [Fact]
    public void ThePulseStopsWithThePageAndWhenNothingWaitsForTheUser()
    {
        var page = terminal.Open(palette: Palette.Dark);
        page.Draw([], [Line.Of("Reading…")], "");
        var waiting = terminal.Output.ToString();
        terminal.Tick!();
        Assert.Equal(waiting, terminal.Output.ToString());

        page.Draw([], [Line.Of("❯ Keep")], "", chosen: 0);
        page.Dispose();
        var closed = terminal.Output.ToString();
        terminal.Tick!();
        Assert.Equal(closed, terminal.Output.ToString());
    }

    [Fact]
    public void WhileThePageWaitsALightRunsAlongTheDotsAndBack()
    {
        var palette = Palette.Dark;
        using var page = terminal.Open(palette: palette);

        page.Draw([], [Line.Of("Reading the milestones"), Line.Of("•••••••••••", Tone.Muted)], "", waiting: 1);

        Assert.Equal([" Reading the milestones", " •••••••••••"], terminal.Frame[8..10]);
        Assert.EndsWith("\u001b[10;2H" + Dots(palette, 0), terminal.Output.ToString(), StringComparison.Ordinal);

        // The light is on the first dot, in the accent, and the last one is in the colour of the rest.
        Assert.StartsWith(palette.Spark("•", 1), Dots(palette, 0), StringComparison.Ordinal);
        Assert.EndsWith(palette.Muted("•"), Dots(palette, 0), StringComparison.Ordinal);

        Tick(20);
        Assert.EndsWith("\u001b[10;2H" + Dots(palette, 20), terminal.Output.ToString(), StringComparison.Ordinal);
        Assert.EndsWith(palette.Spark("•", 1), Dots(palette, 20), StringComparison.Ordinal);
        Tick(20);
        Assert.EndsWith("\u001b[10;2H" + Dots(palette, 40), terminal.Output.ToString(), StringComparison.Ordinal);
        Assert.StartsWith(palette.Spark("•", 1), Dots(palette, 40), StringComparison.Ordinal);
        Assert.Equal(1, terminal.Frames);
    }

    [Fact]
    public void TheDotsOfAWaitMayStandAfterWhatTheLineSays()
    {
        var palette = Palette.Dark;
        using var page = terminal.Open(palette: palette);

        page.Draw([], [new Line(new Part("❯ Run", Tone.Muted), new Part("  •••••••••••", Tone.Muted)), Line.Of("  Exit")], "", waiting: 0);

        // The light runs along the dots only: they start seven columns into the line.
        Assert.EndsWith("\u001b[9;9H" + Dots(palette, 0), terminal.Output.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void APageThatShowsSomethingGoingOnHasTheDotsInItsQuestion()
    {
        var palette = Palette.Dark;
        using var page = terminal.Open(palette: palette);

        page.Draw([Line.Of("#3 Export the data · 1:24"), Line.Of("•••••••••••", Tone.Muted)], [Line.Of("/")], "", new Caret(0, 1), running: 1);

        // The light runs along the second line of the question, and the cursor of the line that is typed in stays.
        var dots = Array.IndexOf(terminal.Frame, " •••••••••••");
        Assert.Equal(" #3 Export the data · 1:24", terminal.Frame[dots - 1]);
        Assert.EndsWith(string.Create(System.Globalization.CultureInfo.InvariantCulture, $"\u001b[{dots + 1};2H") + Dots(palette, 0), terminal.Output.ToString(), StringComparison.Ordinal);
        Assert.Contains(palette.Cursor(" ", 1), terminal.Output.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task APageThatWaitsForAKeyGivesItAndGivesNothingOnceWhatItWaitsForIsDone()
    {
        terminal.Device = terminal.Device with { KeyWaiting = () => terminal.Keys.Waiting };
        using var page = terminal.Open();
        var wake = new TaskCompletionSource();

        var reading = page.ReadAsync(wake.Task);
        Assert.False(reading.IsCompleted);
        terminal.Keys.Press(Keys.Tab);
        Assert.Equal(ConsoleKey.Tab, (await reading)!.Value.Key);

        var woken = page.ReadAsync(wake.Task);
        wake.SetResult();
        Assert.Null(await woken);

        // A key that is pressed goes before what the page waits for.
        terminal.Keys.Press(Keys.Enter);
        Assert.Equal(ConsoleKey.Enter, (await page.ReadAsync(wake.Task))!.Value.Key);
    }

    [Fact]
    public void WhatLeavingDoesIsSaidAsThePageNamesIt()
    {
        terminal.Keys.Press(Keys.ControlC, Keys.Enter);
        using var page = terminal.Open();
        page.Leaving = "stop the session and exit";

        page.Draw([], [Line.Of("/")], "enter run");
        Assert.Equal(" enter run · ctrl+c stop the session and exit", terminal.Frame[^1]);

        page.Read();
        page.Draw([], [Line.Of("/")], "enter run");
        Assert.Equal(" press ctrl+c again to stop the session and exit", terminal.Frame[^1]);
    }

    [Fact]
    public void TheDotsTheLightHasLeftFadeBehindIt()
    {
        // Six beats on the light is on the fourth dot; those behind it are the dimmer the longer ago it left them,
        // and those ahead of it are not lit yet.
        double[] glows = [.. Enumerable.Range(0, Screen.Dots).Select(dot => Screen.Spark(dot, Screen.Dots, 6))];

        Assert.Equal(1, glows[3]);
        Assert.True(glows[3] > glows[2] && glows[2] > glows[1] && glows[1] > glows[0] && glows[0] > 0);
        Assert.All(glows[4..], glow => Assert.Equal(0, glow));
    }

    [Fact]
    public void TheLightTurnsAtTheLastDotWithItsTrailStillBehindIt()
    {
        // Two beats after the turn the light is on the tenth dot again, and the ninth one still fades from the way there.
        Assert.Equal(1, Screen.Spark(9, Screen.Dots, 22));
        Assert.True(Screen.Spark(10, Screen.Dots, 22) > Screen.Spark(8, Screen.Dots, 22));
        Assert.True(Screen.Spark(8, Screen.Dots, 22) > 0);
    }

    [Fact]
    public void WithoutColoursTheDotsStandStill()
    {
        using var page = terminal.Open();
        page.Draw([], [Line.Of("Reading the milestones"), Line.Of("•••••••••••", Tone.Muted)], "", waiting: 1);
        var waiting = terminal.Output.ToString();

        terminal.Tick!();

        Assert.Equal(waiting, terminal.Output.ToString());
        Assert.Contains(" •••••••••••", terminal.Frame);
    }

    [Fact]
    public async Task AWaitGivesWhatTheWorkGives()
    {
        using var page = terminal.Open();

        var (done, value) = await page.WaitAsync("Reading the milestones", _ => Task.FromResult("read"), TestContext.Current.CancellationToken);

        Assert.True(done);
        Assert.Equal("read", value);
        Assert.Equal([" Reading the milestones", " •••••••••••", " ❯ Back  esc", Rule, " enter choose · ctrl+c exit"], terminal.Frame[^5..]);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task BackStandsUnderTheDotsFromTheFirstMomentAndStopsTheWork(bool escape)
    {
        terminal.Device = terminal.Device with { KeyWaiting = () => terminal.Keys.Waiting };
        using var page = terminal.Open();
        var never = new TaskCompletionSource<string>();
        CancellationToken told = default;

        // The work never ends; `Back` is there before any key is pressed.
        var waiting = page.WaitAsync("Reading the milestones", stopping => { told = stopping; return never.Task; }, TestContext.Current.CancellationToken);
        Assert.Contains(" ❯ Back  esc", terminal.Frame);
        Assert.False(waiting.IsCompleted);

        terminal.Keys.Press(Keys.Down, escape ? Keys.Escape : Keys.Enter);
        var (done, value) = await waiting;

        Assert.False(done);
        Assert.Null(value);
        Assert.True(told.IsCancellationRequested);
    }

    [Fact]
    public async Task WhereEscapeLeavesThePageAWaitHasNoBackAndEscapeIsAskedTwice()
    {
        terminal.Device = terminal.Device with { KeyWaiting = () => terminal.Keys.Waiting };
        using var page = terminal.Open();
        page.EscapeLeaves = true;
        var never = new TaskCompletionSource<string>();

        var waiting = page.WaitAsync("Reading the milestones", _ => never.Task, TestContext.Current.CancellationToken);
        Assert.DoesNotContain(" ❯ Back  esc", terminal.Frame);

        // Enter is not a way back here, and one Escape only asks.
        terminal.Keys.Press(Keys.Enter, Keys.Escape);
        await Task.Delay(200, TestContext.Current.CancellationToken);
        Assert.False(waiting.IsCompleted);
        Assert.Equal(" press esc again to exit", terminal.Frame[^1]);

        terminal.Keys.Press(Keys.Escape);
        Assert.False((await waiting).Done);
    }

    [Fact]
    public async Task CtrlCTwiceLeavesAWaitToo()
    {
        terminal.Device = terminal.Device with { KeyWaiting = () => terminal.Keys.Waiting };
        using var page = terminal.Open();
        var never = new TaskCompletionSource<string>();

        var waiting = page.WaitAsync("Reading the milestones", _ => never.Task, TestContext.Current.CancellationToken);
        terminal.Keys.Press(Keys.ControlC, Keys.ControlC);

        await Assert.ThrowsAsync<OperationCanceledException>(() => waiting);
    }

    [Fact]
    public void WithoutColoursTheCursorOfTheTerminalStandsThereAndNothingPulses()
    {
        using var page = terminal.Open();
        page.Draw([], [Line.Of("❯ Keep")], "", chosen: 0);
        var chosen = terminal.Output.ToString();
        terminal.Tick!();
        Assert.EndsWith("\u001b[10;2H\u001b[?25h", chosen, StringComparison.Ordinal);
        Assert.Equal(chosen, terminal.Output.ToString());

        // Where a text is typed too, it stands in the text.
        page.Draw([], [Line.Of("Filter  moon"), Line.Of("❯ Moon base")], "", new Caret(0, 12), chosen: 1);
        Assert.EndsWith("\u001b[9;14H\u001b[?25h", terminal.Output.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void WhenNothingWaitsForTheUserTheCursorIsHidden()
    {
        using var page = terminal.Open();

        page.Draw([], [Line.Of("Reading…")], "");

        Assert.Null(terminal.Caret);
        Assert.DoesNotContain("\u001b[?25h", terminal.Output.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void AShortWindowKeepsTheEndOfTheBody()
    {
        using var page = terminal.Open();
        page.Body = [.. Enumerable.Range(1, 9).Select(number => Line.Of($"line {number}"))];

        page.Draw([Line.Of("Project")], [Line.Of("❯ Keep")], "");

        Assert.Equal(["", " line 7", " line 8", " line 9", " Project", Rule, " ❯ Keep", Rule], terminal.Frame[3..11]);
    }

    [Fact]
    public void ALineLongerThanTheWindowIsCut()
    {
        using var page = terminal.Open();
        page.Body = [Line.Of(new string('a', 80))];

        page.Draw([], [new Line(new Part("❯ "), new Part(new string('b', 80)))], "");

        Assert.Contains(" " + new string('a', 58) + "…", terminal.Frame);
        Assert.Contains(" ❯ " + new string('b', 56) + "…", terminal.Frame);
    }

    [Fact]
    public void WhenTheStatusDoesNotFitTheTitleStays()
    {
        using var page = terminal.Open();
        page.Status = "Setting up this project · demo · and a few words";

        page.Draw([], [], "");

        Assert.Equal("  toobusy · ~/rocket", terminal.Frame[1]);
    }

    [Fact]
    public void TheBarAndTheFootStandOnTheirGroundFromEdgeToEdge()
    {
        var palette = Palette.Dark;
        using var page = terminal.Open("toobusy", palette);
        page.Body = [Line.Of("done", Tone.Success)];
        page.Foot = Line.Of("● Project", Tone.Accent);

        page.Draw([Line.Of("Project", Tone.Strong)], [], "");

        var empty = palette.Paint(Tone.Plain, new string(' ', 60), Ground.Bar);
        var text = terminal.Output.ToString();
        Assert.Contains($"{empty}\u001b[K\r\n{palette.Paint(Tone.Strong, "  toobusy", Ground.Bar)}{palette.Paint(Tone.Plain, new string(' ', 51), Ground.Bar)}\u001b[K\r\n{empty}", text, StringComparison.Ordinal);
        Assert.Contains(palette.Paint(Tone.Success, "done"), text, StringComparison.Ordinal);
        Assert.Contains(palette.Paint(Tone.Strong, "Project"), text, StringComparison.Ordinal);
        Assert.Contains(palette.Paint(Tone.Muted, Rule), text, StringComparison.Ordinal);
        Assert.Contains(
            palette.Paint(Tone.Plain, " ", Ground.Bar) + palette.Paint(Tone.Accent, "● Project", Ground.Bar) + palette.Paint(Tone.Plain, new string(' ', 50), Ground.Bar),
            text,
            StringComparison.Ordinal);
    }

    [Fact]
    public void AFootThatDoesNotFitGoesOnToTheNextLine()
    {
        using var narrow = new TestTerminal(width: 20, height: 12);
        using var page = narrow.Open();
        page.Foot = new Line([.. Enumerable.Range(0, 9).Select(step => new Part(step == 0 ? "●" : "──○"))]);

        page.Draw([], [], "");

        Assert.Equal(["", " ●──○──○──○──○──○", " ──○──○──○"], narrow.Frame[^3..]);
    }

    [Fact]
    public void CtrlCAsksOnceAndLeavesOnTheSecond()
    {
        using var page = terminal.Open();
        terminal.Keys.Press(Keys.ControlC, Keys.ControlC);

        Assert.Equal(ConsoleKey.C, page.Read().Key);
        page.Draw([], [], "enter confirm");
        Assert.Equal(" press ctrl+c again to exit", terminal.Frame[^1]);
        Assert.Throws<OperationCanceledException>(() => page.Read());
    }

    [Fact]
    public void AnyOtherKeyTakesTheQuestionBack()
    {
        using var page = terminal.Open();
        terminal.Keys.Press(Keys.ControlC, Keys.Down, Keys.ControlC);

        page.Read();
        page.Read();
        page.Draw([], [], "enter confirm");

        Assert.Equal(" enter confirm · ctrl+c exit", terminal.Frame[^1]);
        Assert.Equal(ConsoleKey.C, page.Read().Key);
    }

    [Fact]
    public void WhereEscapeLeavesItAsksOnceTooAndTheSecondOneIsGivenToTheQuestion()
    {
        using var page = terminal.Open();
        page.EscapeLeaves = true;
        terminal.Keys.Press(Keys.Escape, Keys.Escape, Keys.Escape, Keys.Down, Keys.Escape, Keys.ControlC, Keys.Escape);

        Assert.Equal(ConsoleKey.NoName, page.Read().Key);
        page.Draw([], [], "");
        Assert.Equal(" press esc again to exit", terminal.Frame[^1]);
        Assert.Equal(ConsoleKey.Escape, page.Read().Key);

        // Any other key takes the question back, and Ctrl+C asks its own.
        Assert.Equal(ConsoleKey.NoName, page.Read().Key);
        Assert.Equal(ConsoleKey.DownArrow, page.Read().Key);
        Assert.Equal(ConsoleKey.NoName, page.Read().Key);
        Assert.Equal(ConsoleKey.C, page.Read().Key);
        Assert.Equal(ConsoleKey.NoName, page.Read().Key);
    }

    [Fact]
    public void WhereEscapeOnlyGoesBackItIsAKeyLikeAnyOther()
    {
        using var page = terminal.Open();
        terminal.Keys.Press(Keys.Escape);

        Assert.Equal(ConsoleKey.Escape, page.Read().Key);
        page.Draw([], [], "");
        Assert.Equal(" ctrl+c exit", terminal.Frame[^1]);
    }

    [Fact]
    public void WhileKeysAreWaitingNothingIsDrawn()
    {
        terminal.Device = terminal.Device with { KeyWaiting = () => terminal.Keys.Waiting };
        using var page = terminal.Open();
        terminal.Keys.Type("ab");

        page.Draw([], [Line.Of("a")], "");
        page.Read();
        page.Draw([], [Line.Of("ab")], "");
        page.Read();
        page.Draw([], [Line.Of("abc")], "");

        Assert.Equal(1, terminal.Frames);
        Assert.Contains(" abc", terminal.Frame);
    }

    [Fact]
    public void AChangeOfTheSizeDrawsTheFrameAgain()
    {
        using var page = terminal.Open();
        page.Draw([Line.Of("Project")], [], "");

        terminal.Resize!();

        Assert.Equal(2, terminal.Frames);
        Assert.Contains(" Project", terminal.Frame);
    }
}
