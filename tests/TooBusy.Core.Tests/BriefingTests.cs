using TooBusy.Core.Queue;
using TooBusy.Core.Run;

namespace TooBusy.Core.Tests;

public class BriefingTests
{
    static readonly QueueTask Export = new(12, "Export the data", "https://github.com/acme/rocket/issues/12", ["feature"], BoardStatus.Todo, "Todo", [], []);

    [Fact]
    public void TheFirstMessageNamesTheTaskAndGivesTheRulesAndTheLastLine()
    {
        var message = Briefing.Task(Export, interrupted: false);

        Assert.StartsWith(
            "You are doing task #12 of this project without its owner. toobusy, the tool that takes the tasks of the project one after another, started this session and reads how it ends.\n\n"
            + "# Task #12: Export the data\n\nhttps://github.com/acme/rocket/issues/12\n\n"
            + "Read the task and its comments before you start: toobusy passes on nothing of them.\n\n"
            + "# Rules of this session\n\n- Nobody will answer.",
            message,
            StringComparison.Ordinal);
        Assert.Contains("Where the instructions of the project (CLAUDE.md, AGENTS.md, its skills and the like) tell you to ask the owner", message, StringComparison.Ordinal);
        Assert.Contains("Commit and push the finished work as they say. Where they differ from the rules of this session, these rules hold.\n", message, StringComparison.Ordinal);
        Assert.Contains("The tracker is kept by toobusy. It has already moved the task to In Progress. Do not change the status or the labels of the task, do not close it and do not comment on it", message, StringComparison.Ordinal);
        Assert.Contains("make no worktree", message, StringComparison.Ordinal);
        Assert.Contains("- Keep a plan of the work in your task list. Before you start, lay the work out there as a list of steps; mark a step when you begin it and when it is done", message, StringComparison.Ordinal);
        Assert.Contains("- `TOOBUSY: done`", message, StringComparison.Ordinal);
        Assert.Contains("put the line `TOOBUSY-REST: <the title of the new task>`", message, StringComparison.Ordinal);
        Assert.Contains("- `TOOBUSY: owner`", message, StringComparison.Ordinal);
        Assert.EndsWith("Leave the working copy as it is.", message, StringComparison.Ordinal);
        Assert.DoesNotContain("interrupted before", message, StringComparison.Ordinal);
    }

    [Fact]
    public void ATaskThatWasInterruptedIsToldWhereTheReportOfItsSessionIs()
    {
        var message = Briefing.Task(Export, interrupted: true);

        Assert.Contains(
            "- This task was interrupted before. The report of that session is the last comment of the task that starts with **Interrupted.**: go on with what it says is left, and do again what it says was undone.\n\n# What cannot be done without the owner",
            message,
            StringComparison.Ordinal);
    }

    [Fact]
    public void TheOtherMessagesNameTheTaskAndTheLineToEndWith()
    {
        Assert.StartsWith("The owner is away: no answer to your question and no approval will come. Decide yourself and go on with task #12", Briefing.Alone(12), StringComparison.Ordinal);
        Assert.EndsWith("End your reply with a `TOOBUSY:` line.", Briefing.Alone(12), StringComparison.Ordinal);
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
        Assert.StartsWith("**Done in part.** What is left is #31, which waits for the owner.\n\nHalf.", Briefing.Comment(OutcomeKind.Partial, "Half.", "s", 31), StringComparison.Ordinal);
        Assert.StartsWith("**Waits for the owner.** Nothing could be done without you. Take the label `manual` off when this is answered", Briefing.Comment(OutcomeKind.Owner, "Which key?", "s", label: "manual"), StringComparison.Ordinal);
        Assert.StartsWith("**Interrupted.** Nothing is committed", Briefing.Comment(OutcomeKind.Interrupted, "Undone.", "s"), StringComparison.Ordinal);
        Assert.Equal(
            "**Stopped by a usage limit** (the weekly limit is spent). The uncommitted changes are left in the working copy, and the next run goes on with the same session.\n\n_Session: `claude attach 1a2b`_",
            Briefing.Paused("the weekly limit is spent", "claude attach 1a2b"));
    }

    [Fact]
    public void TheTaskOfWhatIsLeftCarriesTheDescriptionOfTheOneItComesFrom()
    {
        var description = Briefing.Rest(new RestTask("Choose the format", "The dates are left.\n\n- [ ] ISO?"), Export, "Write it as CSV.\n");

        Assert.Equal(
            "The dates are left.\n\n- [ ] ISO?\n\n---\n\nLeft from #12 “Export the data”, which toobusy closed as done in part.\n\n"
            + "<details>\n<summary>The description of #12</summary>\n\nWrite it as CSV.\n\n</details>",
            description);
        Assert.DoesNotContain("<details>", Briefing.Rest(new RestTask("T", "D"), Export, " "), StringComparison.Ordinal);
        Assert.Equal("What is left of “Export the data”", Briefing.RestTitle(Export));
    }
}
