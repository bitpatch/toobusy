using TooBusy.Core.Queue;
using TooBusy.Core.Run;

namespace TooBusy.Core.Tests;

public class BriefingTests
{
    static readonly QueueTask Export = new(12, "Export the data", "https://github.com/acme/rocket/issues/12", ["feature"], BoardStatus.Todo, "Todo", [], []);

    static readonly NewTasks Ground = new("needs-owner", ["bug", "Feature"], "v0.2.0", "https://github.com/orgs/acme/projects/4");

    [Fact]
    public void TheFirstMessageNamesTheStatusesAsTheSettingsDo()
    {
        var message = Briefing.Task(Export, Ground with { Statuses = new BoardStatuses("Ready", "Doing", "Shipped") }, interrupted: false);

        Assert.Contains("It has already moved the task to Doing, and it assigns the task to the user who runs it.", message, StringComparison.Ordinal);
        Assert.Contains("- Put it on the board https://github.com/orgs/acme/projects/4 with the status `Ready`.", message, StringComparison.Ordinal);
    }

    [Fact]
    public void TheFirstMessageNamesTheTaskAndGivesTheRulesAndTheLastLine()
    {
        var message = Briefing.Task(Export, Ground, interrupted: false);

        Assert.StartsWith(
            "You are doing task #12 of this project without its owner. toobusy, the tool that takes the tasks of the project one after another, started this session and reads how it ends.\n\n"
            + "# Task #12: Export the data\n\nhttps://github.com/acme/rocket/issues/12\n\n"
            + "Read the task and its comments before you start: toobusy passes on nothing of them.\n\n"
            + "# Rules of this session\n\n- Nobody will answer.",
            message,
            StringComparison.Ordinal);
        Assert.Contains("Where the instructions of the project (CLAUDE.md, AGENTS.md, its skills and the like) tell you to ask the owner", message, StringComparison.Ordinal);
        Assert.Contains("Commit and push the finished work as they say. Where they differ from the rules of this session, these rules hold.\n", message, StringComparison.Ordinal);
        Assert.Contains("- Your task is kept by toobusy. It has already moved the task to In Progress, and it assigns the task to the user who runs it. Do not change the status, the assignee or the labels of the task, do not close it and do not comment on it", message, StringComparison.Ordinal);
        Assert.Contains("make no worktree", message, StringComparison.Ordinal);
        Assert.Contains("everything of yours is committed and pushed, or undone. The branch may have moved on the remote by the time you push: then bring it up to date as the instructions of the project say, and push again.\n", message, StringComparison.Ordinal);
        Assert.Contains("- Keep a plan of the work in your task list. Before you start, lay the work out there as a list of steps; mark a step when you begin it and when it is done", message, StringComparison.Ordinal);
        Assert.Contains("- `TOOBUSY: done`", message, StringComparison.Ordinal);
        Assert.Contains("- `TOOBUSY: partial`: a part is done, committed and pushed, and you have made the task of what is left. Name that task in the report by its number, as `#<number>`: toobusy closes task #12 only when the report names it.\n", message, StringComparison.Ordinal);
        Assert.DoesNotContain("TOOBUSY-REST", message, StringComparison.Ordinal);
        Assert.Contains("- `TOOBUSY: owner`", message, StringComparison.Ordinal);
        Assert.EndsWith("Leave the working copy as it is.", message, StringComparison.Ordinal);
        Assert.DoesNotContain("interrupted before", message, StringComparison.Ordinal);
    }

    [Fact]
    public void InAProjectWithASkillTheFirstMessageBeginsWithItsCommandAndGoesOnAsItDoes()
    {
        var plain = Briefing.Task(Export, Ground, interrupted: true, lost: true);

        var message = Briefing.Task(Export, Ground, interrupted: true, lost: true, skill: "task-auto");

        Assert.Equal("/task-auto #12 Export the data\n\n" + plain, message);
        Assert.StartsWith("You are doing task #12 of this project without its owner.", plain, StringComparison.Ordinal);
        Assert.Equal("/task-auto #12 Export the data", Briefing.Command("task-auto", Export));
    }

    [Fact]
    public void TheMessagesForASessionThatGoesOnNameNoSkill()
    {
        foreach (var message in new[] { Briefing.Alone(12, "needs-owner"), Briefing.AfterLimit(12), Briefing.AfterStop(12), Briefing.AfterAbort(12), Briefing.WrapUp(12) })
            Assert.False(message.StartsWith('/'));
    }

    [Fact]
    public void TheFirstMessageSaysThatASessionMakesNewTasksAndWhatTheOneForTheOwnerNeeds()
    {
        var message = Briefing.Task(Export, Ground, interrupted: false);

        Assert.Contains(
            "- You may make new tasks in the tracker. What cannot be done without the owner becomes one, as said below. "
            + "Which other tasks a session makes and when, and their labels, their wording and their relations, is said by the instructions of the project.\n",
            message,
            StringComparison.Ordinal);
        Assert.Contains(
            "# What cannot be done without the owner\n\n"
            + "Do everything that does not depend on the owner, leave nothing broken, and commit and push that part. What is left, make a new task of it yourself:\n\n"
            + "- Describe it so that a session that has never seen this one knows exactly what to do: what is left, what is done already and where, and the questions for the owner with the answers you see. Name task #12 in it.\n"
            + "- Put the label `needs-owner` on it, so that no run takes it before the owner answers, and the label `feature`, as task #12 has, so that a run takes it after that.\n"
            + "- Put it into the milestone `v0.2.0`, the one the owner works on.\n"
            + "- Put it on the board https://github.com/orgs/acme/projects/4 with the status `Todo`.\n"
            + "- Decide whether it blocks what task #12 blocks: toobusy closes task #12, and what waits for it opens.\n\n"
            + "When nothing at all can be done without the owner, make no task: undo your changes and say what you need.\n\n"
            + "# Your last reply",
            message,
            StringComparison.Ordinal);
    }

    [Fact]
    public void ANewTaskIsToldAsTheProjectAndTheTaskAre()
    {
        var plain = Export with { Labels = ["urgent"] };

        var bare = Briefing.Task(plain, new NewTasks("manual", [], null, null), interrupted: false);
        var own = Briefing.Task(plain with { Milestone = "v0.3.0" }, new NewTasks("manual", [], null, null), interrupted: false);
        var several = Briefing.Task(Export with { Labels = ["Bug", "feature", "needs-owner"] }, Ground with { Take = ["bug", "feature", "needs-owner"] }, interrupted: false);

        Assert.Contains("- Put the label `manual` on it, so that no run takes it before the owner answers.\n- Put it into no milestone, as task #12 is in none.\n- Decide whether", bare, StringComparison.Ordinal);
        Assert.Contains("- Put it into the milestone `v0.3.0`, the one of task #12.\n", own, StringComparison.Ordinal);
        Assert.Contains("answers, and the labels `Bug`, `feature`, as task #12 has, so that a run takes it after that.\n", several, StringComparison.Ordinal);
    }

    [Fact]
    public void ATaskWhoseSessionIsGoneIsToldOfTheChangesItMayFind()
    {
        var message = Briefing.Task(Export, Ground, interrupted: false, lost: true);

        Assert.Contains(
            "- A session worked on this task before, and its conversation is gone: it left no report. What it changed and did not commit may still be in the working copy: "
            + "the changes that `git status` shows before you start are its. Look at them with `git diff`, keep what is right, undo what is not, and go on from there.\n\n# What cannot be done without the owner",
            message,
            StringComparison.Ordinal);
        Assert.DoesNotContain("interrupted before", message, StringComparison.Ordinal);
        Assert.DoesNotContain("its conversation is gone", Briefing.Task(Export, Ground, interrupted: true), StringComparison.Ordinal);
    }

    [Fact]
    public void ASessionOfAnEarlierRunIsToldToGoOnInTheWayItWasLeft()
    {
        Assert.Equal(
            "toobusy was stopped while you worked, and it runs again: go on with task #12 from where you stopped. The uncommitted changes in the working copy are yours. "
            + "The rules of this session hold as before: nobody will answer, and your last reply ends with a `TOOBUSY:` line.",
            Briefing.AfterStop(12));
        Assert.Equal(
            "toobusy runs again: go on with task #12. You wrapped it up when the owner stopped the run, and your report is the last comment of the task that starts with **Interrupted.**: "
            + "do again what you undid, and do what is left. The rules of this session hold as before: commit and push the finished work as the instructions of the project say, "
            + "and end your last reply with a `TOOBUSY:` line.",
            Briefing.AfterAbort(12));
    }

    [Fact]
    public void ATaskThatWasInterruptedIsToldOfBothCommentsItMayHaveAndWhatToDoAfterEach()
    {
        var message = Briefing.Task(Export, Ground, interrupted: true);

        Assert.Contains(
            "- This task was interrupted before. The last comment of the task that starts with **Interrupted.** or with **Stopped by a usage limit** says how. "
            + "One that starts with **Interrupted.** is the report of the session before: go on with what it says is left, and do again what it says was undone. "
            + "One that starts with **Stopped by a usage limit** has no report: the session before worked elsewhere, and its uncommitted changes are not here, so do the task from its description."
            + "\n\n# What cannot be done without the owner",
            message,
            StringComparison.Ordinal);
    }

    [Fact]
    public void TheRuleOfATaskThatWasInterruptedNamesTheCommentsAsTheyAreWritten()
    {
        var message = Briefing.Task(Export, Ground, interrupted: true);

        foreach (var comment in new[] { Briefing.Comment(OutcomeKind.Interrupted, "Undone.", "s"), Briefing.Paused("the weekly limit is spent", "s") })
        {
            var head = comment[..(comment.IndexOf("**", 2, StringComparison.Ordinal) + 2)];
            Assert.Contains($"starts with {head}", message, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void TheOtherMessagesNameTheTaskAndTheLineToEndWith()
    {
        Assert.Equal(
            "The owner is away: no answer to your question and no approval will come. Decide yourself and go on with task #12 from where you stopped. "
            + "What cannot be decided without the owner, make a new task of it yourself: describe there what is left and your questions for the owner, "
            + "and put the label `needs-owner` on it, so that no run takes it before the owner answers. End your reply with a `TOOBUSY:` line.",
            Briefing.Alone(12, "needs-owner"));
        Assert.StartsWith("The usage limit has reset: go on with task #12", Briefing.AfterLimit(12), StringComparison.Ordinal);
        Assert.Contains("wrap task #12 up as soon as possible", Briefing.WrapUp(12), StringComparison.Ordinal);
        Assert.Contains("Commit nothing and push nothing. Undo your uncommitted changes", Briefing.WrapUp(12), StringComparison.Ordinal);
        Assert.Contains("`TOOBUSY: interrupted`", Briefing.WrapUp(12), StringComparison.Ordinal);
    }

    [Fact]
    public void TheCommentOfATaskSaysWhatBecameOfItAndNamesTheSession()
    {
        Assert.Equal("**Done.**\n\nIt is written.\n\n_Session: `claude attach 1a2b`_", Briefing.Comment(OutcomeKind.Done, "It is written.\n", "claude attach 1a2b"));
        Assert.Equal("**Done.**\n\n_Session: `claude attach 1a2b`_", Briefing.Comment(OutcomeKind.Done, "", "claude attach 1a2b"));
        Assert.StartsWith("**Done in part.** What is left is a task that the session made: the report names it.\n\nHalf, and #31.", Briefing.Comment(OutcomeKind.Partial, "Half, and #31.", "s"), StringComparison.Ordinal);
        Assert.StartsWith("**Waits for the owner.** Nothing could be done without you. Take the label `manual` off when this is answered", Briefing.Comment(OutcomeKind.Owner, "Which key?", "s", label: "manual"), StringComparison.Ordinal);
        Assert.StartsWith("**Interrupted.** Nothing is committed", Briefing.Comment(OutcomeKind.Interrupted, "Undone.", "s"), StringComparison.Ordinal);
        Assert.Equal(
            "**Stopped by a usage limit** (the weekly limit is spent). The uncommitted changes are left in the working copy, and the next run goes on with the same session.\n\n_Session: `claude attach 1a2b`_",
            Briefing.Paused("the weekly limit is spent", "claude attach 1a2b"));
        Assert.Equal(
            "**Failed.** The run stopped over this task: its session failed: the tests do not pass. Look at what went wrong, take the label `manual` off, and a run takes the task again.\n\nI tried twice.\n\n_Session: `claude attach 1a2b`_",
            Briefing.Stopped("its session failed: the tests do not pass", "I tried twice.", "claude attach 1a2b", "manual"));
    }
}
