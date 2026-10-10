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

    public void Dispose() => folder.Delete(recursive: true);

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
    [InlineData("run", "--demo")]
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
        Assert.Equal("Demo: nothing is changed." + Environment.NewLine + "There is nothing to imitate yet." + Environment.NewLine, output);
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
        Assert.Equal(
            $"toobusy · Running the queue · ~{Path.DirectorySeparatorChar}{folder.Name}{Environment.NewLine}Demo: nothing is changed.{Environment.NewLine}There is nothing to imitate yet.{Environment.NewLine}",
            Report(output));
    }

    [Fact]
    public async Task CtrlCTwiceLeavesTheRunToo()
    {
        GitRepository();
        WriteSettings(Settings);

        var (exit, output, _) = await RunWithKeysAsync(new Keys().Press(Keys.ControlC, Keys.ControlC), "run", "--demo");

        Assert.Equal(0, exit);
        Assert.StartsWith("toobusy · Running the queue", Report(output), StringComparison.Ordinal);
    }

    [Fact]
    public async Task RealRunIsRefusedUntilItExists()
    {
        GitRepository();
        WriteSettings(Settings);

        var (exit, _, error) = await RunAsync("run");

        Assert.Equal(1, exit);
        Assert.Contains("--demo", error, StringComparison.Ordinal);
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
    public async Task ARealSetupIsRefusedUntilItExists()
    {
        GitRepository();

        var (exit, _, error) = await RunAsync("init");

        Assert.Equal(1, exit);
        Assert.Contains("--demo", error, StringComparison.Ordinal);
    }

    [Fact]
    public async Task InitNeedsATerminal()
    {
        GitRepository();

        var (exit, _, error) = await RunAsync("init", "--demo");

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

    static Task<(int Exit, string Output, string Error)> RunFromAsync(string from, params string[] args) =>
        RunAsync(new CliContext(from, TextWriter.Null, TextWriter.Null), args);

    Task<(int Exit, string Output, string Error)> RunWithKeysAsync(Keys keys, params string[] args) => RunWithKeysAsync(keys, null, args);

    // Runs in a terminal of a hundred and twenty columns and thirty lines, in a project that lies right under the home folder.
    Task<(int Exit, string Output, string Error)> RunWithKeysAsync(Keys keys, FakeProcesses? processes, params string[] args) => RunAsync(
        new CliContext(folder.FullName, TextWriter.Null, TextWriter.Null)
        {
            Terminal = new TerminalDevice(keys.Read, () => (120, 30)),
            Home = folder.Parent!.FullName,
            Processes = processes,
        },
        args);

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
    sealed class FakeProcesses(Func<string, ProcessResult> answer) : IProcessRunner
    {
        public List<(string Command, IReadOnlyList<string> Arguments)> Asked { get; } = [];

        public Task<ProcessResult> RunAsync(string command, IReadOnlyList<string> arguments, TimeSpan timeout, CancellationToken cancellationToken)
        {
            Asked.Add((command, arguments));
            return Task.FromResult(answer(command));
        }
    }
}
