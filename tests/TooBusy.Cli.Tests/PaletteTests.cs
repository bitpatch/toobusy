using TooBusy.Cli.Terminal;

namespace TooBusy.Cli.Tests;

public class PaletteTests
{
    [Fact]
    public void WithoutATerminalThereIsNoColour()
    {
        Assert.Same(Palette.None, Palette.Detect(isTerminal: false, Variables(("COLORTERM", "truecolor"))));
    }

    [Fact]
    public void NoColorTurnsTheColourOff()
    {
        Assert.Same(Palette.None, Palette.Detect(isTerminal: true, Variables(("NO_COLOR", "1"), ("COLORTERM", "truecolor"))));
    }

    [Fact]
    public void ATruecolorTerminalIsDarkUnlessItSaysOtherwise()
    {
        Assert.Same(Palette.Dark, Palette.Detect(isTerminal: true, Variables(("COLORTERM", "truecolor"))));
        Assert.Same(Palette.Dark, Palette.Detect(isTerminal: true, Variables(("COLORTERM", "24bit"), ("COLORFGBG", "15;0"))));
        Assert.Same(Palette.Light, Palette.Detect(isTerminal: true, Variables(("COLORTERM", "truecolor"), ("COLORFGBG", "0;15"))));
    }

    [Fact]
    public void ATerminalWithoutTruecolorGetsTheSixteenColours()
    {
        Assert.Same(Palette.Basic, Palette.Detect(isTerminal: true, Variables()));
        Assert.Equal("\u001b[31m?\u001b[0m", Palette.Basic.Accent("?"));
    }

    [Fact]
    public void TheAccentIsCoralRed()
    {
        Assert.Equal("\u001b[38;2;212;80;63m?\u001b[0m", Palette.Dark.Accent("?"));
        Assert.Equal("\u001b[38;2;176;58;46m?\u001b[0m", Palette.Light.Accent("?"));
    }

    [Fact]
    public void WithoutColourEveryTextStaysAsItIs()
    {
        Assert.Equal("✘", Palette.None.Error("✘"));
        Assert.Equal("toobusy: run `toobusy init`", Palette.None.Message("toobusy: run `toobusy init`"));
    }

    [Fact]
    public void AMessageMutesTheNameOfTheToolAndAccentsCommands()
    {
        var message = Palette.Dark.Message("toobusy: run `toobusy init` or `toobusy doctor`, not a lone ` tick");

        Assert.Equal(
            $"{Palette.Dark.Muted("toobusy:")} run {Palette.Dark.Accent("`toobusy init`")} or {Palette.Dark.Accent("`toobusy doctor`")}, not a lone ` tick",
            message);
    }

    [Fact]
    public void ATextOnTheBarKeepsItsColourOverTheGrey()
    {
        Assert.Equal("\u001b[38;2;212;80;63;48;2;48;48;48m?\u001b[0m", Palette.Dark.Paint(Tone.Accent, "?", Ground.Bar));
        Assert.Equal("\u001b[1;48;2;48;48;48m?\u001b[0m", Palette.Dark.Paint(Tone.Strong, "?", Ground.Bar));
        Assert.Equal("\u001b[48;2;228;228;228m \u001b[0m", Palette.Light.Paint(Tone.Plain, " ", Ground.Bar));
    }

    [Fact]
    public void WithoutAGroundAPlainTextStaysAsItIsAndAStrongOneIsBold()
    {
        Assert.Equal("?", Palette.Dark.Paint(Tone.Plain, "?"));
        Assert.Equal("\u001b[1m?\u001b[0m", Palette.Dark.Paint(Tone.Strong, "?"));
    }

    [Fact]
    public void ATerminalOfSixteenColoursHasNoGroundUnderTheBar()
    {
        Assert.Equal(" ", Palette.Basic.Paint(Tone.Plain, " ", Ground.Bar));
        Assert.Equal("\u001b[31m?\u001b[0m", Palette.Basic.Paint(Tone.Accent, "?", Ground.Bar));
        Assert.Equal("?", Palette.None.Paint(Tone.Strong, "?", Ground.Bar));
    }

    [Fact]
    public void TheCursorFadesFromABlockInTheAccentToNothing()
    {
        Assert.Equal("\u001b[38;2;20;20;20;48;2;212;80;63mv\u001b[0m", Palette.Dark.Cursor("v", 1));
        Assert.Equal("\u001b[38;2;123;123;123;48;2;120;54;46mv\u001b[0m", Palette.Dark.Cursor("v", 0.5));
        Assert.Equal("v", Palette.Dark.Cursor("v", 0));
        Assert.Equal("\u001b[38;2;255;255;255;48;2;176;58;46m \u001b[0m", Palette.Light.Cursor(" ", 1));
        Assert.Equal(" ", Palette.Light.Cursor(" ", 0.01));
    }

    [Fact]
    public void TheChosenLineFadesFromTheAccentToTheColourOfAnyText()
    {
        Assert.Equal("\u001b[38;2;212;80;63m❯ Keep\u001b[0m", Palette.Dark.Glow("❯ Keep", 1));
        Assert.Equal("\u001b[38;2;219;153;144m❯ Keep\u001b[0m", Palette.Dark.Glow("❯ Keep", 0.5));
        Assert.Equal("❯ Keep", Palette.Dark.Glow("❯ Keep", 0));
        Assert.Equal("\u001b[38;2;176;58;46m❯ Keep\u001b[0m", Palette.Light.Glow("❯ Keep", 1));
        Assert.Equal("\u001b[38;2;103;44;38m❯ Keep\u001b[0m", Palette.Light.Glow("❯ Keep", 0.5));
        Assert.Equal("", Palette.Dark.Glow("", 1));
    }

    [Fact]
    public void InSixteenColoursTheBlinkHasTwoShadesAndWithoutColoursThereIsNone()
    {
        Assert.Equal("\u001b[7m \u001b[0m", Palette.Basic.Cursor(" ", 0.5));
        Assert.Equal(" ", Palette.Basic.Cursor(" ", 0.4));
        Assert.Equal("\u001b[31m❯ Keep\u001b[0m", Palette.Basic.Glow("❯ Keep", 0.5));
        Assert.Equal("❯ Keep", Palette.Basic.Glow("❯ Keep", 0.4));
        Assert.Equal("v", Palette.None.Cursor("v", 1));
        Assert.Equal("❯ Keep", Palette.None.Glow("❯ Keep", 1));
        Assert.False(Palette.None.DrawsCursor);
        Assert.True(Palette.Basic.DrawsCursor);
    }

    [Fact]
    public void TheChosenTabIsWhiteOnTheDeeperAccentAndTheOthersAreALittleQuieterThanAPlainText()
    {
        Assert.Equal("\u001b[1;38;2;255;255;255;48;2;176;58;46m By URL \u001b[0m", Palette.Dark.Paint(Tone.Quiet, " By URL ", Ground.Chosen));
        Assert.Equal("\u001b[1;38;2;255;255;255;48;2;176;58;46m By URL \u001b[0m", Palette.Light.Paint(Tone.Muted, " By URL ", Ground.Chosen));
        Assert.Equal("\u001b[7m By URL \u001b[0m", Palette.Basic.Paint(Tone.Plain, " By URL ", Ground.Chosen));
        Assert.Equal(" By URL ", Palette.None.Paint(Tone.Plain, " By URL ", Ground.Chosen));
        Assert.Equal("\u001b[38;2;176;176;176;48;2;48;48;48m By URL \u001b[0m", Palette.Dark.Paint(Tone.Quiet, " By URL ", Ground.Bar));
        Assert.Equal("\u001b[38;2;88;88;88mtab\u001b[0m", Palette.Light.Paint(Tone.Quiet, "tab"));
    }

    static Func<string, string?> Variables(params (string Name, string Value)[] variables) =>
        name => variables.Where(variable => variable.Name == name).Select(variable => variable.Value).FirstOrDefault();
}
