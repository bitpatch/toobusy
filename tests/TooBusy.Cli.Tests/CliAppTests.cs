using TooBusy.Cli.Terminal;

namespace TooBusy.Cli.Tests;

public sealed class CliAppTests : IDisposable
{
    const string Settings = """
        version = 1

        [tracker]
        type = "github"
        repository = "bitpatch/toobusy"

        [queue.milestone]
        rule = "lowest-version"

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
        Assert.Contains("--dry-run", output, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData]
    [InlineData("run")]
    [InlineData("run", "--dry-run")]
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
    [InlineData("run", "--dry-run")]
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

        var (exit, output, _) = await RunFromAsync(inside.FullName, "run", "--dry-run");

        Assert.Equal(0, exit);
        Assert.Contains("Dry run", output, StringComparison.Ordinal);
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
    public async Task DryRunSaysThatNothingIsChanged()
    {
        GitRepository();
        WriteSettings(Settings);

        var (exit, output, _) = await RunAsync("run", "--dry-run");

        Assert.Equal(0, exit);
        Assert.Contains("Dry run", output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RealRunIsRefusedUntilItExists()
    {
        GitRepository();
        WriteSettings(Settings);

        var (exit, _, error) = await RunAsync("run");

        Assert.Equal(1, exit);
        Assert.Contains("--dry-run", error, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RunRefusesSettingsThatAreNotValid()
    {
        GitRepository();
        WriteSettings(Settings.Replace("bitpatch/toobusy", "toobusy", StringComparison.Ordinal));

        var (exit, output, error) = await RunAsync("run", "--dry-run");

        Assert.Equal(1, exit);
        Assert.Equal("", output);
        Assert.Contains(".toobusy/settings.toml:5: tracker.repository: ", error, StringComparison.Ordinal);
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
        var (exit, _, error) = await RunAsync("init", "--dry-run");

        Assert.Equal(2, exit);
        Assert.Equal("toobusy: not inside a git repository" + Environment.NewLine, error);
    }

    [Fact]
    public async Task ARealSetupIsRefusedUntilItExists()
    {
        GitRepository();

        var (exit, _, error) = await RunAsync("init");

        Assert.Equal(1, exit);
        Assert.Contains("--dry-run", error, StringComparison.Ordinal);
    }

    [Fact]
    public async Task InitNeedsATerminal()
    {
        GitRepository();

        var (exit, _, error) = await RunAsync("init", "--dry-run");

        Assert.Equal(1, exit);
        Assert.Contains("needs a terminal", error, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ADryRunOfTheSetupGoesThroughTheStepsAndWritesNothing()
    {
        GitRepository();
        var keys = new Keys().Press(Keys.Enter, Keys.Enter, Keys.Enter, Keys.Enter, Keys.Enter, Keys.Enter);

        var (exit, output, error) = await RunWithKeysAsync(keys, "init", "--dry-run");

        Assert.Equal(0, exit);
        Assert.Equal("", error);
        Assert.StartsWith("Dry run:", output, StringComparison.Ordinal);
        Assert.Contains("✔ Repository       example/project", output, StringComparison.Ordinal);
        Assert.Contains("✔ Milestone rule   lowest-version", output, StringComparison.Ordinal);
        Assert.Contains("+ repository = \"example/project\"", output, StringComparison.Ordinal);
        Assert.Contains("✔ Write the settings? yes", output, StringComparison.Ordinal);
        Assert.False(Directory.Exists(Path.Combine(folder.FullName, ".toobusy")));
    }

    [Fact]
    public async Task ADryRunOfTheSetupStartsFromTheSettingsThatExist()
    {
        GitRepository();
        WriteSettings(Settings);
        var keys = new Keys().Press(Keys.Enter, Keys.Enter, Keys.Enter, Keys.Enter, Keys.Enter);

        var (exit, output, _) = await RunWithKeysAsync(keys, "init", "--dry-run");

        Assert.Equal(0, exit);
        Assert.Contains("✔ Repository       bitpatch/toobusy", output, StringComparison.Ordinal);
        Assert.Contains("✔ Blocking labels  manual", output, StringComparison.Ordinal);
        Assert.Contains("Nothing to change", output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task DecliningTheSetupFails()
    {
        GitRepository();
        var keys = new Keys().Press(Keys.Enter, Keys.Enter, Keys.Enter, Keys.Enter, Keys.Enter, Keys.No);

        var (exit, _, _) = await RunWithKeysAsync(keys, "init", "--dry-run");

        Assert.Equal(1, exit);
    }

    void GitRepository() => folder.CreateSubdirectory(".git");

    void WriteSettings(string text) =>
        File.WriteAllText(Path.Combine(folder.CreateSubdirectory(".toobusy").FullName, "settings.toml"), text);

    Task<(int Exit, string Output, string Error)> RunAsync(params string[] args) => RunFromAsync(folder.FullName, args);

    static Task<(int Exit, string Output, string Error)> RunFromAsync(string from, params string[] args) =>
        RunAsync(new CliContext(from, TextWriter.Null, TextWriter.Null), args);

    Task<(int Exit, string Output, string Error)> RunWithKeysAsync(Keys keys, params string[] args) =>
        RunAsync(new CliContext(folder.FullName, TextWriter.Null, TextWriter.Null) { ReadKey = keys.Read }, args);

    static async Task<(int Exit, string Output, string Error)> RunAsync(CliContext context, string[] args)
    {
        using var output = new StringWriter();
        using var error = new StringWriter();
        var exit = await CliApp.RunAsync(args, context with { Output = output, Error = error }, TestContext.Current.CancellationToken);
        return (exit, output.ToString(), error.ToString());
    }
}
