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
}
