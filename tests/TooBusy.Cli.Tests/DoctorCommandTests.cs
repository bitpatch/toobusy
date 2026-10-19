using TooBusy.Cli.Terminal;

namespace TooBusy.Cli.Tests;

// `toobusy doctor`: the checks of everything a run needs.
public sealed class DoctorCommandTests : CliAppTests
{
    [Fact]
    public async Task DoctorTellsEveryCheckOfAProjectThatIsReady()
    {
        GitRepository();
        WriteSettings(Settings.Replace("type = \"github\"", "type = \"github\"\nboard = \"https://github.com/orgs/acme/projects/1\"", StringComparison.Ordinal));
        var processes = GitHub(milestones: "", Labels);

        var (exit, output, error) = await RunAsync(Context(processes), ["doctor"]);

        Assert.Equal(0, exit);
        Assert.Equal("", error);
        Assert.Equal(
            """
            ✔ git
            ✔ GitHub CLI
            ✔ GitHub login
            ✔ Claude Code
            ✔ Claude Code login
            ✔ settings           .toobusy/settings.toml
            ✔ repository         acme/rocket
            ✔ board              https://github.com/orgs/acme/projects/1
            ✔ statuses           Todo, In Progress, Done
            ✔ labels
            ○ skill              the project names no skill

            """.ReplaceLineEndings(),
            output);
        Assert.Contains(processes.Asked, asked => asked.Command == "claude" && asked.Arguments.SequenceEqual(["auth", "status"]));
        Assert.Contains(processes.Asked, asked => asked.Command == "gh" && asked.Arguments.SequenceEqual(["api", "repos/acme/rocket", "--jq", ".full_name"]));
    }

    [Fact]
    public async Task DoctorLooksForTheSkillOfTheSettingsAmongTheSkillsOfTheProject()
    {
        GitRepository();
        WriteSettings(Settings + SkillOfTheProject);
        var processes = GitHub(milestones: "", Labels);
        var place = Path.Combine(".claude", "skills", "task-auto", "SKILL.md");

        var (exit, output, _) = await RunAsync(Context(processes), ["doctor"]);

        Assert.Equal(1, exit);
        Assert.EndsWith(
            "✘ skill              There is no skill “task-auto”: a session that starts with it would do nothing." + Environment.NewLine
            + $"  fix: write {place}, as `toobusy about` tells, or name no skill: toobusy init --no-skill" + Environment.NewLine,
            output,
            StringComparison.Ordinal);

        WriteSkill("task-auto");
        (exit, output, _) = await RunAsync(Context(processes), ["doctor"]);

        Assert.Equal(0, exit);
        Assert.EndsWith($"✔ skill              {place}" + Environment.NewLine, output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task DoctorLooksForTheSkillOfTheSettingsAmongTheSkillsOfTheUserWhereClaudeConfigDirPutsThem()
    {
        GitRepository();
        WriteSettings(Settings + SkillOfTheProject);
        var processes = GitHub(milestones: "", Labels);
        var elsewhere = personal.CreateSubdirectory("elsewhere");
        var place = Path.Combine(elsewhere.CreateSubdirectory(Path.Combine("skills", "task-auto")).FullName, "SKILL.md");
        File.WriteAllText(place, "---\nname: task-auto\n---\n");

        var (exit, output, _) = await RunAsync(Context(processes), ["doctor"]);

        Assert.Equal(1, exit);
        Assert.Contains("✘ skill              There is no skill “task-auto”", output, StringComparison.Ordinal);

        (exit, output, _) = await RunAsync(Context(processes) with { Variables = name => name == "CLAUDE_CONFIG_DIR" ? elsewhere.FullName : null }, ["doctor"]);

        Assert.Equal(0, exit);
        Assert.EndsWith($"✔ skill              {place}" + Environment.NewLine, output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task DoctorOnAMachineWithNothingFailsEveryToolAndSkipsWhatWaitsForThem()
    {
        GitRepository();

        var (exit, output, error) = await RunAsync("doctor");

        Assert.Equal(1, exit);
        Assert.Equal("", error);
        var lines = output.ReplaceLineEndings("\n").Split('\n');
        Assert.Equal("✘ git                `git` is not installed.", lines[0]);
        Assert.StartsWith("  fix: ", lines[1], StringComparison.Ordinal);
        Assert.Equal("✘ GitHub CLI         The GitHub command-line tool `gh` is not installed.", lines[2]);
        Assert.Equal("○ GitHub login       waits for the GitHub CLI", lines[4]);
        Assert.Equal("✘ Claude Code        Claude Code is not installed: `claude` is not on the path.", lines[5]);
        Assert.Equal("○ Claude Code login  waits for Claude Code", lines[7]);
        Assert.Equal(
            [
                "✘ settings           This project is not set up yet.",
                "  fix: toobusy init",
                "○ repository         waits for git",
                "○ board              waits for the settings",
                "○ statuses           waits for the settings",
                "○ labels             waits for the settings",
                "○ skill              waits for the settings",
                "",
            ],
            lines[8..]);
    }

    [Fact]
    public async Task DoctorTellsWhatIsWrongWithSettingsThatDoNotValidate()
    {
        GitRepository();
        WriteSettings("version = 2\n");

        var (exit, output, _) = await RunAsync(Context(GitHub(milestones: "")), ["doctor"]);

        Assert.Equal(1, exit);
        Assert.Contains("✘ settings           .toobusy/settings.toml does not validate:" + Environment.NewLine + "  .toobusy/settings.toml:1: version: ", output, StringComparison.Ordinal);
        Assert.Contains("✔ repository         acme/rocket", output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task DoctorPaintsTheMarksAndWhatIsWrong()
    {
        GitRepository();
        var palette = Palette.Dark;

        var (_, output, _) = await RunAsync(Context(GitHub(milestones: "")) with { OutputPalette = palette }, ["doctor"]);

        Assert.Contains($"{palette.Success("✔")} git" + Environment.NewLine, output, StringComparison.Ordinal);
        Assert.Contains($"{palette.Error("✘")} settings           {palette.Error("This project is not set up yet.")}" + Environment.NewLine, output, StringComparison.Ordinal);
        Assert.Contains($"  {palette.Muted("fix: toobusy init")}" + Environment.NewLine, output, StringComparison.Ordinal);
        Assert.Contains($"{palette.Muted("○")} board              {palette.Muted("waits for the settings")}" + Environment.NewLine, output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task InATerminalDoctorShowsTheCheckThatRunsAndPutsItsResultInItsPlace()
    {
        GitRepository();
        WriteSettings(Settings);

        var (exit, output, _) = await RunWithKeysAsync(new Keys(), GitHub(milestones: "", Labels), "doctor");

        Assert.Equal(0, exit);
        Assert.Contains("\r⣾ GitHub login\r\u001b[K✔ GitHub login" + Environment.NewLine, output, StringComparison.Ordinal);
        Assert.Equal(
            ["✔ git", "✔ GitHub CLI", "✔ GitHub login", "✔ Claude Code", "✔ Claude Code login", "✔ settings           .toobusy/settings.toml", "✔ repository         acme/rocket", "○ board              the project has no board", "○ statuses           the project has no board", "✔ labels", "○ skill              the project names no skill"],
            Scroll.Read(output).Rows.Where(row => row.Length > 0));
    }

    [Fact]
    public async Task DoctorOfADemoChecksAMadeUpMachineThatHasItAll()
    {
        GitRepository();

        var (exit, output, _) = await RunAsync("doctor", "--demo");

        Assert.Equal(0, exit);
        Assert.DoesNotContain("✘", output, StringComparison.Ordinal);
        Assert.Contains("✔ labels" + Environment.NewLine + "✔ skill              .claude/skills/task-auto/SKILL.md" + Environment.NewLine + "Demo: the checks were made up." + Environment.NewLine, output, StringComparison.Ordinal);
    }
}
