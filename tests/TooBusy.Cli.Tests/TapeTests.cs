using TooBusy.Cli.Terminal;

namespace TooBusy.Cli.Tests;

public sealed class TapeTests : IDisposable
{
    readonly TestTerminal terminal = new(width: 40, height: 8);

    public void Dispose() => terminal.Dispose();

    static Strip Foot(params string[] lines) => new([.. lines.Select(line => Line.Of(line))]);

    static Line[] Settled(params string[] lines) => [.. lines.Select(line => Line.Of(line))];

    static Tape Unroll(Page page)
    {
        page.Status = "Running";
        return page.Unroll();
    }

    [Fact]
    public void TheBarIsPrintedOnceAndWhatIsSettledStaysAboveTheFoot()
    {
        using var page = terminal.Open();
        var tape = Unroll(page);

        tape.Draw(Settled("✔ Export the data  1:24"), Foot("● Ship it", "  0:03 · Read a.cs"));
        tape.Draw(Settled("✔ Ship it  0:09"), Foot("● Describe the export", "  0:01 · starting"));

        Assert.Equal(
            [
                "",
                "  toobusy · ~/rocket           Running",
                "",
                "",
                " ✔ Export the data  1:24",
                " ✔ Ship it  0:09",
                " ● Describe the export",
                "   0:01 · starting",
            ],
            terminal.Tape.Rows);
        Assert.False(terminal.Tape.CursorShown);
        Assert.DoesNotContain("\u001b[?1049", terminal.Output.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void AFootThatGetsShorterLeavesNothingOfItselfBehind()
    {
        using var page = terminal.Open();
        var tape = Unroll(page);

        tape.Draw([], Foot("● Ship it", "  0:03 · waits for you", "  │ CSV or JSON?", "  claude attach 1a2b"));
        tape.Draw([], Foot("● Ship it", "  0:09 · Edit a.cs"));

        Assert.Equal([" ● Ship it", "   0:09 · Edit a.cs", "", ""], terminal.Tape.Rows.Skip(4));
    }

    [Fact]
    public void AFootOfOneLineIsDrawnOverItselfWithoutGoingUp()
    {
        using var page = terminal.Open();
        var tape = Unroll(page);

        tape.Draw(Settled("✔ Export the data  1:24"), Foot("● Reading the queue…"));
        tape.Draw([], Foot("● Checking the working tree…"));
        tape.Draw(Settled("✔ Ship it  0:09"), Strip.Empty);
        tape.Draw([], Foot("● Reading the queue…"));

        Assert.Equal([" ✔ Export the data  1:24", " ✔ Ship it  0:09", " ● Reading the queue…"], terminal.Tape.Rows.Skip(4));

        // Going up by no line is not said: a terminal would go up by one.
        Assert.DoesNotContain("\u001b[0A", terminal.Output.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain("\u001b[A", terminal.Output.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void WhatScrollsOutOfTheWindowStaysInTheHistoryOfTheTerminal()
    {
        using var page = terminal.Open();
        var tape = Unroll(page);

        for (var task = 1; task <= 30; task++)
            tape.Draw(Settled($"✔ Task {task}  0:0{task % 10}"), Foot("", $"● Task {task + 1}", "  0:00 · starting"));

        var rows = terminal.Tape.Rows;
        Assert.Equal("  toobusy · ~/rocket           Running", rows[1]);
        Assert.Equal(Enumerable.Range(1, 30).Select(task => $" ✔ Task {task}  0:0{task % 10}"), rows.Skip(4).Take(30));
        Assert.Equal(["", " ● Task 31", "   0:00 · starting"], rows.Skip(34));
    }

    [Fact]
    public void AFootTallerThanTheWindowLosesItsFirstLinesAndLeavesNoCopies()
    {
        using var page = terminal.Open();
        var tape = Unroll(page);
        var tall = Foot([.. Enumerable.Range(1, 10).Select(line => $"line {line}")]);
        Assert.Equal(7, tape.Room);

        tape.Draw([], tall);
        tape.Draw([], tall);
        tape.Draw(Settled("✔ Ship it  0:09"), tall);

        Assert.Equal([" ✔ Ship it  0:09", .. Enumerable.Range(4, 7).Select(line => $" line {line}")], terminal.Tape.Rows.Skip(4));
    }

    [Fact]
    public void ALineLongerThanTheWindowIsCutAndNeverWraps()
    {
        using var page = terminal.Open();
        var tape = Unroll(page);

        tape.Draw(Settled("✔ " + new string('a', 60)), Foot("● " + new string('b', 60)));

        Assert.Equal([" ✔ " + new string('a', 35) + "…", " ● " + new string('b', 35) + "…"], terminal.Tape.Rows.Skip(4));
        Assert.False(terminal.Tape.Wraps);
    }

    [Fact]
    public void TheMarkPulsesFromAPointToACircleWhereItStandsAndTheCursorComesBack()
    {
        var palette = Palette.Dark;
        using var page = terminal.Open(palette: palette);
        var tape = Unroll(page);

        tape.Draw(Settled("✔ Export the data  1:24"), new Strip([Line.Empty, Line.Of("● Ship it"), Line.Of("  0:03 · Read a.cs")], Mark: new Caret(1, 0)));
        var rests = terminal.Tape.Cursor.Row;
        var signs = new List<char>();
        for (var beat = 0; beat < 20; beat++)
        {
            terminal.Tick!();
            signs.Add(terminal.Tape.Rows[6][1]);
            Assert.Equal(rests, terminal.Tape.Cursor.Row);
        }

        // One blink: from the circle down to the point and up again.
        Assert.Equal("●●●•••·······•••●●●●", new string([.. signs]));
        Assert.Contains(palette.Accent("·"), terminal.Output.ToString(), StringComparison.Ordinal);
        Assert.Equal([" ✔ Export the data  1:24", "", " ● Ship it", "   0:03 · Read a.cs"], terminal.Tape.Rows.Skip(4));
    }

    [Fact]
    public void TheChosenLineAndTheCursorOfTheFootBlinkAsOnAScreen()
    {
        var palette = Palette.Dark;
        using var page = terminal.Open(palette: palette);
        var tape = Unroll(page);

        tape.Draw([], new Strip([new Line(new Part("❯ stop", Tone.Accent), new Part("  finish the task", Tone.Muted)), Line.Of("  abort"), Line.Empty], Caret: new Caret(2, 0), Chosen: 0));

        var written = terminal.Output.ToString();
        Assert.Contains("\u001b[2A\u001b[2G" + palette.Glow("❯ stop", 1) + "\r\u001b[2B", written, StringComparison.Ordinal);
        Assert.EndsWith("\r\u001b[2G" + palette.Cursor(" ", 1) + "\r", written, StringComparison.Ordinal);
    }

    [Fact]
    public void WithoutColoursNothingPulsesAndTheCursorOfTheTerminalStandsAtTheCaret()
    {
        using var page = terminal.Open();
        var tape = Unroll(page);
        var foot = new Strip([Line.Of("● Ship it"), Line.Of("  0:03 · Read a.cs"), Line.Of("press / to show the menu"), Line.Of("ctrl+c exit")], Mark: new Caret(0, 0), Caret: new Caret(2, 0));

        tape.Draw([], foot);
        var drawn = terminal.Output.ToString();
        terminal.Tick!();

        Assert.Equal(drawn, terminal.Output.ToString());
        Assert.True(terminal.Tape.CursorShown);
        Assert.Equal((6, 1), terminal.Tape.Cursor);

        // The foot is found again from where the cursor was left.
        tape.Draw(Settled("✔ Ship it  0:09"), foot);
        Assert.Equal([" ✔ Ship it  0:09", " ● Ship it", "   0:03 · Read a.cs", " press / to show the menu", " ctrl+c exit"], terminal.Tape.Rows.Skip(4));
        Assert.Equal((7, 1), terminal.Tape.Cursor);
    }

    [Fact]
    public void AWindowThatHasChangedIsDrawnAnewAtTheNextBeat()
    {
        var size = (Width: 40, Height: 8);
        terminal.Device = terminal.Device with { Size = () => size };
        using var page = terminal.Open(palette: Palette.Dark);
        var tape = Unroll(page);
        tape.Draw([], new Strip([Line.Of("● " + new string('a', 60))], Mark: new Caret(0, 0)));

        size = (30, 8);
        terminal.Tick!();

        Assert.Equal(" ● " + new string('a', 25) + "…", terminal.Tape.Rows[4]);
    }

    [Fact]
    public void ClosingErasesTheFootAndWhatIsWrittenNextStandsUnderTheLastLine()
    {
        var page = terminal.Open();
        var tape = Unroll(page);
        tape.Draw(Settled("✔ Export the data  1:24"), Foot("", "● Ship it", "  0:03 · Read a.cs"));
        Assert.Equal(Tape.Rescue, page.Held);

        tape.Draw(Settled("", "1 task in 1 min · 1 done"), Strip.Empty);
        tape.Close();
        tape.Close();
        Assert.Equal(Screen.Shown, page.Held);
        page.Dispose();
        terminal.Output.WriteLine("Demo: nothing was changed.");

        Assert.Equal([" ✔ Export the data  1:24", "", " 1 task in 1 min · 1 done", "Demo: nothing was changed.", ""], terminal.Tape.Rows.Skip(4));
        Assert.True(terminal.Tape.Wraps);
        Assert.True(terminal.Tape.CursorShown);
        Assert.Equal("", page.Held);
        Assert.DoesNotContain("\u001b[?1049", terminal.Output.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void TheNextFrameTakesTheTerminalBackFromTheTape()
    {
        using (var page = terminal.Open())
        {
            // The screen is left for the tape, and entered again for the frame that follows it.
            page.Draw([], [Line.Of("❯ Run")], "", chosen: 0);
            var tape = Unroll(page);
            Assert.False(terminal.Tape.Alternate);
            tape.Draw(Settled("✔ Export the data  1:24"), Foot("● Ship it"));

            page.Draw([], [Line.Of("❯ Run")], "", chosen: 0);
            Assert.True(tape.Closed);
            Assert.True(terminal.Tape.Alternate);
            Assert.Equal(Screen.Leave, page.Held);
        }

        Assert.False(terminal.Tape.Alternate);
        Assert.Equal([" ✔ Export the data  1:24", ""], terminal.Tape.Rows.Skip(4));
        Assert.EndsWith(Screen.Leave, terminal.Output.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void APageThatIsClosedUnderATapeRollsItUp()
    {
        var page = terminal.Open();
        var tape = Unroll(page);
        tape.Draw(Settled("✔ Export the data  1:24"), Foot("● Ship it", "  0:03 · Read a.cs"));

        page.Dispose();

        Assert.True(tape.Closed);
        Assert.Equal([" ✔ Export the data  1:24", "", ""], terminal.Tape.Rows.Skip(4));
        Assert.True(terminal.Tape.Wraps);
        Assert.True(terminal.Tape.CursorShown);
    }

    [Fact]
    public void WithColoursTheBarStandsOnItsGroundToTheEdgeOfTheWindow()
    {
        var palette = Palette.Dark;
        using var page = terminal.Open(palette: palette);
        Unroll(page);

        // The ground is what the terminal erases the rest of each line of the bar to.
        Assert.Equal(3, terminal.Output.ToString().Split(palette.Erase(Ground.Bar) + "\r\n").Length - 1);
        Assert.Contains(palette.Paint(Tone.Strong, "  toobusy · ~/rocket", Ground.Bar), terminal.Output.ToString(), StringComparison.Ordinal);
    }
}
