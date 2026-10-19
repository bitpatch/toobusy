using TooBusy.Core.Assistant;
using TooBusy.Core.Queue;
using TooBusy.Core.Run;

namespace TooBusy.Core.Tests;

// The tests of the run, in parts by what they are about: SupervisorTests.Branch.cs, .Commands.cs, .Queue.cs,
// .Outcomes.cs, .Counts.cs, .Tree.cs, .Tracker.cs, .Failures.cs, .Owner.cs, .Stop.cs, .Limits.cs, .Record.cs,
// .Closed.cs, .Watch.cs, .OneTask.cs and .Sign.cs. This part holds what they share: the fakes of the ports, the plan
// and the policy of the run, and the run itself.
public sealed partial class SupervisorTests : IDisposable
{
    const string Done = "Did it.\nTOOBUSY: done";
    const string Half = "Half of it; the rest is #9.\nTOOBUSY: partial";

    static readonly QueueRules Rules = new(["manual"], ["feature", "bug"], "needs-owner", "interrupted", Board: true);

    readonly FakeClock clock = new();
    readonly FakeTracker tracker = new();
    readonly FakeAssistant assistant = new();
    readonly FakeWorkspace workspace = new();
    readonly FakeMachine machine = new();
    readonly FakeState state = new();
    readonly FakeSign sign = new();
    readonly Log log = new();
    readonly FakeCommands shell;
    // The kill of the run, which the runner of the tests gives too: a run it stops is killed, and not left going.
    readonly CancellationTokenSource kill = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);

    RunPlan plan = new("v0.2.0", Rules, ModelChoice.AssistantsOwn, "high");

    // The policy of the tests, set here whole and not taken from the default of a run, so that a change of the
    // default fails only the tests that are about it. Its times are round and unlike each other, so that a time in
    // a line of the log says which of them it comes from, and a test that expects a time computes it from here, as
    // `Spoken.Time(policy.OwnerWait)`, and does not write the number out. A session is looked at every three
    // seconds: a task whose session works for a look and ends at the next takes one `Poll`.
    RunPolicy policy = new()
    {
        Poll = TimeSpan.FromSeconds(3),
        Tick = TimeSpan.FromSeconds(1),
        OwnerWait = TimeSpan.FromSeconds(90),
        Messages = 10,
        IdleMessages = 2,
        AbortGrace = TimeSpan.FromMinutes(10),
        Spent = 99,
        ResetMargin = TimeSpan.FromMinutes(1),
        UnknownLimitWait = TimeSpan.FromMinutes(30),
        LimitWaits = 5,
        CommandTime = TimeSpan.FromMinutes(15),
        KilledCommandTime = TimeSpan.FromSeconds(45),
        RetryPauses = [TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(30)],
    };

    Supervisor? run;

    public SupervisorTests() => shell = new FakeCommands(clock);

    // A test whose run did not come to the end of the play of a session fails over it: the play no longer says
    // what happened.
    public void Dispose()
    {
        kill.Dispose();
        foreach (var unplayed in assistant.Sessions.Select(session => session.Unplayed).OfType<string>())
            Assert.Fail($"The play went further than the run: {unplayed}.");
    }

    const string Free = "scripts/dev.sh ports free";
    const string Halt = "scripts/dev.sh server stop";

    static readonly TooBusy.Core.Settings.TaskCommands Around = new(Free, Halt);

    // The tasks that were told to be over, each with its mark.
    IEnumerable<(int Task, RunMark Mark)> Ended => log.Ends.Select(ended => (ended.Task, ended.Mark));

    // How long a task takes whose session works for a look, asks at the next, is told to go on alone when the
    // countdown is over, and works for a look again before it ends.
    TimeSpan OwnerWaited => 3 * policy.Poll + policy.OwnerWait;

    // Asserts what the tracker did, whatever the order it did it in. Most calls of a run come in an order that is no
    // behaviour, as the move of a task and its assignment, so a test that is about something else does not fail
    // when two of them swap. The orders that are behaviour have tests of their own, which read `tracker.Did` as it
    // is: the report in a task before it is closed, and the label of a task before it goes back to Todo.
    void AssertDid(params string[] calls) => Assert.Equal(calls.Order(StringComparer.Ordinal), tracker.Did.Order(StringComparer.Ordinal));

    // The same for what was done to one task, as among the calls of a run of several.
    void AssertDid(int task, params string[] calls) => Assert.Equal(calls.Order(StringComparer.Ordinal), tracker.DidTo(task).Order(StringComparer.Ordinal));

    // A moment a tick after the look of this number at the session of the first task, counting from one: what is
    // planned for it happens after that look and before the next, whatever the policy says of the looks.
    TimeSpan AfterLook(int look) => (look - 1) * policy.Poll + policy.Tick;

    // A time in whole seconds, as the end of a task is told with how long it took.
    static int Seconds(TimeSpan time) => (int)time.TotalSeconds;

    // The record of a task as a run writes it for the session the fake assistant gives the task.
    static TaskRecord Record(int task, RecordState left = RecordState.Working) =>
        new(task, new SessionTrace($"fake-{task}", $"conversation-{task}", FakeClock.Start), left);

    Supervisor Run => run ??= new Supervisor(tracker, assistant, workspace, state, sign, machine, clock, shell, plan, policy);

    Task<RunResult> RunAsync() => Run.RunAsync(log, kill.Token);
}
