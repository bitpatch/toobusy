using TooBusy.Core.Queue;
using TooBusy.Core.Run;

namespace TooBusy.Core.Tests;

public class AboutTests
{
    static readonly QueueRules Rules = new(["manual", "draft"], ["feature", "bug"], "needs-owner", "interrupted", Board: true);

    static readonly QueueTask Sample = new(12, "<the title of the task>", "<the address of the task>", [], BoardStatus.Todo, "Todo", [], []);

    [Fact]
    public void TheMessagesAreThoseASessionIsSentWordForWord()
    {
        var text = About.Text(Rules, RunPolicy.Default);

        Assert.StartsWith("# How toobusy works\n\ntoobusy takes the tasks of a project's tracker one after another", text, StringComparison.Ordinal);
        Assert.Contains($"```text\n{Briefing.Task(Sample, interrupted: false)}\n```", text, StringComparison.Ordinal);
        Assert.Contains("```text\n- This task was interrupted before. The report of that session is the last comment of the task that starts with **Interrupted.**", text, StringComparison.Ordinal);
        Assert.Contains($"```text\n{Briefing.Alone(12)}\n```", text, StringComparison.Ordinal);
        Assert.Contains($"```text\n{Briefing.AfterLimit(12)}\n```", text, StringComparison.Ordinal);
        Assert.Contains($"```text\n{Briefing.WrapUp(12)}\n```", text, StringComparison.Ordinal);
        Assert.EndsWith("is the project's to say in its instructions.\n", text, StringComparison.Ordinal);
    }

    [Fact]
    public void TheRulesOfTheProjectAreNamed()
    {
        var text = About.Text(Rules, RunPolicy.Default);

        Assert.Contains(
            "- A task with one of the labels `manual`, `draft` is not taken.\n"
            + "- Only a task with one of the labels `feature`, `bug` is taken.\n"
            + "- The project has a board: a task is taken only from the status `Todo`.\n"
            + "- A task that has the `needs-owner` label waits for the owner and is not taken.\n",
            text,
            StringComparison.Ordinal);
        Assert.Contains("takes the `interrupted` label off a task that had it", text, StringComparison.Ordinal);
        Assert.Contains("the task gets the `interrupted` label and a comment that starts with `**Stopped by a usage limit**`", text, StringComparison.Ordinal);
        Assert.Contains("| `TOOBUSY: owner` | the working copy is clean | puts the `needs-owner` label on the task", text, StringComparison.Ordinal);
        Assert.Contains("| `TOOBUSY: interrupted` | the working copy is clean | puts the `interrupted` label on the task", text, StringComparison.Ordinal);
    }

    [Fact]
    public void AProjectWithoutABoardAndWithoutLabelsToTakeIsToldAsItIs()
    {
        var text = About.Text(new QueueRules(["manual"], [], "manual", "interrupted", Board: false), RunPolicy.Default);

        Assert.Contains("- A task with the label `manual` is not taken.\n- The project has no board: a task has no status, and where a status is named below, nothing is moved.\n", text, StringComparison.Ordinal);
        Assert.DoesNotContain("Only a task with", text, StringComparison.Ordinal);
    }

    [Fact]
    public void WithoutSettingsTheLabelsHaveNoNames()
    {
        var text = About.Text(null, RunPolicy.Default);

        Assert.Contains("No settings are read here, so its labels are not named below: `toobusy init` sets a project up.", text, StringComparison.Ordinal);
        Assert.Contains("- A task that has the label of the owner waits for the owner and is not taken.", text, StringComparison.Ordinal);
        Assert.Contains("takes the label of interrupted tasks off a task that had it", text, StringComparison.Ordinal);
    }

    [Fact]
    public void EveryLineForToobusyIsTold()
    {
        var text = About.Text(null, RunPolicy.Default);

        foreach (var line in new[] { "done", "partial", "owner", "interrupted", "failed <the reason>" })
            Assert.Contains($"| `TOOBUSY: {line}` |", text, StringComparison.Ordinal);
        Assert.Contains("after `TOOBUSY-REST:`", text, StringComparison.Ordinal);
    }

    [Fact]
    public void TheTimesAndTheCountsAreThoseOfThePolicy()
    {
        var text = About.Text(null, new RunPolicy { OwnerWait = TimeSpan.FromMinutes(5), Messages = 4, IdleMessages = 3, AbortGrace = TimeSpan.FromSeconds(30) });

        Assert.Contains("is left for 5 min, in which the owner may answer it.", text, StringComparison.Ordinal);
        Assert.Contains("A task is told so 4 times at most, and it is left to the owner sooner when 3 such messages in a row brought no step of its own.", text, StringComparison.Ordinal);
        Assert.Contains("A session that has not wrapped up in 30 s has its turn cut", text, StringComparison.Ordinal);
    }
}
