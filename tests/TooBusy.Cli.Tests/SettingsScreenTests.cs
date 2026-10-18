using TooBusy.Cli.Terminal;
using TooBusy.Core.Setup;

namespace TooBusy.Cli.Tests;

public sealed class SettingsScreenTests : IDisposable
{
    static readonly SetupValue[] Values =
    [
        new(SetupSetting.Project, "Project", "https://github.com/orgs/acme/projects/1", SetupLook.Set),
        new(SetupSetting.Statuses, "Statuses", "Todo: Ready · In Progress: Doing · Done: Shipped", SetupLook.Set),
        new(SetupSetting.Blocking, "Blocking labels", "manual, draft", SetupLook.KeepsOut),
        new(SetupSetting.Take, "Labels to take", "any task", SetupLook.Set),
        new(SetupSetting.Manual, "Manual label", "manual", SetupLook.Set),
        new(SetupSetting.Interrupted, "Interrupt label", "interrupted", SetupLook.Set),
        new(SetupSetting.Skill, "Skill", "task-auto", SetupLook.Set),
        new(SetupSetting.Before, "Before a task", "none", SetupLook.Unset),
        new(SetupSetting.After, "After a task", "none", SetupLook.Unset),
        new(SetupSetting.Branch, "Branch", "develop", SetupLook.Set),
        new(SetupSetting.PermissionMode, "Permission mode", "auto by default", SetupLook.Set),
    ];

    readonly TestTerminal terminal = new(width: 80, height: 26);

    public void Dispose() => terminal.Dispose();

    [Fact]
    public void EverySettingStandsWithItsValueAndNoneScrolls()
    {
        terminal.Keys.Press(Keys.Down, Keys.Down, Keys.Down, Keys.Enter);

        Assert.Equal(SetupSetting.Take, Ask());
        terminal.AssertSaw("""
             Settings
             Choose a setting to change it: a change is saved when you say so.
            """.ReplaceLineEndings("\n"));
        terminal.AssertSaw("""
               Project          https://github.com/orgs/acme/projects/1
               Statuses         Todo: Ready · In Progress: Doing · Done: Shipped
               Blocking labels  manual, draft
             ❯ Labels to take   any task
               Manual label     manual
               Interrupt label  interrupted
               Skill            task-auto
               Before a task    none
               After a task     none
               Branch           develop
               Permission mode  auto by default
               Back             esc
             ↑↓ move · enter choose · ctrl+c exit
            """.ReplaceLineEndings("\n").TrimEnd(' '));
        Assert.DoesNotContain("more", terminal.Text, StringComparison.Ordinal);
    }

    [Fact]
    public void TheBlockingLabelsAreInTheColourOfAnErrorAndNoneIsMuted()
    {
        var palette = Palette.Dark;
        terminal.Keys.Press(Keys.Escape);

        using (var page = terminal.Open(palette: palette))
            SettingsScreen.Ask(page, Values);

        var output = terminal.Output.ToString();
        Assert.Contains(palette.Paint(Tone.Error, "  manual, draft"), output, StringComparison.Ordinal);
        Assert.Contains(palette.Paint(Tone.Muted, "  none"), output, StringComparison.Ordinal);
        Assert.Contains(palette.Paint(Tone.Success, "  task-auto"), output, StringComparison.Ordinal);
    }

    [Fact]
    public void ThePointerStartsOnTheGivenRow()
    {
        terminal.Keys.Press(Keys.Enter);

        Assert.Equal(SetupSetting.Branch, Ask(at: SetupSetting.Branch));
        terminal.AssertSaw(" ❯ Branch           develop");
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void EscapeAndTheLastRowGoBack(bool row)
    {
        terminal.Keys.Press(row ? [Keys.Up, Keys.Enter] : [Keys.Escape]);

        Assert.Null(Ask());
    }

    SetupSetting? Ask(SetupSetting at = SetupSetting.Project)
    {
        using var page = terminal.Open();
        return SettingsScreen.Ask(page, Values, at);
    }
}
