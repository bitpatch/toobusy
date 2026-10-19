using TooBusy.Core.Assistant;
using TooBusy.Core.Queue;
using TooBusy.Core.Run;

namespace TooBusy.Core.Tests;

// A task from its start to its outcome: how it is taken, what its session is told first, and how the tracker
// is told how it went.
public sealed partial class SupervisorTests
{
    [Fact]
    public async Task TheStatusesAreNamedToTheSessionAndInTheLogAsTheSettingsNameThem()
    {
        plan = plan with { Rules = Rules with { Statuses = new BoardStatuses("Ready", "Doing", "Shipped") } };
        tracker.Add(3);

        await RunAsync();

        var start = Assert.Single(assistant.Started);
        Assert.Contains("moved the task to Doing", start.Message, StringComparison.Ordinal);
        Assert.Contains("  #3 is Doing on the board and assigned to the current user; the session is told that toobusy keeps its task", log.Lines);
        Assert.DoesNotContain("In Progress", start.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("Todo", start.Message, StringComparison.Ordinal);
    }

    // The golden of the log of a run: the one test that pins every line a run prints for a task, from the head to the
    // end. The other tests look at the lines they are about.
    [Fact]
    public async Task ATaskIsMovedBeforeItsSessionStartsAndClosedWithItsReportWhenItIsDone()
    {
        tracker.Add(3);

        var result = await RunAsync();

        Assert.Equal(new RunResult(RunEnd.Emptied, 1), result);
        Assert.Equal(["status 3 InProgress", "assign 3", "comment 3", "close 3", "status 3 Done"], tracker.Did);
        Assert.Equal((3, "**Done.**\n\nDid it.\n\n_Session: `fake attach 3`_"), Assert.Single(tracker.Comments));
        var start = Assert.Single(assistant.Started);
        Assert.Equal((3, "#3 Task 3", ModelChoice.AssistantsOwn, "high"), (start.Task, start.Name, start.Model, start.Effort));
        Assert.StartsWith("You are doing task #3", start.Message, StringComparison.Ordinal);
        Assert.Contains("# Task #3: Task 3\n\nhttps://example.com/3\n\nRead the task and its comments before you start", start.Message, StringComparison.Ordinal);
        Assert.Contains("moved the task to In Progress", start.Message, StringComparison.Ordinal);
        Assert.Equal(1, assistant.Session(3).Stopped);
        Assert.Equal(
            """
            ✻ v0.2.0 · 1 task in the queue · the assistant's own model · high effort · auto mode
            → #3 Task 3 — started · a new session · fake attach 3
              #3 is In Progress on the board and assigned to the current user; the session is told that toobusy keeps its task
            ✔ #3 Task 3 — done in 3 s
              #3 is closed, with the report of the session as its comment
            ✔ No task is left · 1 done
              Worked 3 s
            """.ReplaceLineEndings("\n"),
            log.Text);
    }

    [Fact]
    public async Task InAProjectWithASkillASessionStartsWithItsCommand()
    {
        plan = plan with { Skill = "task-auto" };
        tracker.Add(3);

        await RunAsync();

        Assert.StartsWith("/task-auto #3 Task 3\n\n", Assert.Single(assistant.Started).Message, StringComparison.Ordinal);
        Assert.Contains("→ #3 Task 3 — started · a new session with the skill task-auto · fake attach 3", log.Lines);
    }

    [Fact]
    public async Task WithoutABoardATaskThatIsTakenIsAssignedAllTheSame()
    {
        plan = plan with { Rules = Rules with { Board = false } };
        tracker.Add(3);

        var result = await RunAsync();

        Assert.Equal(new RunResult(RunEnd.Emptied, 1), result);
        Assert.Contains("assign 3", tracker.Did);
        Assert.Contains("  #3 is assigned to the current user; the session is told that toobusy keeps its task", log.Lines);
    }

    [Fact]
    public async Task TheTasksAreTakenOneAfterAnotherAndTheQueueIsReadBeforeEach()
    {
        tracker.Add(5);
        tracker.Add(3);

        var result = await RunAsync();

        Assert.Equal(2, result.Done);
        Assert.Equal([3, 5], assistant.Started.Select(start => start.Task));
        Assert.Equal(["v0.2.0", "v0.2.0", "v0.2.0"], tracker.Read);
        Assert.Equal(3, workspace.Read - 2);
    }

    [Theory]
    [InlineData("v0.3.0", "- Put it into the milestone `v0.3.0`, the one of task #3.\n")]
    public async Task WithoutAMilestoneANewTaskGoesWhereTheTaskOfTheSessionIs(string? milestone, string told)
    {
        plan = plan with { Milestone = null };
        tracker.Add(3, milestone: milestone);

        await RunAsync();

        Assert.Contains(told, Assert.Single(assistant.Started).Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AProjectWithoutABoardNamesNoneToASession()
    {
        plan = plan with { Rules = Rules with { Board = false }, Board = null };
        tracker.Add(3);

        await RunAsync();

        Assert.DoesNotContain("on the board", Assert.Single(assistant.Started).Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ADirtyTreeStopsTheRunBeforeATask()
    {
        tracker.Add(3);
        workspace.State = new WorkspaceState(2, 0);

        var result = await RunAsync();

        Assert.Equal(RunEnd.Problem, result.End);
        Assert.Equal("the working tree is not clean: commit or stash the changes first", result.Why);
        Assert.Contains("✖ Stopped: the working tree is not clean: commit or stash the changes first · the next task did not start · 0 done", log.Lines);
        Assert.Empty(assistant.Started);
        Assert.Empty(tracker.Did);
        Assert.Equal(["Stopped: the working tree is not clean: commit or stash the changes first"], machine.Notified);
    }

    [Theory]
    [InlineData("Half of it.")]
    public async Task APartWhoseReportNamesNoOtherTaskStopsTheRunAndLeavesTheTaskToTheOwner(string report)
    {
        tracker.Add(3);
        tracker.Add(5);
        assistant.Session(3).Works().Ends($"{report}\nTOOBUSY: partial");

        var result = await RunAsync();

        Assert.Equal(RunEnd.Problem, result.End);
        Assert.Equal("#3 said that a part is done and named no task of what is left · fake attach 3", result.Why);
        Assert.Equal(["status 3 InProgress", "assign 3", "label 3 +needs-owner", "comment 3", "status 3 Todo"], tracker.Did);
        Assert.StartsWith(
            $"**Failed.** The run stopped over this task: its session said that a part is done and named no task of what is left. Look at what went wrong, take the label `needs-owner` off, and a run takes the task again.\n\n{report}",
            tracker.Comments[0].Text,
            StringComparison.Ordinal);
        Assert.Equal([3], assistant.Started.Select(start => start.Task));
    }

    [Fact]
    public async Task ATaskNothingCouldBeDoneOfGetsTheLabelOfTheOwnerAndGoesBack()
    {
        tracker.Add(3);
        tracker.Add(5);
        assistant.Session(3).Works().Ends("Which key is it?\nTOOBUSY: owner");

        var result = await RunAsync();

        Assert.Equal(new RunResult(RunEnd.Emptied, 1), result);
        Assert.Equal(["status 3 InProgress", "assign 3", "label 3 +needs-owner", "comment 3", "status 3 Todo"], tracker.Did.Take(5));
        Assert.StartsWith("**Waits for the owner.** Nothing could be done without you. Take the label `needs-owner` off", tracker.Comments[0].Text, StringComparison.Ordinal);
        Assert.Contains("◇ #3 Task 3 — waits for the owner after 3 s", log.Lines);
        Assert.Equal([3, 5], assistant.Started.Select(start => start.Task));
    }

    [Fact]
    public async Task AFailedTaskStopsTheRunWithItsReason()
    {
        tracker.Add(3);
        tracker.Add(5);
        assistant.Session(3).Works().Ends("I tried twice.\nTOOBUSY: failed the tests do not pass");

        var result = await RunAsync();

        Assert.Equal(new RunResult(RunEnd.Problem, 0, "#3 failed · fake attach 3"), result);
        Assert.Contains("✖ #3 Task 3 — failed after 3 s: the tests do not pass", log.Lines);
        Assert.Contains("  │ I tried twice.", log.Lines);
        Assert.Equal(["status 3 InProgress", "assign 3", "label 3 +needs-owner", "comment 3", "status 3 Todo"], tracker.Did);
        Assert.Equal(
            (3, Briefing.Stopped("its session failed: the tests do not pass", "I tried twice.", "fake attach 3", "needs-owner")),
            Assert.Single(tracker.Comments));
        Assert.Single(assistant.Started);
        Assert.Equal([(3, RunMark.Failed, 3)], log.Ends);

        // A failure is for the owner: no run goes on with the task by itself.
        Assert.Equal([Record(3), null], state.Written);
    }

    [Fact]
    public async Task ASessionThatIsLostStopsTheRun()
    {
        tracker.Add(3);
        assistant.Session(3).Works().Lost("I was about to");

        var result = await RunAsync();

        Assert.Equal("the session of #3 did not say how the task went · fake attach 3", result.Why);
        Assert.Contains("✖ #3 Task 3 — ended after 3 s without saying how the task went", log.Lines);
        Assert.Null(state.Record);
        Assert.Equal(["status 3 InProgress", "assign 3", "label 3 +needs-owner", "comment 3", "status 3 Todo"], tracker.Did);
        Assert.Equal(
            (3, Briefing.Stopped("its session ended without saying how the task went", "I was about to", "fake attach 3", "needs-owner")),
            Assert.Single(tracker.Comments));
    }

    [Theory]
    [InlineData("I tried twice.\nTOOBUSY: failed the tests do not pass", 0)]
    [InlineData("What now?", 0)]
    [InlineData(Done, 1)]
    public async Task ATaskThatARunStoppedOverIsNotTakenByTheNextRunInAProjectWithoutABoard(string reply, int changes)
    {
        plan = plan with { Rules = Rules with { Board = false } };
        policy = policy with { Messages = 0 };
        tracker.Add(3);
        tracker.Add(5);
        assistant.Session(3).Works().Ends(reply);
        workspace.Next.Enqueue(new WorkspaceState(0, 0));
        workspace.Next.Enqueue(new WorkspaceState(changes, 0));

        Assert.Equal(RunEnd.Problem, (await RunAsync()).End);
        Assert.Equal([3], assistant.Started.Select(start => start.Task));

        // The next run, once the owner has put the working copy right: the label holds the task, and the next one
        // is taken.
        run = null;
        log.Lines.Clear();
        var result = await RunAsync();

        Assert.Equal(new RunResult(RunEnd.Emptied, 1), result);
        Assert.Equal([3, 5], assistant.Started.Select(start => start.Task));
        Assert.Contains("  Held: #3 it waits for the owner: it has the needs-owner label", log.Lines);

        // The owner takes the label off, and a run takes the task as any other.
        await tracker.RemoveLabelAsync(3, "needs-owner", CancellationToken.None);
        run = null;
        await RunAsync();

        Assert.Equal([3, 5, 3], assistant.Started.Select(start => start.Task));
    }

    [Fact]
    public async Task ASessionThatIsLostAfterItSaidHowTheTaskWentCounts()
    {
        tracker.Add(3);
        assistant.Session(3).Works().Lost(Done);

        Assert.Equal(new RunResult(RunEnd.Emptied, 1), await RunAsync());
    }

    // A line with something after it is no report: the session may have written it before it went on.
    [Fact]
    public async Task ASessionThatIsLostAfterAQuestionThatFollowsTheLineHasNotSaidHowTheTaskWent()
    {
        tracker.Add(3);
        assistant.Session(3).Works().Lost($"{Done}\n\nShould I also update the docs?");

        var result = await RunAsync();

        Assert.Equal("the session of #3 did not say how the task went · fake attach 3", result.Why);
        Assert.DoesNotContain("close 3", tracker.Did);
    }

    [Fact]
    public async Task ATrackerThatRefusesStopsTheRun()
    {
        tracker.Add(3);
        tracker.Add(5);
        tracker.Refused.Add("close");

        var result = await RunAsync();

        Assert.Equal(new RunResult(RunEnd.Problem, 0, "The tracker refused: close 3."), result);
        Assert.Single(assistant.Started);
        Assert.Equal([(3, RunMark.Failed)], Ended);

        // The record is forgotten, so the task is left to the owner: nothing else keeps the next run from it.
        Assert.Equal(["status 3 InProgress", "assign 3", "comment 3", "label 3 +needs-owner", "comment 3", "status 3 Todo"], tracker.Did);
        Assert.StartsWith("**Failed.** The run stopped over this task: the tracker refused something after its session: The tracker refused: close 3.", tracker.Comments[^1].Text, StringComparison.Ordinal);

        // A refusal stays: it is told at once, and the call is not made again.
        Assert.Single(tracker.Asked, asked => asked == "close 3");
        Assert.DoesNotContain(log.Lines, line => line.Contains("did not answer", StringComparison.Ordinal));
    }

    [Fact]
    public async Task SessionsThatCannotBeReadAreSaidOnce()
    {
        tracker.Add(3);
        assistant.Session(3).Works().Unseen(3).Works().Ends(Done);

        var result = await RunAsync();

        Assert.Equal(1, result.Done);
        Assert.Single(log.Lines, line => line == "‖ The sessions of the assistant cannot be read: #3 may still be working · fake attach 3");
        Assert.Contains("▶ The sessions are read again: #3 is watched", log.Lines);

        // While they cannot be read, what the run is doing says so in the place of the step of the session.
        var steps = log.Statuses.Where(status => status.Phase == RunPhase.Working).Select(status => status.Step).ToList();
        Assert.Contains("its session cannot be read", steps);
        Assert.NotEqual("its session cannot be read", steps[^1]);
    }
}
