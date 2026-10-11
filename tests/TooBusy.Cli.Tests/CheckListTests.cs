using TooBusy.Cli.Terminal;
using TooBusy.Core.Doctor;

namespace TooBusy.Cli.Tests;

public sealed class CheckListTests : IDisposable
{
    readonly StringWriter output = new();
    Action? tick;

    public void Dispose() => output.Dispose();

    [Fact]
    public void TheSignOfTheCheckThatRunsTurnsWithTheBeatAndItsResultTakesItsLine()
    {
        var palette = Palette.Dark;
        using var list = new CheckList(output, palette, output, terminal: Terminal());

        list.Start("GitHub login");
        tick!();
        tick!();
        list.Done(new CheckResult("GitHub login", CheckState.Passed));

        Assert.Equal(
            $"\r{palette.Accent("⣾")} GitHub login\r{palette.Accent("⣷")} GitHub login\r{palette.Accent("⣯")} GitHub login\r\u001b[K{palette.Success("✔")} GitHub login{Environment.NewLine}",
            output.ToString());

        // Between two checks nothing turns.
        tick!();
        Assert.EndsWith(Environment.NewLine, output.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void WithoutColoursTheSignStandsStill()
    {
        using var list = new CheckList(output, Palette.None, output, terminal: Terminal());

        list.Start("git");

        Assert.Null(tick);
        Assert.Equal("\r⣾ git", output.ToString());
    }

    [Fact]
    public void WithoutATerminalOnlyTheResultsAreWritten()
    {
        using var list = new CheckList(output, Palette.None, output);

        list.Start("settings");
        list.Done(new CheckResult("settings", CheckState.Failed, ".toobusy/settings.toml does not validate:") { Details = [".toobusy/settings.toml:1: version: must be 1"] });
        list.Done(new CheckResult("board", CheckState.Failed, "Your GitHub login cannot work with projects.", "gh auth refresh -s project"));
        list.Done(new CheckResult("labels", CheckState.Skipped, "waits for the settings"));

        Assert.Equal(
            """
            ✘ settings           .toobusy/settings.toml does not validate:
              .toobusy/settings.toml:1: version: must be 1
            ✘ board              Your GitHub login cannot work with projects.
              fix: gh auth refresh -s project
            ○ labels             waits for the settings

            """.ReplaceLineEndings(),
            output.ToString());
    }

    [Fact]
    public void AListOfFailuresShowsWhatRunsWhereItIsWatchedAndTellsTheFailuresWhereErrorsGo()
    {
        using var errors = new StringWriter();
        using (var list = new CheckList(errors, Palette.None, output, Palette.None, Terminal(), failuresOnly: true))
        {
            list.Start("git");
            list.Done(new CheckResult("git", CheckState.Passed));
            list.Done(new CheckResult("GitHub login", CheckState.Skipped, "waits for the GitHub CLI"));
            list.Start("Claude Code");
            list.Done(new CheckResult("Claude Code", CheckState.Failed, "Claude Code is not installed: `claude` is not on the path.", "curl -fsSL https://claude.ai/install.sh | bash"));
            list.Start("labels");
        }

        Assert.Equal("\r⣾ git\r\u001b[K\r⣾ Claude Code\r\u001b[K\r⣾ labels\r\u001b[K", output.ToString());
        Assert.Equal(
            """
            ✘ Claude Code        Claude Code is not installed: `claude` is not on the path.
              fix: curl -fsSL https://claude.ai/install.sh | bash

            """.ReplaceLineEndings(),
            errors.ToString());
    }

    TerminalDevice Terminal() => new(() => default, () => (120, 30))
    {
        Every = (_, beat) =>
        {
            tick = beat;
            return null;
        },
    };
}
