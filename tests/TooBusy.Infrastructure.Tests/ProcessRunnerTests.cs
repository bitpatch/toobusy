using TooBusy.Core.Processes;
using TooBusy.Infrastructure.Processes;

namespace TooBusy.Infrastructure.Tests;

public class ProcessRunnerTests
{
    static readonly TimeSpan Long = TimeSpan.FromSeconds(30);

    readonly ProcessRunner runner = new();

    [Fact]
    public async Task ACommandGivesItsOutputAndItsExitCode()
    {
        var result = await runner.RunAsync("sh", ["-c", "echo out; echo err >&2; exit 3"], Long, TestContext.Current.CancellationToken);

        Assert.Equal(new ProcessResult(ProcessStatus.Exited, 3, "out\n", "err\n"), result);
    }

    [Fact]
    public async Task ArgumentsArePassedAsTheyAre()
    {
        var result = await runner.RunAsync("printf", ["%s", "a b&c"], Long, TestContext.Current.CancellationToken);

        Assert.Equal("a b&c", result.Output);
    }

    [Fact]
    public async Task ACommandThatAsksForInputGetsNone()
    {
        var result = await runner.RunAsync("cat", [], Long, TestContext.Current.CancellationToken);

        Assert.Equal(new ProcessResult(ProcessStatus.Exited, 0, "", ""), result);
    }

    [Fact]
    public async Task ACommandThatIsNotInstalledIsNotFound()
    {
        var result = await runner.RunAsync("toobusy-no-such-command", [], Long, TestContext.Current.CancellationToken);

        Assert.Equal(ProcessStatus.NotFound, result.Status);
    }

    [Fact]
    public async Task ACommandThatTakesTooLongIsStopped()
    {
        var result = await runner.RunAsync("sleep", ["30"], TimeSpan.FromMilliseconds(100), TestContext.Current.CancellationToken);

        Assert.Equal(ProcessStatus.TimedOut, result.Status);
    }

    [Fact]
    public async Task ACancellationStopsTheCommandAndThrows()
    {
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        cancellation.CancelAfter(TimeSpan.FromMilliseconds(100));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => runner.RunAsync("sleep", ["30"], Long, cancellation.Token));
    }
}
