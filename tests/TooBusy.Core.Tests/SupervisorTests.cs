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
    // The times the tests count in look at a session every three seconds, whatever a run of the tool does.
    RunPolicy policy = RunPolicy.Default with { Poll = TimeSpan.FromSeconds(3) };
    Supervisor? run;

    public SupervisorTests() => shell = new FakeCommands(clock);

    public void Dispose() => kill.Dispose();

    const string Free = "scripts/dev.sh ports free";
    const string Halt = "scripts/dev.sh server stop";

    static readonly TooBusy.Core.Settings.TaskCommands Around = new(Free, Halt);

    // The tasks that were told to be over, each with its mark.
    IEnumerable<(int Task, RunMark Mark)> Ended => log.Ends.Select(ended => (ended.Task, ended.Mark));

    // The record of a task as a run writes it for the session the fake assistant gives the task.
    static TaskRecord Record(int task, RecordState left = RecordState.Working) =>
        new(task, new SessionTrace($"fake-{task}", $"conversation-{task}", FakeClock.Start), left);

    Supervisor Run => run ??= new Supervisor(tracker, assistant, workspace, state, sign, machine, clock, shell, plan, policy);

    Task<RunResult> RunAsync() => Run.RunAsync(log, kill.Token);
}
