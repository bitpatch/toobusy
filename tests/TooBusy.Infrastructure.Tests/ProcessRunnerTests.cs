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
    public async Task ACommandStartsInTheFolderOfItsRunner()
    {
        var folder = Directory.CreateTempSubdirectory("toobusy-runner-");
        try
        {
            File.WriteAllText(Path.Combine(folder.FullName, "here.txt"), "");

            var result = await runner.Inside(folder.FullName).RunAsync("ls", [], Long, TestContext.Current.CancellationToken);

            Assert.Equal("here.txt\n", result.Output);
        }
        finally
        {
            folder.Delete(recursive: true);
        }
    }

    [Fact]
    public async Task ACommandWithoutALimitOfTimeRunsUntilItIsStopped()
    {
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        var running = runner.RunAsync("sleep", ["30"], Timeout.InfiniteTimeSpan, cancellation.Token);

        await Task.Delay(TimeSpan.FromMilliseconds(100), TestContext.Current.CancellationToken);
        Assert.False(running.IsCompleted);
        await cancellation.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => running);
    }

    [Fact]
    public async Task ACommandThatAsksForInputGetsNone()
    {
        var result = await runner.RunAsync("cat", [], Long, TestContext.Current.CancellationToken);

        Assert.Equal(new ProcessResult(ProcessStatus.Exited, 0, "", ""), result);
    }

    [Fact]
    public async Task ACommandHasGitsPromptsOff()
    {
        var result = await runner.RunAsync("sh", ["-c", "echo \"$GIT_TERMINAL_PROMPT $GCM_INTERACTIVE\""], Long, TestContext.Current.CancellationToken);

        Assert.Equal("0 never\n", result.Output);
    }

    // git asks for a login on the terminal, which the closed input does not stop. The test leaves out every helper
    // and program of the machine that could give one.
    [Fact]
    public async Task GitAsksACommandForNoLoginAndFailsAtOnce()
    {
        var fill = "printf 'protocol=https\\nhost=example.com\\n\\n' | env -u GIT_ASKPASS -u SSH_ASKPASS git -c credential.helper= -c core.askPass= credential fill";

        var result = await runner.RunAsync("sh", ["-c", fill], Long, TestContext.Current.CancellationToken);

        Assert.Equal((ProcessStatus.Exited, 128), (result.Status, result.ExitCode));
        Assert.Contains("terminal prompts disabled", result.Error, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ACommandThatIsNotInstalledIsNotFound()
    {
        var result = await runner.RunAsync("toobusy-no-such-command", [], Long, TestContext.Current.CancellationToken);

        Assert.Equal(ProcessStatus.NotFound, result.Status);
    }

    // A command that is there and cannot start is not one to install: the system says why.
    [Fact]
    public async Task ACommandThatIsNotExecutableIsNotStartedAndSaysWhy()
    {
        var folder = Directory.CreateTempSubdirectory("toobusy-runner-");
        try
        {
            var tool = Path.Combine(folder.FullName, "tool");
            File.WriteAllText(tool, "#!/bin/sh\necho hi\n");

            var result = await runner.RunAsync(tool, [], Long, TestContext.Current.CancellationToken);

            Assert.Equal(new ProcessResult(ProcessStatus.NotStarted, 0, "", "Permission denied"), result);
        }
        finally
        {
            folder.Delete(recursive: true);
        }
    }

    // Linux takes no argument over 128 KiB, and macOS no more than a megabyte of them in all.
    [Fact]
    public async Task ACommandWhoseArgumentsAreTooLongIsNotStarted()
    {
        var result = await runner.RunAsync("true", [.. Enumerable.Repeat(new string('a', 200 * 1024), 40)], Long, TestContext.Current.CancellationToken);

        Assert.Equal(new ProcessResult(ProcessStatus.NotStarted, 0, "", "Argument list too long"), result);
    }

    // The system says that there is no such file of both, as it does of a command that is not installed.
    [Fact]
    public async Task ACommandWhoseFolderIsNotThereIsNotStarted()
    {
        var folder = Path.Combine(Path.GetTempPath(), "toobusy-runner-" + Guid.NewGuid().ToString("N"));

        var result = await runner.Inside(folder).RunAsync("ls", [], Long, TestContext.Current.CancellationToken);

        Assert.Equal(new ProcessResult(ProcessStatus.NotStarted, 0, "", $"the folder {folder} is not there"), result);
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
