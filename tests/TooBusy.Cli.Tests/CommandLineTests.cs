using TooBusy.Cli.Terminal;
using TooBusy.Core.Processes;

namespace TooBusy.Cli.Tests;

// What the tool does whatever the command: its version, its help, a wrong command line, a project that
// is not found or not set up, and an error nobody foresaw.
public sealed class CommandLineTests : CliAppTests
{
    [Fact]
    public async Task VersionOptionPrintsTheVersion()
    {
        var (exit, output, _) = await RunAsync("--version");

        Assert.Equal(0, exit);
        Assert.Matches(@"^\d+\.\d+\.\d+(\.dev(\+[0-9a-f]{7})?)?" + Environment.NewLine + "$", output);
        Assert.Equal(CliApp.Commit is null ? CliApp.Version : $"{CliApp.Version}+{CliApp.Commit}", output.TrimEnd());
    }

    [Theory]
    [InlineData("1.0.0.dev+c56f6c2e1f0a9b8c7d6e5f4a3b2c1d0e9f8a7b6c", "1.0.0.dev", "c56f6c2")]
    [InlineData("1.0.0.dev+c56f6c2", "1.0.0.dev", "c56f6c2")]
    [InlineData("1.0.0.dev+c56f", "1.0.0.dev", "c56f")]
    [InlineData("1.0.0.dev+", "1.0.0.dev", null)]
    [InlineData("1.0.0", "1.0.0", null)]
    public void TheVersionIsSplitFromTheCommitWhichIsShortened(string informational, string version, string? commit)
    {
        Assert.Equal((version, commit), CliApp.SplitVersion(informational));
    }

    [Theory]
    [InlineData]
    [InlineData("run")]
    [InlineData("run", "--demo")]
    [InlineData("queue")]
    [InlineData("queue", "--demo")]
    [InlineData("doctor")]
    [InlineData("status")]
    [InlineData("init", "--demo")]
    [InlineData("init", "--assist")]
    [InlineData("init", "--assist", "10")]
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
    [InlineData("queue")]
    public async Task WithoutSettingsTheProjectIsNotSetUp(params string[] args)
    {
        GitRepository();

        var (exit, output, error) = await RunAsync(args);

        Assert.Equal(2, exit);
        Assert.Equal("", output);
        Assert.Equal(
            "toobusy: this project is not set up yet." + Environment.NewLine
            + "Run `toobusy init` to set it up." + Environment.NewLine
            + "An assistant that sets a project up for its owner starts with `toobusy init --assist`." + Environment.NewLine,
            error);
    }

    [Theory]
    [InlineData("init", "--yes")]
    [InlineData("init", "--assist")]
    [InlineData("milestone")]
    [InlineData("model")]
    [InlineData("effort")]
    [InlineData("run")]
    [InlineData("queue")]
    [InlineData("doctor")]
    public async Task WithoutAHomeFolderACommandThatWorksInAProjectFails(params string[] args)
    {
        GitRepository();
        WriteSettings(Settings);

        var (exit, output, error) = await RunAsync(Context(null) with { PersonalFolder = null }, args);

        Assert.Equal(2, exit);
        Assert.Equal("", output);
        Assert.Equal("toobusy: there is no home folder to keep your choices in" + Environment.NewLine, error);
    }

    [Fact]
    public async Task ADemoNeedsNoHomeFolder()
    {
        GitRepository();

        var (exit, output, error) = await RunAsync(Context(null) with { PersonalFolder = null }, ["effort", "--demo", "high"]);

        Assert.Equal(0, exit);
        Assert.Equal("Effort: high" + Environment.NewLine, output);
        Assert.Equal("", error);
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

    [Theory]
    [InlineData]
    [InlineData("init")]
    [InlineData("milestone")]
    [InlineData("model")]
    [InlineData("effort")]
    [InlineData("run")]
    [InlineData("queue")]
    [InlineData("status")]
    [InlineData("doctor")]
    public async Task EveryCommandButAboutHasADemo(params string[] command)
    {
        var (exit, output, _) = await RunAsync([.. command, "--help"]);

        Assert.Equal(0, exit);
        Assert.Contains("--demo", output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AboutHasNoDemoAsThereIsNothingOfItToImitate()
    {
        var (_, help, _) = await RunAsync("about", "--help");
        var (exit, _, error) = await RunAsync("about", "--demo");

        Assert.DoesNotContain("--demo", help, StringComparison.Ordinal);
        Assert.Equal(2, exit);
        Assert.Contains("--demo", error, StringComparison.Ordinal);
    }

    [Fact]
    public async Task UnknownArgumentIsAWrongCommandLine()
    {
        var (exit, _, error) = await RunAsync("--no-such-option");

        Assert.Equal(2, exit);
        Assert.Contains("--no-such-option", error, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AnErrorTheToolDidNotForeseeIsALineWhereErrorsGoAndNotAStackTrace()
    {
        GitRepository();
        WriteSettings(Settings);
        await RunAsync("milestone", "--none");
        var processes = new ScriptedProcesses(command => command == "git"
            ? new ProcessResult(ProcessStatus.Exited, 0, "https://github.com/acme/rocket.git\n", "")
            : throw new InvalidOperationException("The answer of gh\nis not what it was."));

        var (exit, output, error) = await RunAsync(Context(processes), ["queue"]);

        Assert.Equal(1, exit);
        Assert.Equal("", output);
        Assert.Equal(
            "toobusy: The answer of gh is not what it was." + Environment.NewLine
            + "This is an error toobusy did not foresee (InvalidOperationException): run the command again with TOOBUSY_TRACE=1 to see where it happened." + Environment.NewLine,
            error);

        // TOOBUSY_TRACE=1 asks for where it happened, for a report of the bug.
        (exit, _, error) = await RunAsync(Context(processes) with { Trace = true }, ["queue"]);

        Assert.Equal(1, exit);
        Assert.StartsWith("toobusy: The answer of gh is not what it was." + Environment.NewLine + "System.InvalidOperationException: The answer of gh", error, StringComparison.Ordinal);
        Assert.Contains("   at ", error, StringComparison.Ordinal);
    }

    [Fact]
    public async Task TheNotSetUpMessageIsColouredInATerminal()
    {
        GitRepository();
        using var error = new StringWriter();
        var context = new CliContext(folder.FullName, TextWriter.Null, error) { ErrorPalette = Palette.Dark };

        var exit = await CliApp.RunAsync(["run"], context, TestContext.Current.CancellationToken);

        Assert.Equal(2, exit);
        Assert.StartsWith(
            $"{Palette.Dark.Muted("toobusy:")} this project is not set up yet.{Environment.NewLine}Run {Palette.Dark.Accent("`toobusy init`")} to set it up.{Environment.NewLine}",
            error.ToString(),
            StringComparison.Ordinal);
    }
}
