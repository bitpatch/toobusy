using TooBusy.Core.Run;
using TooBusy.Infrastructure.Run;

namespace TooBusy.Infrastructure.Tests;

public sealed class RunSignFileTests : IDisposable
{
    static readonly DateTimeOffset Morning = new(2030, 1, 1, 9, 0, 0, TimeSpan.Zero);

    static readonly RunSign Working = new(RunPhase.Working)
    {
        Task = 12,
        Title = "Export the orders as a file",
        Url = "https://github.com/acme/rocket/issues/12",
        Open = "claude attach 1a2b3c4d",
    };

    readonly DirectoryInfo root = Directory.CreateTempSubdirectory("toobusy-sign-");

    // The processes that live on the machine of the test, each with the time it started.
    readonly Dictionary<int, DateTimeOffset?> living = new() { [100] = Morning, [200] = Morning + TimeSpan.FromMinutes(5) };

    public void Dispose() => root.Delete(recursive: true);

    [Fact]
    public void TheSignOfARunIsReadWhileItsProcessLives()
    {
        var sign = Sign(100);
        Assert.Null(sign.Read());

        Assert.Null(sign.Raise(new RunSign(RunPhase.Preparing)));
        Assert.Equal(new GoingRun(100, Morning + TimeSpan.FromHours(1), new RunSign(RunPhase.Preparing)), Sign(200).Read());

        sign.Change(Working);
        Assert.Equal(new GoingRun(100, Morning + TimeSpan.FromHours(1), Working), Sign(200).Read());

        var waits = Working with { Phase = RunPhase.WaitingForLimit, Until = Morning + TimeSpan.FromHours(2) };
        sign.Change(waits);
        Assert.Equal(waits, Sign(200).Read()?.Sign);

        sign.Change(Working with { Phase = RunPhase.WaitingForOwner });
        Assert.Equal(RunPhase.WaitingForOwner, Sign(200).Read()?.Sign.Phase);
        Assert.Equal([".gitignore", "run.json", "run.lock"], root.GetFiles().Select(file => file.Name).Order(StringComparer.Ordinal));
    }

    [Fact]
    public void TheTasksBesideTheFirstStandOnTheSignToo()
    {
        var sign = Sign(100);
        var waiting = new RunSign(RunPhase.WaitingForOwner) { Task = 13, Title = "Import the orders", Url = "https://github.com/acme/rocket/issues/13", Open = "claude attach 5e6f7a8b" };
        sign.Raise(Working with { Beside = [waiting] });

        var read = Sign(200).Read()?.Sign;

        Assert.Equal(Working with { Beside = [waiting] }, read);
        Assert.NotEqual(Working, read);
        Assert.Contains("#12 Export the orders as a file", read!.Told, StringComparison.Ordinal);
        Assert.Contains("#13 Import the orders · its session waits for the owner", read.Told, StringComparison.Ordinal);
    }

    [Fact]
    public void TheSignIsWrittenWithTheProcessAndTheTimes()
    {
        var sign = Sign(100);
        sign.Raise(Working);

        Assert.Equal(
            """{"process":100,"processStarted":"2030-01-01T09:00:00.0000000Z","since":"2030-01-01T10:00:00.0000000Z","phase":"working","task":12,"title":"Export the orders as a file","url":"https://github.com/acme/rocket/issues/12","open":"claude attach 1a2b3c4d"}""",
            File.ReadAllText(Path.Combine(root.FullName, "run.json")));

        sign.Change(new RunSign(RunPhase.WaitingForLimit) { Until = Morning + TimeSpan.FromHours(2) });

        Assert.Equal(
            """{"process":100,"processStarted":"2030-01-01T09:00:00.0000000Z","since":"2030-01-01T10:00:00.0000000Z","phase":"waiting-for-limit","until":"2030-01-01T11:00:00.0000000Z"}""",
            File.ReadAllText(Path.Combine(root.FullName, "run.json")));
    }

    // A session or a script of the project may take the local folder away while the run goes on, as `git clean -fdx`
    // does: the sign puts it back out of git, or the next task would find the working tree not clean over it.
    [Fact]
    public void TheLocalFolderComesBackOutOfGitWhenItWasTakenAwayDuringARun()
    {
        var local = Path.Combine(root.FullName, "local");
        var sign = Sign(100, folder: local);

        sign.Raise(new RunSign(RunPhase.Preparing));
        Assert.Equal("*\n", File.ReadAllText(Path.Combine(local, ".gitignore")));

        Directory.Delete(local, recursive: true);
        sign.Change(Working);

        Assert.Equal("*\n", File.ReadAllText(Path.Combine(local, ".gitignore")));
        Assert.Equal(Working, Sign(200, folder: local).Read()?.Sign);

        // A run of the menu after another one of the same process puts its sign up anew.
        Directory.Delete(local, recursive: true);
        sign.Raise(new RunSign(RunPhase.Preparing));

        Assert.Equal("*\n", File.ReadAllText(Path.Combine(local, ".gitignore")));
        Assert.Equal(RunPhase.Preparing, Sign(200, folder: local).Read()?.Sign.Phase);
    }

    [Fact]
    public void ASignThatCannotBeWrittenOrTakenDownIsAFailureOfTheLocalFiles()
    {
        // A folder stands where the sign should be.
        Directory.CreateDirectory(Path.Combine(root.FullName, "run.json", "inside"));
        var sign = Sign(100);

        var unwritten = Assert.Throws<LocalStateException>(() => sign.Raise(new RunSign(RunPhase.Preparing)));
        Assert.StartsWith("the sign of the run cannot be written: ", unwritten.Message, StringComparison.Ordinal);
        Assert.Throws<LocalStateException>(() => sign.Change(Working));

        var kept = Assert.Throws<LocalStateException>(sign.Lower);
        Assert.StartsWith("the sign of the run cannot be taken down: ", kept.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void TheSignIsTakenDownWhenTheRunEnds()
    {
        var sign = Sign(100);
        sign.Raise(Working);

        sign.Lower();

        Assert.Null(Sign(200).Read());

        // The lock of the sign stays, and so does the folder, out of git.
        Assert.Equal([".gitignore", "run.lock"], root.GetFiles().Select(file => file.Name).Order(StringComparer.Ordinal));
    }

    [Fact]
    public void ASecondRunIsGivenTheSignOfTheOneThatGoesOnAndPutsNoneUp()
    {
        Sign(100).Raise(Working);
        var second = Sign(200);

        Assert.Equal(Working, second.Raise(new RunSign(RunPhase.Preparing)));

        // The sign it found is not its own to take down.
        second.Lower();
        Assert.Equal(new GoingRun(100, Morning + TimeSpan.FromHours(1), Working), Sign(200).Read());
    }

    // The first run has looked and found no sign that counts; before it puts its own up, the second one starts. The
    // second waits for the first and finds its sign instead of putting up one of its own.
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task OfTwoRunsStartedTogetherOnlyOnePutsItsSignUp(bool overOneThatDied)
    {
        if (overOneThatDied)
        {
            living[300] = Morning;
            Sign(300).Raise(Working);
            living.Remove(300);
        }

        using var waits = new ManualResetEventSlim();
        Task<RunSign?>? second = null;
        var first = new RunSignFile(root.FullName, 100, id =>
        {
            if (id == 100 && second is null)
            {
                second = Task.Run(() => Sign(200, moment => { waits.Set(); Thread.Sleep(moment); }).Raise(new RunSign(RunPhase.Preparing)), TestContext.Current.CancellationToken);
                Assert.True(waits.Wait(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken));
            }

            return Start(id);
        }, () => Morning + TimeSpan.FromHours(1), Thread.Sleep);

        Assert.Null(first.Raise(Working));

        Assert.Equal(Working, await second!);
        Assert.Equal(100, Sign(200).Read()?.Process);
    }

    [Fact]
    public void ASignHeldForTooLongCannotBePutUp()
    {
        using var held = File.Open(Path.Combine(root.FullName, "run.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        var waited = 0;

        var stuck = Assert.Throws<LocalStateException>(() => Sign(100, _ => waited++).Raise(Working));

        Assert.StartsWith("the sign of the run cannot be written: ", stuck.Message, StringComparison.Ordinal);
        Assert.Equal(100, waited);
        Assert.False(File.Exists(Path.Combine(root.FullName, "run.json")));
    }

    [Fact]
    public void ARunOfTheSameProcessPutsItsSignUpAgain()
    {
        Sign(100).Raise(Working);

        Assert.Null(Sign(100).Raise(new RunSign(RunPhase.Preparing)));

        Assert.Equal(new RunSign(RunPhase.Preparing), Sign(200).Read()?.Sign);
    }

    [Fact]
    public void WhatARunThatDiedLeftBehindDoesNotCount()
    {
        Sign(100).Raise(Working);
        living.Remove(100);

        Assert.Null(Sign(200).Read());

        // The next run puts its own sign up over it.
        Assert.Null(Sign(200).Raise(new RunSign(RunPhase.Preparing)));
        Assert.Equal(200, Sign(100).Read()?.Process);
    }

    [Fact]
    public void AProcessThatGotTheNumberOfARunThatDiedIsNotThatRun()
    {
        Sign(100).Raise(Working);
        living[100] = Morning + TimeSpan.FromHours(3);

        Assert.Null(Sign(200).Read());
    }

    [Fact]
    public void AProcessWhoseStartCannotBeReadLivesAllTheSame()
    {
        Sign(100).Raise(Working);
        living[100] = null;

        Assert.Equal(Working, Sign(200).Read()?.Sign);
    }

    // A sign is read as it stands in the file, whatever wrote it: a run of another version of toobusy, in the middle of
    // an upgrade, for one. A start of the process that the system reads a little apart is the same start.
    [Fact]
    public void ASignWrittenByAnotherRunIsReadFromItsFile()
    {
        File.WriteAllText(
            Path.Combine(root.FullName, "run.json"),
            """{"process":100,"processStarted":"2030-01-01T09:00:01.0000000Z","since":"2030-01-01T09:30:00.0000000Z","phase":"waiting-for-owner","task":12,"title":"Export the orders as a file","url":"https://github.com/acme/rocket/issues/12","open":"claude attach 1a2b3c4d","until":"2030-01-01T09:45:00.0000000Z"}""");

        Assert.Equal(
            new GoingRun(100, Morning + TimeSpan.FromMinutes(30), Working with { Phase = RunPhase.WaitingForOwner, Until = Morning + TimeSpan.FromMinutes(45) }),
            Sign(200).Read());
    }

    [Theory]
    [InlineData("")]
    [InlineData("not json")]
    [InlineData("[]")]
    [InlineData("""{"phase":"working","task":12}""")]
    [InlineData("""{"process":0,"phase":"working"}""")]
    public void AFileThatCannotBeReadSaysNothing(string text)
    {
        File.WriteAllText(Path.Combine(root.FullName, "run.json"), text);

        Assert.Null(Sign(200).Read());
    }

    [Fact]
    public void TheProcessesAreThoseOfTheMachine()
    {
        // The process of the test lives; a number that no process has does not.
        new RunSignFile(root.FullName).Raise(Working);
        Assert.Equal(Environment.ProcessId, new RunSignFile(root.FullName).Read()?.Process);

        File.WriteAllText(Path.Combine(root.FullName, "run.json"), """{"process":2147483646,"since":"2030-01-01T09:00:00.0000000Z","phase":"working","task":12}""");
        Assert.Null(new RunSignFile(root.FullName).Read());
    }

    // The sign as the process with that number has it, in the folder of the test unless another is given; the time is
    // an hour after the morning.
    RunSignFile Sign(int process, Action<TimeSpan>? pause = null, string? folder = null) =>
        new(folder ?? root.FullName, process, Start, () => Morning + TimeSpan.FromHours(1), pause ?? Thread.Sleep);

    ProcessStart? Start(int process) => living.TryGetValue(process, out var started) ? new ProcessStart(started) : null;
}
