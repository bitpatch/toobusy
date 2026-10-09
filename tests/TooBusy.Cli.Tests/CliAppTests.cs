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

    void GitRepository() => folder.CreateSubdirectory(".git");

    void WriteSettings(string text) =>
        File.WriteAllText(Path.Combine(folder.CreateSubdirectory(".toobusy").FullName, "settings.toml"), text);

    Task<(int Exit, string Output, string Error)> RunAsync(params string[] args) => RunFromAsync(folder.FullName, args);

    static async Task<(int Exit, string Output, string Error)> RunFromAsync(string from, params string[] args)
    {
        using var output = new StringWriter();
        using var error = new StringWriter();
        var exit = await CliApp.RunAsync(args, from, output, error, TestContext.Current.CancellationToken);
        return (exit, output.ToString(), error.ToString());
    }
}
