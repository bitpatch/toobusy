using TooBusy.Core.Queue;

namespace TooBusy.Core.Run;

// The last look at a session that is over for this run; Paused tells that a usage limit stopped its task.
readonly record struct Watched(SessionLook Look, bool Paused = false);

// Watches the session of a task until its turn ends with an outcome, or until nothing more can come of it. What it
// knows of the session is what the fields below say: a look at the session is read against them, `Read`, and what that
// comes to moves them on.
sealed class SessionWatch(TaskRun run, QueueTask task, int queued, DateTimeOffset since, IAssistantSession session, ITaskHost host, TaskTools tools)
{
    // Whether the session works, as far as the run knows, or waits for the owner.
    enum Turn
    {
        Works,
        WaitsForOwner,
    }

    // How far the request to wrap up came after `/abort`: it was made, and the turn of the session was cut for it.
    enum WrapUp
    {
        NotAsked,
        Asked,
        Cut,
    }

    // What a look at the session comes to.
    enum Reading
    {
        // It ran into a usage limit.
        Limit,

        // Nothing more can come of it.
        Over,
        Works,

        // It has reported, and the assistant marks it as waiting for an answer that will not come.
        NoAnswer,

        // It stopped without saying how the task went.
        NewStop,

        // It still stands where it stopped.
        SameStop,
    }

    readonly int number = task.Number;
    readonly IClock clock = tools.Clock;
    readonly RunPlan plan = tools.Plan;
    readonly RunPolicy policy = tools.Policy;

    SessionLook look = new(SessionPhase.Working);
    DateTimeOffset next = tools.Clock.Now;

    // How many times the session was told to go on alone, how many of the last ones brought no step of its own, and
    // how many steps it had made when it was told last.
    int sent;
    int idle;
    int steps;
    int limitWaits;

    // When a session that waits for the owner is told to go on alone; null is never.
    Turn turn;
    DateTimeOffset? sendAt;
    bool unseen;

    WrapUp wrapUp;
    DateTimeOffset askedAt = tools.Clock.Now;

    public async Task<Watched> WatchAsync(CancellationToken kill)
    {
        while (true)
        {
            var heard = host.Hear();
            kill.ThrowIfCancellationRequested();

            run.Remember(session);

            if (host.Aborting && wrapUp == WrapUp.NotAsked)
                await AskToWrapUpAsync(kill);

            if (host.Aborting && wrapUp != WrapUp.Cut && (Waits || clock.Now - askedAt >= policy.AbortGrace) && await CutTurnAsync(kill) is { } refused)
                return refused;

            if (clock.Now >= next && await LookAsync(kill) is { } over)
                return over;

            if (Waits && heard.Hold && sendAt is not null)
                Hold();

            if (Waits && !host.Aborting && (heard.Nudge || (sendAt is { } due && clock.Now >= due)) && await TellAloneAsync(kill) is { } lost)
                return lost;

            await clock.DelayAsync(policy.Tick, kill);
        }
    }

    bool Waits => turn == Turn.WaitsForOwner;

    RunStatus Now() => new(Waits ? RunPhase.WaitingForOwner : RunPhase.Working, "")
    {
        Task = task,
        Since = since,
        Step = unseen ? "its session cannot be read" : look.Odd ?? look.Step,
        Context = look.Context,
        Plan = look.Plan ?? [],
        Queued = queued,
        Until = sendAt,
        Open = session.Open,
        Reply = Waits ? SessionWords.Said(look.Reply) : [],
    };

    // A session that works reads the request after its next step. One that waits makes no step, so its turn is cut at
    // once, as it is for one that did not wrap up in time.
    async Task AskToWrapUpAsync(CancellationToken kill)
    {
        wrapUp = WrapUp.Asked;
        askedAt = clock.Now;
        sendAt = null;
        if (Waits)
            return;

        await session.AskAsync(Briefing.WrapUp(number), kill);
        Say(RunMark.Interrupted, $"Asked #{number} to wrap up after its current step: nothing committed, a report in the task, the {plan.Rules.Labels.Interrupted} label");
    }

    // The end of the watch when the session does not go on with the request; otherwise null.
    async Task<Watched?> CutTurnAsync(CancellationToken kill)
    {
        wrapUp = WrapUp.Cut;
        Say(RunMark.Interrupted, Waits
            ? $"#{number} waits for the owner: its turn is cut, and it is told to wrap up"
            : $"#{number} did not wrap up in {Spoken.Time(policy.AbortGrace)}: its turn is cut, and it is asked again");
        turn = Turn.Works;
        if (await GoOnAsync(Briefing.WrapUp(number), "the request to wrap up", kill) is { } lost)
            return lost;

        host.Show(Now());
        return null;
    }

    // Looks at the session and moves on by what the look comes to: the end of the watch, or null while it goes on.
    async Task<Watched?> LookAsync(CancellationToken kill)
    {
        // The status line of the session tells of the limits as it works, so they are read with every look.
        look = await session.LookAsync(kill);
        run.Seen(session, look);
        host.ReadLimits();
        next = clock.Now + policy.Poll;
        run.Remember(session);
        if (look.Phase == SessionPhase.Unseen)
        {
            if (!unseen)
            {
                unseen = true;
                Say(RunMark.Paused, $"The sessions of the assistant cannot be read: #{number} may still be working · {session.Open}");
            }

            host.Show(Now());
            return null;
        }

        if (unseen)
            Say(RunMark.GoesOn, $"The sessions are read again: #{number} is watched");
        unseen = false;

        switch (Read(look, told: sent > 0, host.Aborting, cut: wrapUp == WrapUp.Cut, Waits))
        {
            case Reading.Limit:
                if (!await run.LimitAsync(session, look, look.Limit!, ++limitWaits, kill))
                    return new Watched(look, Paused: true);

                (turn, sendAt) = (Turn.Works, null);
                look = look with { Phase = SessionPhase.Working, Limit = null };
                next = clock.Now + policy.Poll;
                break;
            case Reading.Over:
                return new Watched(look);
            case Reading.Works:
                (turn, sendAt) = (Turn.Works, null);
                break;
            case Reading.NoAnswer:
                if (await TellNoAnswerAsync(kill) is { } lost)
                    return lost;
                break;
            case Reading.NewStop:
                if (StartWaiting() is { } left)
                    return left;
                break;
        }

        host.Show(Now());
        return null;
    }

    // A turn that ends without saying how the task went waits for the owner as a question does, and while the run
    // aborts its session gets the request to wrap up as a message, as one that waits does; a turn that ends so after
    // that message is over. One that the assistant marks as waiting for an answer and whose reply ends with a line for
    // toobusy does not wait: the session has reported, and there is nothing for the owner to answer. A session that was
    // told that no answer will come, and one that was asked to wrap up, has nothing more to do: such a reply ends the
    // task, whatever the assistant marks it as.
    static Reading Read(SessionLook look, bool told, bool aborting, bool cut, bool waits)
    {
        var silent = look.Phase == SessionPhase.Ended && Outcome.Of(look) is null && !cut;
        var reported = look is { Phase: SessionPhase.Asking, Reported: true };
        return look.Limit is not null && look.Phase != SessionPhase.Working ? Reading.Limit
            : look.Phase == SessionPhase.Lost || (look.Phase == SessionPhase.Ended && !silent) ? Reading.Over
            : reported && (told || aborting) ? Reading.Over
            : look.Phase == SessionPhase.Working ? Reading.Works
            : reported ? Reading.NoAnswer
            : !waits ? Reading.NewStop
            : Reading.SameStop;
    }

    // It is told so at once, with no countdown: nothing is asked of the owner.
    Task<Watched?> TellNoAnswerAsync(CancellationToken kill)
    {
        sent++;
        steps = look.Steps;
        (turn, sendAt) = (Turn.Works, null);
        var message = Briefing.NoAnswer(number, plan.Rules.Labels.Manual);
        Say(RunMark.Attention, $"#{number} ended its turn with a line for toobusy, but Claude Code marks it as waiting for an answer · it is told that none will come · {session.Open}");
        SessionWords.Quote(host, look.Reply, session);
        Say(RunMark.Note, $"“{message}”");
        return GoOnAsync(message, "the message", kill);
    }

    // A step of its own since it was told last makes this a new stop, not the old one again. While the run aborts, a
    // session that did not get the request to wrap up as a message yet is not left to the owner for the messages
    // before: it gets that one first. The end of the watch for a turn that ended and is left to the owner; otherwise
    // null.
    Watched? StartWaiting()
    {
        turn = Turn.WaitsForOwner;
        idle = sent > 0 && look.Steps == steps ? idle + 1 : 0;
        var why = host.Aborting && wrapUp != WrapUp.Cut ? null
            : idle >= policy.IdleMessages ? $"{policy.IdleMessages} messages in a row brought no step of its own"
            : sent >= policy.Messages ? $"it was told to go on alone {policy.Messages} times"
            : null;
        sendAt = why is null && !host.Aborting ? clock.Now + policy.OwnerWait : null;
        Say(RunMark.Paused, (look.Phase == SessionPhase.Asking ? $"#{number} waits for the owner: {look.Asks ?? "an answer is needed"}" : $"#{number} ended its turn without saying how the task went")
            + (sendAt is not null ? $" · it is told to go on alone in {Spoken.Time(policy.OwnerWait)}: /nudge does it now, /hold never"
                : why is not null ? $" · it is left to the owner: {why}"
                : "")
            + $" · {session.Open}");
        SessionWords.Quote(host, look.Reply, session);
        if (!host.Aborting)
            host.Notify($"#{number} waits for an answer");
        return look.Phase == SessionPhase.Ended && why is not null ? new Watched(look) : null;
    }

    void Hold()
    {
        sendAt = null;
        Say(RunMark.Paused, $"#{number} gets no message and waits for the owner · /nudge tells it to go on alone · {session.Open}");
        host.Show(Now());
    }

    // The end of the watch when the session does not go on with the message; otherwise null.
    async Task<Watched?> TellAloneAsync(CancellationToken kill)
    {
        sent++;
        steps = look.Steps;
        (turn, sendAt) = (Turn.Works, null);
        var message = Briefing.Alone(number, plan.Rules.Labels.Manual);
        Say(RunMark.GoesOn, $"#{number} is told to go on without the owner: its turn is cut, and this is the next message of its conversation · {session.Open}");
        Say(RunMark.Note, $"“{message}”");
        if (await GoOnAsync(message, "the message", kill) is { } lost)
            return lost;

        host.Show(Now());
        return null;
    }

    // Tells the session a message: it works from here on, and is looked at after the next pause. A session that does
    // not go on is lost, which ends the watch; `what` says what it did not go on with. Otherwise null.
    async Task<Watched?> GoOnAsync(string message, string what, CancellationToken kill)
    {
        var former = session.Trace.Id;
        var tell = await session.TellAsync(message, kill);
        if (!tell.Went)
        {
            Say(RunMark.Failed, $"#{number} did not go on with {what}: {tell.Why}");
            return new Watched(look with { Phase = SessionPhase.Lost });
        }

        if (tell.AsCopy)
            run.NoteCopy(former, session);

        look = look with { Phase = SessionPhase.Working };
        next = clock.Now + policy.Poll;
        return null;
    }

    void Say(RunMark mark, string text) => host.Say(mark, text);
}
