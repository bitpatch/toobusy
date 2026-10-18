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
        Assert.Equal(["run.json"], root.GetFiles().Select(file => file.Name));
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
        Assert.Empty(root.GetFiles());
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

    // The sign as the process with that number has it; the time is an hour after the morning.
    RunSignFile Sign(int process) =>
        new(root.FullName, process, id => living.TryGetValue(id, out var started) ? new ProcessStart(started) : null, () => Morning + TimeSpan.FromHours(1));
}
