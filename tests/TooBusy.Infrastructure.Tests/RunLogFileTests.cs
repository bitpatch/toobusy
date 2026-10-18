using System.Globalization;
using TooBusy.Infrastructure.Run;

namespace TooBusy.Infrastructure.Tests;

public sealed class RunLogFileTests : IDisposable
{
    readonly DirectoryInfo folder = Directory.CreateTempSubdirectory("toobusy-log-");
    readonly FixedClock clock = new(FixedClock.Start);

    string Logs => Path.Combine(folder.FullName, "logs");

    // The log is named after the moment it opens, in the time of the machine.
    static string Name => $"run-{FixedClock.Start.ToLocalTime().ToString("yyyy-MM-dd-HH-mm-ss", CultureInfo.InvariantCulture)}.log";

    public void Dispose() => folder.Delete(recursive: true);

    [Fact]
    public void ALogIsNamedAfterTheMomentItOpensAndStandsAmongTheLogsOfTheLocalFolder()
    {
        using var log = new RunLogFile(folder.FullName, clock);

        Assert.Equal(Path.Combine(Logs, Name), log.FullPath);
        Assert.Equal($".toobusy/local/logs/{Name}", log.DisplayPath);
        Assert.Null(log.Unwritable);
        Assert.Equal([$"run started {FixedClock.Start.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss zzz", CultureInfo.InvariantCulture)}"], File.ReadAllLines(log.FullPath));
    }

    [Fact]
    public void ALineIsWrittenWithTheTimeOfDayAndTheRestOfATextStandsUnderIt()
    {
        var log = new RunLogFile(folder.FullName, clock);
        clock.Now += TimeSpan.FromMilliseconds(1500);

        log.Write("✖ Stopped: it broke\nSystem.Exception: boom\n   at Somewhere()");
        log.Dispose();

        var at = clock.Now.ToLocalTime().ToString("HH:mm:ss.fff", CultureInfo.InvariantCulture);
        Assert.Equal([$"{at} ✖ Stopped: it broke", "             System.Exception: boom", "                at Somewhere()"], File.ReadAllLines(log.FullPath).Skip(1));
    }

    [Fact]
    public void TheOlderLogsArePrunedToTenWhenALogOpens()
    {
        Directory.CreateDirectory(Logs);
        var old = Enumerable.Range(1, 13).Select(day => $"run-2029-12-{day.ToString("00", CultureInfo.InvariantCulture)}-09-00-00.log").ToList();
        foreach (var name in old)
            File.WriteAllText(Path.Combine(Logs, name), "old");
        File.WriteAllText(Path.Combine(Logs, "notes.txt"), "not a log");

        using var log = new RunLogFile(folder.FullName, clock);

        Assert.Equal(["notes.txt", .. old.Skip(4), Name], Directory.GetFiles(Logs).Select(Path.GetFileName).Order(StringComparer.Ordinal));
    }

    [Fact]
    public void ALogThatCannotBeOpenedSaysSoAndTakesWhatIsWrittenWithoutAWord()
    {
        File.WriteAllText(Logs, "a file where the folder of the logs should be");

        using var log = new RunLogFile(folder.FullName, clock);
        log.Write("anything");

        Assert.NotNull(log.Unwritable);
        Assert.False(File.Exists(log.FullPath));
    }

    [Fact]
    public void NothingIsWrittenAfterTheLogIsClosed()
    {
        var log = new RunLogFile(folder.FullName, clock);
        log.Write("before");
        log.Dispose();

        log.Write("after");
        log.Dispose();

        Assert.Equal(["before"], Logged.Lines(log));
        Assert.Null(log.Unwritable);
    }

    // A held text waits for the next one written, and is replaced by one held after it; the log writes it when it
    // closes, too.
    [Fact]
    public void AHeldTextIsWrittenBeforeTheNextOneAndWhenTheLogCloses()
    {
        var log = new RunLogFile(folder.FullName, clock);

        log.Hold("held 1");
        log.Hold("held 2");
        var before = log.Written;
        log.Write("next");
        log.Hold("held 3");
        log.Dispose();

        Assert.Equal(["held 2", "next", "held 3"], Logged.Lines(log));
        Assert.Equal(before + 1, log.Written);
    }

    [Fact]
    public async Task LinesFromManyThreadsLandWhole()
    {
        var log = new RunLogFile(folder.FullName, clock);

        await Task.WhenAll(Enumerable.Range(0, 8).Select(thread => Task.Run(() =>
        {
            for (var line = 0; line < 50; line++)
                log.Write($"thread {thread} line {line}");
        })));
        log.Dispose();

        var lines = File.ReadAllLines(log.FullPath).Skip(1).ToList();
        Assert.Equal(400, lines.Count);
        Assert.All(lines, line => Assert.Matches(@"^\d\d:\d\d:\d\d\.\d\d\d thread \d line \d+$", line));
    }
}
