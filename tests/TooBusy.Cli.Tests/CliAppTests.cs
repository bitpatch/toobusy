namespace TooBusy.Cli.Tests;

public class CliAppTests
{
    [Fact]
    public async Task VersionOptionPrintsTheVersion()
    {
        var (exit, output, _) = await RunAsync("--version");

        Assert.Equal(0, exit);
        Assert.Matches(@"^\d+\.\d+\.\d+", output.Trim());
    }

    [Fact]
    public async Task NoArgumentsPrintTheHelp()
    {
        var (exit, output, _) = await RunAsync();

        Assert.Equal(0, exit);
        Assert.Contains("Usage:", output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task UnknownArgumentFails()
    {
        var (exit, _, error) = await RunAsync("--no-such-option");

        Assert.NotEqual(0, exit);
        Assert.Contains("--no-such-option", error, StringComparison.Ordinal);
    }

    static async Task<(int Exit, string Output, string Error)> RunAsync(params string[] args)
    {
        using var output = new StringWriter();
        using var error = new StringWriter();
        var exit = await CliApp.RunAsync(args, output, error, TestContext.Current.CancellationToken);
        return (exit, output.ToString(), error.ToString());
    }
}
