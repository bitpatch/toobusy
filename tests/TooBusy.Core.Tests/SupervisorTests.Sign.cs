using TooBusy.Core.Run;

namespace TooBusy.Core.Tests;

// The sign of a run in the working copy, which tells another run and `toobusy status` what it does.
public sealed partial class SupervisorTests
{
    [Fact]
    public async Task ARunDoesNotStartWhileAnotherGoesOnInTheWorkingCopy()
    {
        tracker.Add(3);
        sign.Other = new RunSign(RunPhase.Working) { Task = 12, Title = "Export the orders as a file", Open = "claude attach 1a2b3c4d" };

        var result = await RunAsync();

        var why = "a run is going on in this working copy already: #12 Export the orders as a file · claude attach 1a2b3c4d";
        Assert.Equal(new RunResult(RunEnd.Problem, 0, why), result);
        Assert.Equal([$"✖ Did not start: {why}"], log.Lines);
        Assert.Empty(assistant.Started);
        Assert.Empty(tracker.Did);

        // The sign of the other run is not this one's to take down.
        Assert.Empty(sign.Written);
        Assert.Equal(RunPhase.Ended, log.Statuses[^1].Phase);
    }

    [Fact]
    public async Task ASessionThatWaitsForTheOwnerIsSaidOnTheSign()
    {
        tracker.Add(3);
        assistant.Session(3).Works().Asks().Then().Works().Ends(Done);

        await RunAsync();

        Assert.Contains(sign.Written, written => written is { Phase: RunPhase.WaitingForOwner, Task: 3, Open: "fake attach 3", Until: null });
    }

    [Theory]
    [InlineData(RunPhase.Preparing, null, null, false, "between tasks")]
    [InlineData(RunPhase.WaitingForLimit, null, null, false, "it waits for a usage limit")]
    [InlineData(RunPhase.Preparing, 12, null, false, "#12 Export the orders as a file")]
    [InlineData(RunPhase.Working, 12, "claude attach 1a2b3c4d", false, "#12 Export the orders as a file · claude attach 1a2b3c4d")]
    [InlineData(RunPhase.WaitingForOwner, 12, "claude attach 1a2b3c4d", false, "#12 Export the orders as a file · its session waits for the owner · claude attach 1a2b3c4d")]
    [InlineData(RunPhase.WaitingForLimit, 12, "claude attach 1a2b3c4d", false, "#12 Export the orders as a file · waits for a usage limit · claude attach 1a2b3c4d")]
    [InlineData(RunPhase.WaitingForLimit, 12, "claude attach 1a2b3c4d", true, "#12 Export the orders as a file · waits for a usage limit until 2030-01-01 10:01 UTC · claude attach 1a2b3c4d")]
    public void TheSignOfARunSaysInAFewWordsWhatTheRunDoes(RunPhase phase, int? task, string? open, bool until, string told)
    {
        var said = new RunSign(phase)
        {
            Task = task,
            Title = task is null ? null : "Export the orders as a file",
            Open = open,
            Until = until ? FakeClock.Start + TimeSpan.FromMinutes(61) : null,
        };

        Assert.Equal(told, said.Told);
    }
}
