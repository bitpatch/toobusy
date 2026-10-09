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

    static Func<string, string?> Variables(params (string Name, string Value)[] variables) =>
        name => variables.Where(variable => variable.Name == name).Select(variable => variable.Value).FirstOrDefault();
}
