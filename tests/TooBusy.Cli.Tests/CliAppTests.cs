using TooBusy.Cli.Terminal;
using TooBusy.Core.Processes;

namespace TooBusy.Cli.Tests;

public sealed class CliAppTests : IDisposable
{
    const string Settings = """
        version = 1

        [tracker]
        type = "github"

        [queue.labels]
        blocking = ["manual"]
        take = []

        [assistant]
        type = "claude-code"
        """;

    readonly DirectoryInfo folder = Directory.CreateTempSubdirectory("toobusy-cli-");

    // Where the choice of the user is kept, in place of the home folder.
    readonly DirectoryInfo personal = Directory.CreateTempSubdirectory("toobusy-personal-");

    public void Dispose()
    {
        folder.Delete(recursive: true);
        personal.Delete(recursive: true);
    }

    [Fact]
    public async Task VersionOptionPrintsTheVersion()
    {
        var (exit, output, _) = await RunAsync("--version");

        Assert.Equal(0, exit);
        Assert.Matches(@"^\d+\.\d+\.\d+", output.Trim());
    }

    [Fact]
    public async Task HelpOptionWorksOutsideAGitRepository()
    {
        var (exit, output, _) = await RunAsync("--help");

        Assert.Equal(0, exit);
        Assert.Contains("Usage:", output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task HelpOfACommandWorksOutsideAGitRepository()
    {
        var (exit, output, _) = await RunAsync("run", "--help");

        Assert.Equal(0, exit);
        Assert.Contains("--demo", output, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData]
    [InlineData("run")]
    [InlineData("run", "--demo")]
    public async Task OutsideAGitRepositoryACommandFails(params string[] args)
    {
        var (exit, output, error) = await RunAsync(args);

        Assert.Equal(2, exit);
        Assert.Equal("", output);
        Assert.Equal("toobusy: not inside a git repository" + Environment.NewLine, error);
    }

    [Theory]
    [InlineData]
    [InlineData("run")]
    public async Task WithoutSettingsTheProjectIsNotSetUp(params string[] args)
    {
        GitRepository();

        var (exit, output, error) = await RunAsync(args);

        Assert.Equal(2, exit);
        Assert.Equal("", output);
        Assert.Equal(
            "toobusy: this project is not set up yet." + Environment.NewLine + "Run `toobusy init` to set it up." + Environment.NewLine,
            error);
    }

    [Fact]
    public async Task TheProjectIsFoundFromAFolderInsideIt()
    {
        GitRepository();
        WriteSettings(Settings);
        var inside = folder.CreateSubdirectory("src").CreateSubdirectory("deep");

        var (exit, output, _) = await RunFromAsync(inside.FullName, "run", "--demo");

        Assert.Equal(0, exit);
        Assert.Contains("Demo", output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task NoArgumentsPrintTheHelpInAProjectThatIsSetUp()
    {
        GitRepository();
        WriteSettings(Settings);

        var (exit, output, error) = await RunAsync();

        Assert.Equal(0, exit);
        Assert.Contains("Usage:", output, StringComparison.Ordinal);
        Assert.Equal("", error);
    }

    [Fact]
    public async Task UnknownArgumentIsAWrongCommandLine()
    {
        var (exit, _, error) = await RunAsync("--no-such-option");

        Assert.Equal(2, exit);
        Assert.Contains("--no-such-option", error, StringComparison.Ordinal);
    }

    [Fact]
    public async Task UnknownOptionOfACommandIsAWrongCommandLine()
    {
        GitRepository();
        WriteSettings(Settings);

        var (exit, _, error) = await RunAsync("run", "--no-such-option");

        Assert.Equal(2, exit);
        Assert.Contains("--no-such-option", error, StringComparison.Ordinal);
    }

    [Fact]
    public async Task DemoSaysThatNothingIsChanged()
    {
        GitRepository();
        WriteSettings(Settings);

        var (exit, output, _) = await RunAsync("run", "--demo");

        Assert.Equal(0, exit);
        Assert.Equal("Demo: nothing is changed." + Environment.NewLine + "Running the tasks is not built yet." + Environment.NewLine, output);
    }

    [Fact]
    public async Task InATerminalARunIsAPageThatExitLeaves()
    {
        GitRepository();
        WriteSettings(Settings);
        var keys = new Keys().Type("/exit").Press(Keys.Enter);

        var (exit, output, _) = await RunWithKeysAsync(keys, "run", "--demo");

        Assert.Equal(0, exit);
        Assert.StartsWith("\u001b[?1049h", output, StringComparison.Ordinal);
        Assert.Contains("Running the queue · demo", output, StringComparison.Ordinal);
        Assert.Contains("Running the tasks is not built yet.", output, StringComparison.Ordinal);
        Assert.Equal($"toobusy · ~{Path.DirectorySeparatorChar}{folder.Name}{Environment.NewLine}", Report(output));
    }

    [Fact]
    public async Task CtrlCTwiceLeavesTheRunToo()
    {
        GitRepository();
        WriteSettings(Settings);

        var (exit, output, _) = await RunWithKeysAsync(new Keys().Press(Keys.ControlC, Keys.ControlC), "run", "--demo");

        Assert.Equal(0, exit);
        Assert.StartsWith("toobusy · ~", Report(output), StringComparison.Ordinal);
    }

    [Fact]
    public async Task ARunDoesNotStartBeforeTheMilestoneIsChosen()
    {
        GitRepository();
        WriteSettings(Settings);

        var (exit, output, error) = await RunAsync("run");

        Assert.Equal(1, exit);
        Assert.Equal("", output);
        Assert.Equal(
            "toobusy: the milestone to work on is not chosen." + Environment.NewLine + "Run `toobusy milestone` to choose it." + Environment.NewLine,
            error);
    }

    [Fact]
    public async Task ARunDoesNotStartWithAMilestoneThatIsNotOpenAnyMore()
    {
        GitRepository();
        WriteSettings(Settings);
        var processes = GitHub(milestones: "v2\t\t3\nv3\t\t0\n");
        await RunAsync(Context(processes), ["milestone", "v2"]);
        var later = GitHub(milestones: "v3\t\t0\n");

        var (exit, _, error) = await RunAsync(Context(later), ["run"]);

        Assert.Equal(1, exit);
        Assert.StartsWith("toobusy: the milestone “v2” is not open any more." + Environment.NewLine, error, StringComparison.Ordinal);
    }

    [Fact]
    public async Task WithoutATerminalARunSaysThatItIsNotBuiltYet()
    {
        GitRepository();
        WriteSettings(Settings);
        await RunAsync("milestone", "--none");

        var (exit, _, error) = await RunAsync("run");

        Assert.Equal(1, exit);
        Assert.Equal("toobusy: running the tasks is not built yet" + Environment.NewLine, error);
    }

    [Fact]
    public async Task InATerminalARunOpensThePageOnTheRunAndGoesToTheMenu()
    {
        GitRepository();
        WriteSettings(Settings);
        await RunAsync("milestone", "--none");
        var keys = new Keys().Type("/menu").Press(Keys.Enter, Keys.Up, Keys.Enter);

        var (exit, output, _) = await RunWithKeysAsync(keys, "run");

        Assert.Equal(0, exit);
        Assert.Contains("Running the queue", output, StringComparison.Ordinal);
        Assert.DoesNotContain("demo", output, StringComparison.Ordinal);
        Assert.Contains("What to do", output, StringComparison.Ordinal);
        Assert.Equal($"toobusy · ~{Path.DirectorySeparatorChar}{folder.Name}{Environment.NewLine}", Report(output));
    }

    [Fact]
    public async Task AMilestoneIsChosenByItsTitleFromAScript()
    {
        GitRepository();
        var processes = GitHub(milestones: "v0.2.0\t2030-01-15T08:00:00Z\t12\nBacklog\t\t1\n");

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
        var processes = GitHub(milestones: "v0.3.0\t\t1\nv0.2.0\t\t2\n");

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
        var processes = GitHub(milestones: "v0.3.0\t\t1\nv0.2.0\t\t2\n");

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
        GitRepository();

        var (exit, output, _) = await RunAsync("milestone", "--none", "--demo");

        Assert.Equal(0, exit);
        Assert.StartsWith("Milestone: none.", output, StringComparison.Ordinal);
        Assert.False(File.Exists(Path.Combine(personal.FullName, "projects.toml")));
    }

    [Fact]
    public async Task WithoutACommandAProjectIsSetUpAMilestoneIsChosenAndTheMenuOpens()
    {
        GitRepository();
        var processes = GitHub(milestones: "v0.2.0\t\t2\n", labels: "bug\nmanual\n");

        // No project, no blocking labels, any label, yes; the first milestone; the last of the menu.
        var keys = new Keys().Press(Keys.Tab, Keys.Tab, Keys.Enter, Keys.Enter, Keys.Enter, Keys.Enter, Keys.Enter, Keys.Up, Keys.Enter);

        var (exit, output, error) = await RunWithKeysAsync(keys, processes, []);

        Assert.Equal(0, exit);
        Assert.Equal("", error);
        Assert.Contains("What to do", output, StringComparison.Ordinal);
        Assert.Contains("Change milestone  v0.2.0", output, StringComparison.Ordinal);
        Assert.Equal(
            [
                $"toobusy · ~{Path.DirectorySeparatorChar}{folder.Name}",
                "✔ Tracker          GitHub",
                "✔ Project          none",
                "✔ Blocking labels  none",
                "✔ Labels to take   any task",
                "✔ Assistant        Claude Code",
                "✔ The settings are written to .toobusy/settings.toml. Commit the file.",
                "✔ Milestone        v0.2.0",
                "",
            ],
            Report(output).Split(Environment.NewLine));
        Assert.Contains("type = \"github\"", File.ReadAllText(Path.Combine(folder.FullName, ".toobusy", "settings.toml")), StringComparison.Ordinal);
        Assert.Equal("Milestone: v0.2.0", (await ShownAsync(processes)).Output.Trim());
    }

    [Fact]
    public async Task WithoutACommandAProjectThatIsReadyOpensWithTheMenu()
    {
        GitRepository();
        WriteSettings(Settings);
        await RunAsync("milestone", "--none");

        // Settings, back out of them, then leave with Escape twice.
        var keys = new Keys().Press(Keys.Down, Keys.Down, Keys.Enter, Keys.Escape, Keys.Escape, Keys.Escape);

        var (exit, output, _) = await RunWithKeysAsync(keys, []);

        Assert.Equal(0, exit);
        Assert.Contains("✔ Milestone        none: tasks are taken whatever their milestone", output, StringComparison.Ordinal);
        Assert.Contains("Setting up this project", output, StringComparison.Ordinal);
        Assert.Contains("The setup was left: nothing was changed.", output, StringComparison.Ordinal);
        Assert.Contains("press esc again to exit", output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ADemoShowsAProjectThatIsReadyWhereThereAreNoSettings()
    {
        GitRepository();

        // Straight to the menu: the run, back to the menu, and out.
        var keys = new Keys().Press(Keys.Enter).Type("/menu").Press(Keys.Enter, Keys.Up, Keys.Enter);

        var (exit, output, error) = await RunWithKeysAsync(keys, ["--demo"]);

        Assert.Equal(0, exit);
        Assert.Equal("", error);
        Assert.DoesNotContain("Write the settings?", output, StringComparison.Ordinal);
        Assert.Contains("✔ Project          https://github.com/users/example/projects/1", output, StringComparison.Ordinal);
        Assert.Contains("✔ Blocking labels  manual, draft", output, StringComparison.Ordinal);
        Assert.Contains("✔ Milestone        v0.1.0 (made up) · due 2030-01-01 · 3 open tasks", output, StringComparison.Ordinal);
        Assert.Contains("Running the tasks is not built yet.", output, StringComparison.Ordinal);
        Assert.Equal($"toobusy · ~{Path.DirectorySeparatorChar}{folder.Name}{Environment.NewLine}", Report(output));
        Assert.False(Directory.Exists(Path.Combine(folder.FullName, ".toobusy")));
        Assert.False(File.Exists(Path.Combine(personal.FullName, "projects.toml")));
    }

    [Fact]
    public async Task ADemoOfTheRunNeedsNoSettingsEither()
    {
        GitRepository();

        var (exit, output, _) = await RunAsync("run", "--demo");

        Assert.Equal(0, exit);
        Assert.StartsWith("Demo: nothing is changed.", output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ADemoShowsTheSettingsThatExist()
    {
        GitRepository();
        WriteSettings(Settings);

        var (_, output, _) = await RunWithKeysAsync(new Keys().Press(Keys.Up, Keys.Enter), ["--demo"]);

        Assert.Contains("✔ Project          none", output, StringComparison.Ordinal);
        Assert.Contains("✔ Blocking labels  manual", output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task LeavingTheFirstSetupLeavesThePage()
    {
        GitRepository();

        var (exit, output, _) = await RunWithKeysAsync(new Keys().Press(Keys.ControlC, Keys.ControlC), []);

        Assert.Equal(0, exit);
        Assert.DoesNotContain("What to do", output, StringComparison.Ordinal);
        Assert.False(Directory.Exists(Path.Combine(folder.FullName, ".toobusy")));
    }

    [Fact]
    public async Task ARealSetupWritesTheSettings()
    {
        GitRepository();
        var processes = GitHub(milestones: "", labels: "bug\nmanual\n");

        // No project; `manual` blocks; any label; yes.
        var keys = new Keys().Press(Keys.Tab, Keys.Tab, Keys.Enter, Keys.Down, Keys.Space, Keys.Enter, Keys.Enter, Keys.Enter);

        var (exit, output, _) = await RunWithKeysAsync(keys, processes, "init");

        Assert.Equal(0, exit);
        Assert.EndsWith("✔ The settings are written to .toobusy/settings.toml. Commit the file." + Environment.NewLine, Report(output), StringComparison.Ordinal);
        Assert.Contains("blocking = [\"manual\"]", File.ReadAllText(Path.Combine(folder.FullName, ".toobusy", "settings.toml")), StringComparison.Ordinal);
        Assert.Contains(processes.Asked, asked => asked.Command == "gh" && asked.Arguments.Take(4).SequenceEqual(["label", "list", "--repo", "acme/rocket"]));
    }

    [Fact]
    public async Task WithoutTheGitHubToolASetupSaysHowToGetIt()
    {
        GitRepository();

        var (_, output, _) = await RunWithKeysAsync(new Keys().Press(Keys.ControlC, Keys.ControlC), "init");

        Assert.Contains("✘ The GitHub command-line tool `gh` is not installed.", output, StringComparison.Ordinal);
        Assert.Contains("fix: ", output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RunRefusesSettingsThatAreNotValid()
    {
        GitRepository();
        WriteSettings(Settings.Replace("blocking = [\"manual\"]", "blocking = \"manual\"", StringComparison.Ordinal));

        var (exit, output, error) = await RunAsync("run", "--demo");

        Assert.Equal(1, exit);
        Assert.Equal("", output);
        Assert.Contains(".toobusy/settings.toml:7: queue.labels.blocking: ", error, StringComparison.Ordinal);
    }

    [Fact]
    public async Task TheNotSetUpMessageIsColouredInATerminal()
    {
        GitRepository();
        using var error = new StringWriter();
        var context = new CliContext(folder.FullName, TextWriter.Null, error) { ErrorPalette = Palette.Dark };

        var exit = await CliApp.RunAsync(["run"], context, TestContext.Current.CancellationToken);

        Assert.Equal(2, exit);
        Assert.Equal(
            $"{Palette.Dark.Muted("toobusy:")} this project is not set up yet.{Environment.NewLine}Run {Palette.Dark.Accent("`toobusy init`")} to set it up.{Environment.NewLine}",
            error.ToString());
    }

    [Fact]
    public async Task InitNeedsAGitRepository()
    {
        var (exit, _, error) = await RunAsync("init", "--demo");

        Assert.Equal(2, exit);
        Assert.Equal("toobusy: not inside a git repository" + Environment.NewLine, error);
    }

    [Fact]
    public async Task InitNeedsATerminal()
    {
        GitRepository();

        var (exit, _, error) = await RunAsync("init");

        Assert.Equal(1, exit);
        Assert.Contains("needs a terminal", error, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ADemoOfTheSetupGoesThroughTheStepsAndWritesNothing()
    {
        GitRepository();
        var keys = new Keys().Press(Keys.Enter, Keys.Enter, Keys.Enter, Keys.Enter);

        var (exit, output, error) = await RunWithKeysAsync(keys, "init", "--demo");

        Assert.Equal(0, exit);
        Assert.Equal("", error);
        Assert.StartsWith("\u001b[?1049h", output, StringComparison.Ordinal);
        Assert.Contains("Setting up this project · demo", output, StringComparison.Ordinal);
        Assert.Contains("The answers above will be written to .toobusy/settings.toml.", output, StringComparison.Ordinal);
        Assert.DoesNotContain("[tracker]", output, StringComparison.Ordinal);
        Assert.Equal(
            [
                $"toobusy · Setting up this project · ~{Path.DirectorySeparatorChar}{folder.Name}",
                "✔ Tracker          GitHub",
                "✔ Project          Rocket (made up)  https://github.com/users/example/projects/1",
                "✔ Blocking labels  none",
                "✔ Labels to take   any task",
                "✔ Assistant        Claude Code",
                "Demo: nothing was made, linked or written; .toobusy/settings.toml is left as it was.",
                "",
            ],
            Report(output).Split(Environment.NewLine));
        Assert.DoesNotContain("repository", Report(output), StringComparison.OrdinalIgnoreCase);
        Assert.False(Directory.Exists(Path.Combine(folder.FullName, ".toobusy")));
    }

    [Fact]
    public async Task ADemoOfTheSetupStartsFromTheSettingsThatExist()
    {
        GitRepository();
        WriteSettings(Settings);
        var keys = new Keys().Press(Keys.Tab, Keys.Tab, Keys.Tab, Keys.Enter, Keys.Enter, Keys.Enter);

        var (exit, output, _) = await RunWithKeysAsync(keys, "init", "--demo");

        Assert.Equal(0, exit);
        Assert.Contains("✔ Project          none", Report(output), StringComparison.Ordinal);
        Assert.Contains("✔ Blocking labels  manual", Report(output), StringComparison.Ordinal);
        Assert.EndsWith("Nothing to change: the settings already say this." + Environment.NewLine, Report(output), StringComparison.Ordinal);
    }

    [Fact]
    public async Task ADemoOfTheSetupReadsTheOriginAndTheBoardsOfTheUser()
    {
        GitRepository();
        var processes = new FakeProcesses(command => new ProcessResult(ProcessStatus.Exited, 0, command == "git"
            ? "https://github.com/bitpatch/toobusy.git\n"
            : "owner\tuser\tdenis\nboard\thttps://github.com/users/denis/projects/3\tFatgard\nlinked\thttps://github.com/orgs/bitpatch/projects/4\tToobusy\n", ""));
        var keys = new Keys().Press(Keys.Enter, Keys.Enter, Keys.Enter, Keys.Enter);

        var (exit, output, _) = await RunWithKeysAsync(keys, processes, "init", "--demo");

        Assert.Equal(0, exit);
        Assert.Contains(processes.Asked, asked => asked.Command == "git" && asked.Arguments.SequenceEqual(["-C", folder.FullName, "remote", "get-url", "origin"]));
        Assert.Contains(processes.Asked, asked => asked.Command == "gh" && asked.Arguments.Contains("owner=bitpatch") && asked.Arguments.Contains("name=toobusy"));
        Assert.Contains("Toobusy  https://github.com/orgs/bitpatch/projects/4  linked to this repository", output, StringComparison.Ordinal);
        Assert.Contains("✔ Project          Toobusy  https://github.com/orgs/bitpatch/projects/4", Report(output), StringComparison.Ordinal);
    }

    [Fact]
    public async Task ADemoOfTheSetupGoesWithMadeUpBoardsWhenTheRealOnesCannotBeRead()
    {
        GitRepository();
        var processes = new FakeProcesses(_ => new ProcessResult(ProcessStatus.NotFound, 0, "", ""));
        var keys = new Keys().Press(Keys.Down, Keys.Enter, Keys.Enter, Keys.Enter, Keys.Enter);

        var (exit, output, _) = await RunWithKeysAsync(keys, processes, "init", "--demo");

        Assert.Equal(0, exit);
        Assert.Contains("The `origin` remote is not a GitHub repository", output, StringComparison.Ordinal);
        Assert.Contains("✔ Project          Moon base (made up)  https://github.com/users/example/projects/2", Report(output), StringComparison.Ordinal);
    }

    [Fact]
    public async Task ADemoOfTheSetupOffersEnoughOwnersToScroll()
    {
        GitRepository();
        var processes = new FakeProcesses(command => new ProcessResult(ProcessStatus.Exited, 0, command == "git"
            ? "https://github.com/bitpatch/toobusy.git\n"
            : "owner\torg\tbitpatch\nowner\tuser\tdenis\n", ""));
        var keys = new Keys().Press(Keys.Tab, Keys.Tab).Type("Rocket").Press(Keys.Down, Keys.Down, Keys.Enter, Keys.Enter, Keys.Enter, Keys.Enter);

        var (exit, output, _) = await RunWithKeysAsync(keys, processes, "init", "--demo");

        Assert.Equal(0, exit);
        Assert.Contains("↓ 5 more", output, StringComparison.Ordinal);
        Assert.Contains("The project “Rocket” will be made for example.", output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task DecliningTheSetupFails()
    {
        GitRepository();
        var keys = new Keys().Press(Keys.Enter, Keys.Enter, Keys.Enter, Keys.No);

        var (exit, output, _) = await RunWithKeysAsync(keys, "init", "--demo");

        Assert.Equal(1, exit);
        Assert.EndsWith("✘ The setup was declined: nothing was changed." + Environment.NewLine, Report(output), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task EscapeTwiceFromTheFirstStepAndCtrlCTwiceLeaveTheSetup(bool controlC)
    {
        GitRepository();
        var keys = new Keys().Press(Keys.Enter).Press(controlC ? [Keys.ControlC, Keys.ControlC] : [Keys.Escape, Keys.Escape, Keys.Escape]);

        var (exit, output, _) = await RunWithKeysAsync(keys, "init", "--demo");

        Assert.Equal(1, exit);
        Assert.Equal(
            $"toobusy · Setting up this project · ~{Path.DirectorySeparatorChar}{folder.Name}{Environment.NewLine}✘ The setup was left: nothing was changed.{Environment.NewLine}",
            Report(output));
    }

    void GitRepository() => folder.CreateSubdirectory(".git");

    void WriteSettings(string text) =>
        File.WriteAllText(Path.Combine(folder.CreateSubdirectory(".toobusy").FullName, "settings.toml"), text);

    Task<(int Exit, string Output, string Error)> RunAsync(params string[] args) => RunFromAsync(folder.FullName, args);

    Task<(int Exit, string Output, string Error)> RunFromAsync(string from, params string[] args) => RunAsync(Context(null) with { Folder = from }, args);

    Task<(int Exit, string Output, string Error)> RunWithKeysAsync(Keys keys, params string[] args) => RunWithKeysAsync(keys, null, args);

    // Runs in a terminal of a hundred and twenty columns and thirty lines.
    Task<(int Exit, string Output, string Error)> RunWithKeysAsync(Keys keys, FakeProcesses? processes, params string[] args) =>
        RunAsync(Context(processes) with { Terminal = new TerminalDevice(keys.Read, () => (120, 30)) }, args);

    // The project lies right under the home folder, and the choices of the user are kept in a folder of the test.
    CliContext Context(FakeProcesses? processes) => new(folder.FullName, TextWriter.Null, TextWriter.Null)
    {
        Home = folder.Parent!.FullName,
        Processes = processes,
        PersonalFolder = personal.FullName,
    };

    // What `toobusy milestone` prints without a terminal, and its exit code.
    async Task<(string Output, int Exit)> ShownAsync(FakeProcesses? processes)
    {
        var (exit, output, _) = await RunAsync(Context(processes), ["milestone"]);
        return (output, exit);
    }

    // A machine with git and a logged-in `gh`: the project is cloned from acme/rocket, the user can make boards for
    // themselves, and the repository has the given milestones and labels.
    static FakeProcesses GitHub(string milestones, string labels = "") => new((command, arguments) => new ProcessResult(ProcessStatus.Exited, 0, command switch
    {
        "git" => "https://github.com/acme/rocket.git\n",
        "gh" when arguments[0] == "label" => labels,
        "gh" when arguments[0] == "api" && arguments[1].StartsWith("repos/", StringComparison.Ordinal) => milestones,
        "gh" when arguments[0] == "api" => "owner\tuser\tann\n",
        _ => "",
    }, ""));

    // What is left in the terminal when the screen is closed.
    static string Report(string output) => output.Split(Screen.Leave)[^1];

    static async Task<(int Exit, string Output, string Error)> RunAsync(CliContext context, string[] args)
    {
        using var output = new StringWriter();
        using var error = new StringWriter();
        var exit = await CliApp.RunAsync(args, context with { Output = output, Error = error }, TestContext.Current.CancellationToken);
        return (exit, output.ToString(), error.ToString());
    }

    // Answers every command as it is told to and remembers what it was asked.
    sealed class FakeProcesses(Func<string, IReadOnlyList<string>, ProcessResult> answer) : IProcessRunner
    {
        public FakeProcesses(Func<string, ProcessResult> answer)
            : this((command, _) => answer(command))
        {
        }

        public List<(string Command, IReadOnlyList<string> Arguments)> Asked { get; } = [];

        public Task<ProcessResult> RunAsync(string command, IReadOnlyList<string> arguments, TimeSpan timeout, CancellationToken cancellationToken)
        {
            Asked.Add((command, arguments));
            return Task.FromResult(answer(command, arguments));
        }
    }
}
