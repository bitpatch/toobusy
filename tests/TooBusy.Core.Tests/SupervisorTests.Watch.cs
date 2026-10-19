using TooBusy.Core.Run;

namespace TooBusy.Core.Tests;

// How a run looks at a session: the looks, what goes into the log, copies of a session and the status of the run.
public sealed partial class SupervisorTests
{
    // A run looks at a session every ten seconds unless it is told otherwise.
    [Fact]
    public void ASessionIsLookedAtEveryTenSeconds() => Assert.Equal(TimeSpan.FromSeconds(10), RunPolicy.Default.Poll);

    // How the run sees the session of its task goes into the log on disk when it changes, and not with every look.
    [Fact]
    public async Task HowTheSessionIsSeenIsTracedWhenItChanges()
    {
        tracker.Add(3);
        assistant.Session(3).Works(3).Asks("input needed").Then().Works(2).Ends(Done);

        await RunAsync();

        Assert.Equal(
            ["#3 session fake-3: working", "#3 session fake-3: working → asking (input needed)", "#3 session fake-3: asking (input needed) → working", "#3 session fake-3: working → ended"],
            log.Traces);
        Assert.DoesNotContain(log.Lines, line => line.Contains("session fake-3:", StringComparison.Ordinal));
    }

    // What the assistant cannot make of how a session stands, as a state it does not know, stands in the place of the
    // step of the session, and goes into the log with how the session is seen.
    [Fact]
    public async Task WhatTheAssistantCannotMakeOfASessionIsShownAndTraced()
    {
        const string Odd = "its session is in a state toobusy does not know: paused";
        tracker.Add(3);
        assistant.Session(3).Works().Works(odd: Odd).Lost(odd: Odd);

        await RunAsync();

        Assert.Equal(
            ["#3 session fake-3: working", $"#3 session fake-3: working → working ({Odd})", $"#3 session fake-3: working ({Odd}) → lost ({Odd})"],
            log.Traces);
        var steps = log.Statuses.Where(status => status.Phase == RunPhase.Working).Select(status => status.Step).ToList();
        Assert.Equal(["Editing a file", Odd], steps.Distinct().Where(step => step is not null));
    }

    [Fact]
    public async Task TheSessionOfAnEarlierRunIsTracedAtItsFirstLook()
    {
        state.Record = Record(5);
        tracker.Add(5);
        assistant.Session(5).Limit("You have hit your limit", SessionPhase.Lost).Then().Works().Ends(Done);

        await RunAsync();

        Assert.Equal("#5 session fake-5: lost (You have hit your limit)", log.Traces[0]);
    }

    // Claude Code may go on with a session as a copy, a new session with the whole conversation: the task is gone
    // on with by the copy, which the record names from then on.
    [Fact]
    public async Task ASessionThatGoesOnAsACopyIsTheSessionOfTheTaskFromThenOn()
    {
        state.Record = Record(5, RecordState.WrappedUp);
        tracker.Add(5, labels: ["feature", "interrupted"]);
        var session = assistant.Session(5);
        session.Copies = true;

        var result = await RunAsync();

        Assert.Equal(new RunResult(RunEnd.Emptied, 1), result);
        Assert.Equal([Briefing.AfterAbort(5)], session.Told);
        Assert.Contains("→ #5 Task 5 — taken again · its session goes on after /abort, as a copy of fake-5 · fake attach 5-copy", log.Lines);
        Assert.Contains(state.Written, record => record is { Session: { Id: "fake-5-copy", Conversation: "copy-5" } });
    }

    [Fact]
    public async Task ASessionThatGoesOnAsACopyInTheMiddleOfItsTaskIsNoted()
    {
        tracker.Add(3);
        var session = assistant.Session(3).Works().Asks().Then().Works().Ends(Done);
        session.Copies = true;

        var result = await RunAsync();

        Assert.Equal(new RunResult(RunEnd.Emptied, 1), result);
        Assert.Contains("  #3 goes on as a copy of fake-3: Claude Code started a new session with the whole conversation · fake attach 3-copy", log.Lines);
        Assert.Contains(state.Written, record => record is { Session.Id: "fake-3-copy" });
    }

    [Fact]
    public async Task ASessionThatGoesOnAsACopyAfterTheResetOfTheLimitIsSaidSo()
    {
        tracker.Add(3);
        var session = assistant.Session(3)
            .Works(and: () => assistant.Limits = new UsageLimits(new UsageWindow("5-hour", 100, FakeClock.Start + TimeSpan.FromHours(1)), null))
            .Limit("You have hit your limit · resets 10am")
            .Then().Works().Ends(Done);
        session.Copies = true;

        var result = await RunAsync();

        Assert.Equal(new RunResult(RunEnd.Emptied, 1), result);
        Assert.Contains("▶ #3 goes on after the reset of the limit, as a copy of fake-3 · fake attach 3-copy", log.Lines);
    }

    [Fact]
    public async Task ASessionThatDoesNotGoOnWithTheRequestToWrapUpSaysWhy()
    {
        tracker.Add(3);
        var session = assistant.Session(3).Works().Asks();
        session.Deaf = "it is deaf";
        clock.At(AfterLook(2), () => Run.Send(RunCommand.Abort));

        var result = await RunAsync();

        Assert.Equal([Briefing.WrapUp(3)], session.Told);
        Assert.Contains("✖ #3 did not go on with the request to wrap up: it is deaf", log.Lines);

        // The session is lost without a line for toobusy, and its task is left to the owner.
        Assert.Equal(new RunResult(RunEnd.Problem, 0, "the session of #3 did not say how the task went · fake attach 3"), result);
        AssertDid("status 3 InProgress", "assign 3", "label 3 +needs-owner", "comment 3", "status 3 Todo");
        Assert.Null(state.Record);
    }

    // What a session said goes into the log up to a number of lines; the rest is where the session is.
    [Fact]
    public async Task OnlyTheFirstLinesOfALongReplyAreQuotedAndTheSessionShowsTheWhole()
    {
        tracker.Add(3);
        assistant.Session(3).Ends(string.Join('\n', Enumerable.Range(1, 15).Select(line => $"line {line}")) + "\nTOOBUSY: failed it broke");

        await RunAsync();

        Assert.Equal(
            [.. Enumerable.Range(1, 12).Select(line => $"  │ line {line}"), "  │ … fake attach 3 shows the whole session"],
            log.Lines.Where(line => line.StartsWith("  │ ", StringComparison.Ordinal)));
    }

    [Fact]
    public async Task EveryTaskThatIsOverIsToldOnceWithHowItWentAndHowLongItTook()
    {
        tracker.Add(3);
        tracker.Add(5);
        tracker.Add(7);
        assistant.Session(3).Works().Ends(Done);
        assistant.Session(5).Works().Ends("Half of it; the rest is #9.\nTOOBUSY: partial");
        assistant.Session(7).Works().Asks("input needed", "Which key is it?").Then().Works().Ends("Still: which key?\nTOOBUSY: owner");

        await RunAsync();

        Assert.Equal([(3, RunMark.Done, Seconds(policy.Poll)), (5, RunMark.Partial, Seconds(policy.Poll)), (7, RunMark.Owner, Seconds(OwnerWaited))], log.Ends);
    }

    [Fact]
    public async Task ThePlanOfTheSessionIsInTheStatusAsItIsAtEveryLook()
    {
        tracker.Add(3);
        assistant.Session(3)
            .Works()
            .Works(plan: [PlanStep.Done, PlanStep.Active, PlanStep.Pending])
            .Works(plan: [PlanStep.Done, PlanStep.Done, PlanStep.Active, PlanStep.Pending])
            .Ends(Done);

        await RunAsync();

        var plans = log.Statuses.Where(status => status.Phase == RunPhase.Working).Select(status => string.Join(' ', status.Plan)).Distinct().ToList();
        Assert.Equal(["", "Done Active Pending", "Done Done Active Pending"], plans);
    }

    [Fact]
    public async Task TheStatusSaysWhatIsGoingOnAndWhichCommandsMeanSomething()
    {
        tracker.Add(3);
        tracker.Add(5);
        assistant.Session(3).Works(2).Asks().Then().Works().Ends(Done);
        clock.At(AfterLook(3), () => Run.Send(RunCommand.Stop));

        await RunAsync();

        Assert.Equal("Checking the working tree…", log.Statuses[0].Text);
        Assert.Contains(log.Statuses, status => status is { Phase: RunPhase.Preparing, Text: "Reading the queue…" });
        Assert.Contains(log.Statuses, status => status is { Phase: RunPhase.Preparing, Text: "Starting #3…", Task.Number: 3 });

        var working = log.Statuses.First(status => status.Phase == RunPhase.Working);
        Assert.Equal((3, "Editing a file", 1000L, 1, "fake attach 3"), (working.Task!.Number, working.Step, working.Context, working.Queued, working.Open));
        Assert.Equal(FakeClock.Start, working.Since);
        Assert.Equal(RunCommands.Stop | RunCommands.Abort, working.Available);

        var waiting = log.Statuses.First(status => status.Phase == RunPhase.WaitingForOwner);
        Assert.Equal(FakeClock.Start + 2 * policy.Poll + policy.OwnerWait, waiting.Until);
        Assert.Equal(RunCommands.Stop | RunCommands.Abort | RunCommands.Nudge | RunCommands.Hold, waiting.Available);

        var stopping = log.Statuses.First(status => status.Stopping);
        Assert.Equal(RunCommands.Continue | RunCommands.Abort | RunCommands.Nudge | RunCommands.Hold, stopping.Available);

        Assert.Equal((RunPhase.Ended, RunCommands.None), (log.Statuses[^1].Phase, log.Statuses[^1].Available));
    }

    [Fact]
    public async Task ARunOfOneTaskDoesItAndNoOtherAndEndsAsOneThatItsOwnerStopped()
    {
        plan = plan with { Only = 5 };
        tracker.Add(3);
        tracker.Add(5);
        tracker.Add(7);

        var result = await RunAsync();

        Assert.Equal(new RunResult(RunEnd.Stopped, 1, "Stopped after #5, as you asked"), result);
        Assert.Equal([5], assistant.Started.Select(start => start.Task));
        Assert.Equal("✻ v0.2.0 · #5 and no other task · the assistant's own model · high effort · auto mode", log.Lines[0]);
        Assert.Equal("‖ Stopped after #5, as you asked · 1 done", log.Lines[^2]);
        Assert.Equal(["Stopped after #5, as you asked: 1 done"], machine.Notified);

        // The queue is not read again: nothing is taken after the task.
        Assert.Single(tracker.Read);
        Assert.Equal(0, log.Statuses.First(status => status.Phase == RunPhase.Working).Queued);
    }
}
