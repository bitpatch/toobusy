using TooBusy.Core.Processes;

namespace TooBusy.Cli.Tests;

// `toobusy model`, `toobusy effort` and `toobusy milestone`: the choices of the user, from a script and from a list.
public sealed class ChoiceCommandTests : CliAppTests
{
    [Fact]
    public async Task AModelIsChosenByItsNameFromAScript()
    {
        GitRepository();

        var (exit, output, error) = await RunAsync("model", " claude-opus-4 ");

        Assert.Equal(0, exit);
        Assert.Equal("", error);
        Assert.Equal("Model: claude-opus-4" + Environment.NewLine, output);
        Assert.Equal("Model: claude-opus-4" + Environment.NewLine, (await RunAsync("model")).Output);
        Assert.Contains("model = \"claude-opus-4\"", File.ReadAllText(Path.Combine(personal.FullName, "projects.toml")), StringComparison.Ordinal);
    }

    [Fact]
    public async Task TheAssistantsOwnModelIsChosenFromAScriptToo()
    {
        GitRepository();

        var (exit, output, _) = await RunAsync("model", "--default");

        Assert.Equal(0, exit);
        Assert.Equal("Model: the assistant's own." + Environment.NewLine, output);
        Assert.Equal("Model: the assistant's own" + Environment.NewLine, (await RunAsync("model")).Output);
        Assert.Contains("model = \"\"", File.ReadAllText(Path.Combine(personal.FullName, "projects.toml")), StringComparison.Ordinal);
    }

    [Fact]
    public async Task ANameAndDefaultTogetherAreAWrongCommandLine()
    {
        GitRepository();

        var (exit, _, error) = await RunAsync("model", "opus", "--default");

        Assert.Equal(2, exit);
        Assert.Contains("not both", error, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AnEmptyModelNameIsRefused()
    {
        GitRepository();

        var (exit, output, error) = await RunAsync("model", " ");

        Assert.Equal(1, exit);
        Assert.Equal("", output);
        Assert.Equal("toobusy: the name of the model is empty" + Environment.NewLine, error);
        Assert.False(File.Exists(Path.Combine(personal.FullName, "projects.toml")));
    }

    [Fact]
    public async Task WithoutATerminalAModelThatIsNotChosenSaysHowToChooseIt()
    {
        GitRepository();

        var (exit, output, _) = await RunAsync("model");

        Assert.Equal(0, exit);
        Assert.Equal(
            "Model: not chosen" + Environment.NewLine + "Choose it with `toobusy model <name>` or `toobusy model --default`." + Environment.NewLine,
            output);
    }

    [Fact]
    public async Task InATerminalTheModelIsChosenFromAList()
    {
        GitRepository();

        var (exit, output, _) = await RunWithKeysAsync(new Keys().Press(Keys.Up, Keys.Enter).Type("claude-opus-4").Press(Keys.Enter), "model");

        Assert.Equal(0, exit);
        Assert.Contains("Choosing the model", output, StringComparison.Ordinal);
        Assert.Equal(
            $"toobusy · ~{Path.DirectorySeparatorChar}{folder.Name}{Environment.NewLine}✔ Model            claude-opus-4{Environment.NewLine}",
            Report(output));
        Assert.Equal("Model: claude-opus-4", (await RunAsync("model")).Output.Trim());
    }

    [Fact]
    public async Task LeavingTheQuestionAboutTheModelLeavesItAsItWas()
    {
        GitRepository();
        await RunAsync("model", "opus");

        var (exit, output, _) = await RunWithKeysAsync(new Keys().Press(Keys.Escape, Keys.Escape), "model");

        Assert.Equal(0, exit);
        Assert.EndsWith("The model is left as it was." + Environment.NewLine, Report(output), StringComparison.Ordinal);
        Assert.Equal("Model: opus", (await RunAsync("model")).Output.Trim());
    }

    [Fact]
    public async Task ADemoRemembersNoModelAndNoEffort()
    {
        var (exit, output, _) = await RunAsync("model", "opus", "--demo");
        var (_, effort, _) = await RunAsync("effort", "max", "--demo");
        var (_, page, _) = await RunWithKeysAsync(new Keys().Press(Keys.Enter), "effort", "--demo");

        Assert.Equal(0, exit);
        Assert.Equal("Model: opus" + Environment.NewLine, output);
        Assert.Equal("Effort: max" + Environment.NewLine, effort);
        Assert.EndsWith($"✔ Effort           high{Environment.NewLine}Demo: what was chosen is not remembered.{Environment.NewLine}", Report(page), StringComparison.Ordinal);
        Assert.False(File.Exists(Path.Combine(personal.FullName, "projects.toml")));
    }

    [Fact]
    public async Task AnEffortIsChosenByItsLevelFromAScript()
    {
        GitRepository();

        var (exit, output, error) = await RunAsync("effort", "XHigh");

        Assert.Equal(0, exit);
        Assert.Equal("", error);
        Assert.Equal("Effort: xhigh" + Environment.NewLine, output);
        Assert.Equal("Effort: xhigh" + Environment.NewLine, (await RunAsync("effort")).Output);
    }

    [Fact]
    public async Task AnEffortThatIsNotALevelIsRefusedWithTheLevels()
    {
        GitRepository();

        var (exit, output, error) = await RunAsync("effort", "ultra");

        Assert.Equal(1, exit);
        Assert.Equal("", output);
        Assert.Equal("toobusy: there is no effort “ultra”." + Environment.NewLine + "The levels: low, medium, high, xhigh, max" + Environment.NewLine, error);
    }

    [Fact]
    public async Task WithoutATerminalAnEffortThatIsNotChosenSaysHowToChooseIt()
    {
        GitRepository();

        var (exit, output, _) = await RunAsync("effort");

        Assert.Equal(0, exit);
        Assert.Equal(
            "Effort: not chosen" + Environment.NewLine + "Choose it with `toobusy effort <level>`: low, medium, high, xhigh, max." + Environment.NewLine,
            output);
    }

    [Fact]
    public async Task InATerminalTheEffortIsChosenFromAList()
    {
        GitRepository();

        var (exit, output, _) = await RunWithKeysAsync(new Keys().Press(Keys.Down, Keys.Enter), "effort");

        Assert.Equal(0, exit);
        Assert.Contains("❯ high", output, StringComparison.Ordinal);
        Assert.Equal(
            $"toobusy · ~{Path.DirectorySeparatorChar}{folder.Name}{Environment.NewLine}✔ Effort           xhigh{Environment.NewLine}",
            Report(output));
        Assert.Equal("Effort: xhigh", (await RunAsync("effort")).Output.Trim());
    }

    [Fact]
    public async Task AMilestoneIsChosenByItsTitleFromAScript()
    {
        GitRepository();
        var processes = GitHub(milestones: "1\tv0.2.0\t2030-01-15T08:00:00Z\t12\n2\tBacklog\t\t1\n");

        var (exit, output, error) = await RunAsync(Context(processes), ["milestone", "V0.2.0"]);

        Assert.Equal(0, exit);
        Assert.Equal("", error);
        Assert.Equal("Milestone: v0.2.0 · due 2030-01-15 · 12 open tasks" + Environment.NewLine, output);
        Assert.Contains(processes.Asked, asked => asked.Command == "gh" && asked.Arguments[1] == "repos/acme/rocket/milestones?state=open&per_page=100");
        Assert.Equal(("Milestone: v0.2.0" + Environment.NewLine, 0), await ShownAsync(processes));
    }

    [Fact]
    public async Task AMilestoneThatIsNotOpenIsRefusedWithTheOpenOnes()
    {
        GitRepository();
        var processes = GitHub(milestones: "1\tv0.3.0\t\t1\n2\tv0.2.0\t\t2\n");

        var (exit, output, error) = await RunAsync(Context(processes), ["milestone", "v0.1.0"]);

        Assert.Equal(1, exit);
        Assert.Equal("", output);
        Assert.Equal("toobusy: there is no open milestone “v0.1.0”." + Environment.NewLine + "The open ones: v0.2.0, v0.3.0" + Environment.NewLine, error);
        Assert.Equal("Milestone: not chosen", (await ShownAsync(processes)).Output.Split(Environment.NewLine)[0]);
    }

    [Fact]
    public async Task WithoutMilestonesToReadATitleCannotBeChosen()
    {
        GitRepository();

        var (exit, _, error) = await RunAsync("milestone", "v1");

        Assert.Equal(1, exit);
        Assert.Equal("toobusy: the `origin` remote is not a GitHub repository, so there are no milestones to choose from" + Environment.NewLine, error);
    }

    [Fact]
    public async Task MilestonesThatCannotBeReadRefuseATitle()
    {
        GitRepository();
        var processes = new ScriptedProcesses(command => command == "git"
            ? new ProcessResult(ProcessStatus.Exited, 0, "https://github.com/acme/rocket.git\n", "")
            : new ProcessResult(ProcessStatus.Exited, 1, "", "HTTP 502"));

        var (exit, output, error) = await RunAsync(Context(processes), ["milestone", "v1"]);

        Assert.Equal(1, exit);
        Assert.Equal("", output);
        Assert.Equal("toobusy: the milestones of acme/rocket cannot be read" + Environment.NewLine, error);
    }

    [Fact]
    public async Task ARepositoryWithoutOpenMilestonesRefusesATitleAndSaysSo()
    {
        GitRepository();

        var (exit, output, error) = await RunAsync(Context(GitHub(milestones: "")), ["milestone", "v1"]);

        Assert.Equal(1, exit);
        Assert.Equal("", output);
        Assert.Equal("toobusy: there is no open milestone “v1”." + Environment.NewLine + "The repository has no open milestones." + Environment.NewLine, error);
    }

    [Fact]
    public async Task WorkingWithoutAMilestoneIsChosenFromAScriptToo()
    {
        GitRepository();

        var (exit, output, _) = await RunAsync("milestone", "--none");

        Assert.Equal(0, exit);
        Assert.Equal("Milestone: none. Tasks are taken whatever their milestone." + Environment.NewLine, output);
        Assert.Equal(("Milestone: no milestone" + Environment.NewLine, 0), await ShownAsync(null));
        Assert.Contains("milestone = \"\"", File.ReadAllText(Path.Combine(personal.FullName, "projects.toml")), StringComparison.Ordinal);
    }

    [Fact]
    public async Task ATitleAndNoneTogetherAreAWrongCommandLine()
    {
        GitRepository();

        var (exit, _, error) = await RunAsync("milestone", "v1", "--none");

        Assert.Equal(2, exit);
        Assert.Contains("not both", error, StringComparison.Ordinal);
    }

    [Fact]
    public async Task InATerminalTheMilestoneIsChosenFromAList()
    {
        GitRepository();
        var processes = GitHub(milestones: "1\tv0.3.0\t\t1\n2\tv0.2.0\t\t2\n");

        var (exit, output, _) = await RunWithKeysAsync(new Keys().Press(Keys.Down, Keys.Enter), processes, "milestone");

        Assert.Equal(0, exit);
        Assert.Contains("❯ v0.2.0", output, StringComparison.Ordinal);
        Assert.Equal(
            $"toobusy · ~{Path.DirectorySeparatorChar}{folder.Name}{Environment.NewLine}✔ Milestone        v0.3.0{Environment.NewLine}",
            Report(output));
        Assert.Equal("Milestone: v0.3.0", (await ShownAsync(processes)).Output.Trim());
    }

    [Fact]
    public async Task ADemoRemembersNoMilestone()
    {
        var (exit, output, _) = await RunAsync("milestone", "--none", "--demo");
        var (_, chosen, _) = await RunAsync("milestone", "v1.1.0", "--demo");
        var (_, shown, _) = await RunAsync("milestone", "--demo");

        // The milestones are the made-up ones of the demo.
        Assert.Equal(0, exit);
        Assert.StartsWith("Milestone: none.", output, StringComparison.Ordinal);
        Assert.Equal("Milestone: v1.1.0 · due 2030-06-01 · 6 open tasks" + Environment.NewLine, chosen);
        Assert.StartsWith("Milestone: not chosen" + Environment.NewLine, shown, StringComparison.Ordinal);
        Assert.False(File.Exists(Path.Combine(personal.FullName, "projects.toml")));
    }
}
