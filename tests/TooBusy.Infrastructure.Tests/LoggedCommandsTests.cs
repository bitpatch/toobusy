using TooBusy.Core.Run;
using TooBusy.Infrastructure.Processes;
using TooBusy.Infrastructure.Run;

namespace TooBusy.Infrastructure.Tests;

public sealed class LoggedCommandsTests : IDisposable
{
    static readonly Dictionary<string, string> Told = new()
    {
        ["TOOBUSY_TASK"] = "12",
        ["TOOBUSY_TASK_TITLE"] = "Export the data",
        ["TOOBUSY_TASK_URL"] = "https://github.com/acme/rocket/issues/12",
    };

    readonly DirectoryInfo folder = Directory.CreateTempSubdirectory("toobusy-log-");
    readonly FixedClock clock = new(FixedClock.Start);
    readonly RunLogFile log;

    public LoggedCommandsTests() => log = new RunLogFile(folder.FullName, clock);

    public void Dispose()
    {
        log.Dispose();
        folder.Delete(recursive: true);
    }

    [Fact]
    public async Task ACommandOfTheProjectIsWrittenWithWhatItIsToldAndItsFirstLines()
    {
        var commands = new LoggedCommands(new FakeCommands(_ =>
        {
            clock.Now += TimeSpan.FromSeconds(3);
            return new CommandEnd(CommandStatus.Exited, 0, "ports are free\n");
        }), log);

        await commands.RunAsync("scripts/dev.sh ports free", Told, TimeSpan.FromMinutes(5), TestContext.Current.CancellationToken);
        log.Dispose();

        Assert.Equal(
            ["$ scripts/dev.sh ports free · TOOBUSY_TASK=12 TOOBUSY_TASK_TITLE=\"Export the data\" TOOBUSY_TASK_URL=https://github.com/acme/rocket/issues/12", "→ the command exited 0 in 3 s", "ports are free"],
            Logged.Lines(log));
    }

    [Fact]
    public async Task ACommandThatFailedOrDidNotEndOrCouldNotBeRunIsSaidSo()
    {
        var ends = new Queue<CommandEnd>([new(CommandStatus.Exited, 1, "a\nb\n"), new(CommandStatus.TimedOut, 0, "still going"), new(CommandStatus.NotStarted, 0, "")]);
        var commands = new LoggedCommands(new FakeCommands(_ => ends.Dequeue()), log);

        for (var run = 0; run < 3; run++)
            await commands.RunAsync("make check", new Dictionary<string, string>(), TimeSpan.FromMinutes(5), TestContext.Current.CancellationToken);
        log.Dispose();

        Assert.Equal(
            [
                "$ make check", "→ the command exited 1 in 0 s", "a", "b",
                "$ make check", "→ the command did not end in 5 min, and was stopped", "still going",
                "$ make check", "→ the shell could not be run",
            ],
            Logged.Lines(log));
    }
}
