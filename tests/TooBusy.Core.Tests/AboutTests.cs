using TooBusy.Core.Queue;
using TooBusy.Core.Run;

namespace TooBusy.Core.Tests;

public class AboutTests
{
    static readonly QueueRules Rules = new(["manual", "draft"], ["feature", "bug"], "needs-owner", "interrupted", Board: true);

    static readonly NewTasks Ground = new("needs-owner", [], "<the milestone the user works on>", "<the address of the board>");

    static readonly QueueTask Sample = new(12, "<the title of the task>", "<the address of the task>", [], BoardStatus.Todo, "Todo", [], []);

    [Fact]
    public void TheMessagesAreThoseASessionIsSentWordForWord()
    {
        var text = About.Text(Rules, RunPolicy.Default);

        Assert.StartsWith("# How toobusy works\n\ntoobusy takes the tasks of a project's tracker one after another", text, StringComparison.Ordinal);
        Assert.Contains($"```text\n{Briefing.Task(Sample, Ground, interrupted: false)}\n```", text, StringComparison.Ordinal);
        Assert.Contains("```text\n- This task was interrupted before. The last comment of the task that starts with **Interrupted.** or with **Stopped by a usage limit** says how.", text, StringComparison.Ordinal);
        Assert.Contains("One that starts with **Stopped by a usage limit** has no report: the session before worked elsewhere, and its uncommitted changes are not here, so do the task from its description.\n```", text, StringComparison.Ordinal);
        Assert.Contains($"```text\n{Briefing.Alone(12, "needs-owner")}\n```", text, StringComparison.Ordinal);
        Assert.Contains($"```text\n{Briefing.AfterLimit(12)}\n```", text, StringComparison.Ordinal);
        Assert.Contains($"```text\n{Briefing.WrapUp(12)}\n```", text, StringComparison.Ordinal);
        Assert.Contains($"```text\n{Briefing.AfterStop(12)}\n```", text, StringComparison.Ordinal);
        Assert.Contains($"```text\n{Briefing.AfterAbort(12)}\n```", text, StringComparison.Ordinal);
        Assert.Contains("```text\n- A session worked on this task before, and its conversation is gone: it left no report.", text, StringComparison.Ordinal);
        Assert.EndsWith("is the project's to say, and so is which tasks it makes besides the one for the owner: in the skill a session starts with, as told under “A session starts with the skill of the project”, or in its instructions.\n", text, StringComparison.Ordinal);
    }

    [Fact]
    public void TheRulesOfTheProjectAreNamed()
    {
        var text = About.Text(Rules, RunPolicy.Default);

        Assert.Contains(
            "- A task with one of the labels `manual`, `draft` is not taken.\n"
            + "- Only a task with one of the labels `feature`, `bug` is taken.\n"
            + "- The project has a board: a task is taken only from the status `Todo`. The statuses a run knows are named below as the settings of the project call them, `Todo`, `In Progress` and `Done`.\n"
            + "- A task that has the `needs-owner` label waits for the owner and is not taken.\n",
            text,
            StringComparison.Ordinal);
        Assert.Contains("takes the `interrupted` label off a task that had it", text, StringComparison.Ordinal);
        Assert.Contains("the task gets the `interrupted` label and a comment that starts with `**Stopped by a usage limit**`", text, StringComparison.Ordinal);
        Assert.Contains("No limit holds a task back: a task is started however much of a limit is used.", text, StringComparison.Ordinal);
        Assert.Contains("in the language the instructions of the project call for; the words of toobusy around it are in English.", text, StringComparison.Ordinal);
        Assert.Contains("| `TOOBUSY: owner` | the working copy is clean | puts the `needs-owner` label on the task", text, StringComparison.Ordinal);
        Assert.Contains("| `TOOBUSY: interrupted` | the working copy is clean | puts the `interrupted` label on the task", text, StringComparison.Ordinal);
        Assert.Contains("| `TOOBUSY: failed <the reason>` | nothing | puts the `needs-owner` label on the task and a comment that says what went wrong, moves it back to `Todo` and stops the run;", text, StringComparison.Ordinal);
        Assert.Contains("The run stops in the same way, with the `needs-owner` label and such a comment on the task, when a check does not pass", text, StringComparison.Ordinal);
        Assert.Contains("A task that got the `needs-owner` label or a label that keeps a task out since is not taken. One that was closed since is settled when its session ended with a line for toobusy", text, StringComparison.Ordinal);
        Assert.Contains("A commit may close its task, as one with `Closes #12` in its message does when it is pushed", text, StringComparison.Ordinal);
        Assert.Contains("- `toobusy run` takes tasks until none is left. `toobusy run --max 3` takes three at most: a task counts when it is done, done in part or left to the owner. `toobusy run --task 12` does task #12 and no other, and only when a run would take it now:", text, StringComparison.Ordinal);
        Assert.Contains("- `toobusy queue` shows which tasks a run takes as things are, in the order it takes them, those that open later and those that are held, each with the reason, and changes nothing.", text, StringComparison.Ordinal);
        Assert.Contains("- `toobusy status` says whether a run is going on in the working copy, with the task it works on and the command that opens its session, and changes nothing.", text, StringComparison.Ordinal);
        Assert.Contains("It exits with 0 when no run is going on and with 3 while one is, and `toobusy status --json` prints the same for an assistant. A second run does not start in a working copy while one goes on.", text, StringComparison.Ordinal);
    }

    [Fact]
    public void TheStatusesAreNamedAsTheSettingsNameThem()
    {
        var text = About.Text(Rules with { Statuses = new BoardStatuses("Ready", "Doing", "Shipped") }, RunPolicy.Default);

        Assert.Contains("- The project has a board: a task is taken only from the status `Ready`. The statuses a run knows are named below as the settings of the project call them, `Ready`, `Doing` and `Shipped`.\n", text, StringComparison.Ordinal);
        Assert.Contains("Before a session starts, toobusy moves the task to `Doing`;", text, StringComparison.Ordinal);
        Assert.Contains("It has already moved the task to Doing, and", text, StringComparison.Ordinal);
        Assert.Contains("with the status `Ready`.", text, StringComparison.Ordinal);
        Assert.Contains("closes the task and moves it to `Shipped` |", text, StringComparison.Ordinal);
        Assert.Contains("and moves it back to `Ready`;", text, StringComparison.Ordinal);
        Assert.DoesNotContain("`Todo`", text, StringComparison.Ordinal);
        Assert.DoesNotContain("In Progress", text, StringComparison.Ordinal);
        Assert.DoesNotContain("`Done`", text, StringComparison.Ordinal);
    }

    [Fact]
    public void AProjectWithoutABoardAndWithoutLabelsToTakeIsToldAsItIs()
    {
        var text = About.Text(new QueueRules(["manual"], [], "manual", "interrupted", Board: false), RunPolicy.Default);

        Assert.Contains("- A task with the label `manual` is not taken.\n- The project has no board: a task has no status, and where a status is named below, nothing is moved.\n", text, StringComparison.Ordinal);
        Assert.DoesNotContain("Only a task with", text, StringComparison.Ordinal);
        Assert.Contains("- Put the label `manual` on it", text, StringComparison.Ordinal);
        Assert.DoesNotContain("- Put it on the board", text, StringComparison.Ordinal);
    }

    [Fact]
    public void WithoutSettingsTheLabelsHaveNoNames()
    {
        var text = About.Text(null, RunPolicy.Default);

        Assert.Contains("No settings are read here, so its labels are not named below, and the statuses of a board are named as GitHub names them on a new one, where the settings of a project may call them otherwise: `toobusy init` sets a project up.", text, StringComparison.Ordinal);
        Assert.Contains("- A task that has the label of the owner waits for the owner and is not taken.", text, StringComparison.Ordinal);
        Assert.Contains("takes the label of interrupted tasks off a task that had it", text, StringComparison.Ordinal);
        Assert.Contains("- Put the label `<the label of the owner>` on it", text, StringComparison.Ordinal);
        Assert.Contains("- Put it on the board <the address of the board> with the status `Todo`.", text, StringComparison.Ordinal);
        Assert.Contains("and put the label `<the label of the owner>` on it, so that no run takes it before the owner answers.", text, StringComparison.Ordinal);
    }

    [Fact]
    public void ItSaysWhatASessionMayDoInTheTrackerAndWhatIsTheProjectsToSay()
    {
        var text = About.Text(Rules, RunPolicy.Default);

        Assert.Contains("## The task of a session is kept by toobusy\n\nA session changes nothing of its own task:", text, StringComparison.Ordinal);
        Assert.Contains("## A session makes new tasks itself\n\nA session may make new tasks in the tracker, and toobusy makes none. toobusy asks for one of them: what cannot be done without the owner becomes a new task with the `needs-owner` label", text, StringComparison.Ordinal);
        Assert.Contains("Which other tasks a session makes and when, such as the findings of an audit or a proposal, and their labels, their wording and their relations, is the project's to say in its instructions.", text, StringComparison.Ordinal);
        Assert.Contains("An instruction to make other tasks is followed.", text, StringComparison.Ordinal);
    }

    [Fact]
    public void TheSkillOfTheProjectIsNamedAndItsFirstMessageBeginsWithItsCommand()
    {
        var text = About.Text(Rules, RunPolicy.Default, "task-auto");

        Assert.Contains("## A session starts with the skill of the project\n\nA project may name a skill in its settings, as `session.skill`, and then a session starts with it:", text, StringComparison.Ordinal);
        Assert.Contains("This project names the skill `task-auto`, so its first message begins with `/task-auto #12 <the title of the task>`, as shown below.", text, StringComparison.Ordinal);
        Assert.Contains($"```text\n{Briefing.Task(Sample, Ground, interrupted: false, skill: "task-auto")}\n```", text, StringComparison.Ordinal);
        Assert.Contains("```text\n/task-auto #12 <the title of the task>\n\nYou are doing task #12 of this project without its owner.", text, StringComparison.Ordinal);
        Assert.Contains("After the line of the skill it begins with the same words in every session", text, StringComparison.Ordinal);
    }

    [Fact]
    public void AProjectThatNamesNoSkillIsToldSoAndItsFirstMessageBeginsAsItDid()
    {
        var text = About.Text(Rules, RunPolicy.Default);

        Assert.Contains("This project names none: its first message begins with the words of toobusy, as shown below. `toobusy init` names one.", text, StringComparison.Ordinal);
        Assert.Contains("```text\nYou are doing task #12 of this project without its owner.", text, StringComparison.Ordinal);
        Assert.Contains("It begins with the same line in every session, but for the number", text, StringComparison.Ordinal);

        var unread = About.Text(null, RunPolicy.Default);
        Assert.Contains("No settings are read here, so it is not said whether this project names one: the first message is shown below as it is without a skill.", unread, StringComparison.Ordinal);
        Assert.Contains("```text\nYou are doing task #12 of this project without its owner.", unread, StringComparison.Ordinal);
    }

    [Fact]
    public void ItSaysWhatTheFirstMessageSaysAlreadyAndWhatIsTheSkillsToSay()
    {
        var text = About.Text(null, RunPolicy.Default);

        Assert.Contains("The skill is loaded once, at the start. The messages for a session that goes on do not name it, and a new session for a task whose conversation is gone starts with it again.", text, StringComparison.Ordinal);
        Assert.Contains("`toobusy doctor` checks that the skill is there, and no run starts while it is not", text, StringComparison.Ordinal);
        Assert.Contains("What that message says already, the skill does not say again, and does not say against:\n\n- that nobody answers,", text, StringComparison.Ordinal);
        Assert.Contains("- how the last reply ends: the lines for toobusy.\n\nWhat is the skill's to say:\n\n", text, StringComparison.Ordinal);
        Assert.Contains("- how the work of this project is done and checked without its owner:", text, StringComparison.Ordinal);
        Assert.Contains("- what is done before the work and after it,", text, StringComparison.Ordinal);
        Assert.Contains("- the language of the report;\n", text, StringComparison.Ordinal);
        Assert.Contains("- which tasks a session makes besides the one of what is left, with their labels, their wording and their relations.\n", text, StringComparison.Ordinal);
    }

    [Fact]
    public void TheCommandsOfTheProjectAroundATaskAreTold()
    {
        var text = About.Text(Rules, new RunPolicy { CommandTime = TimeSpan.FromMinutes(5), KilledCommandTime = TimeSpan.FromSeconds(20) }, commands: new("scripts/dev.sh ports free", "scripts/dev.sh server stop"));

        Assert.Contains("## The commands of the project around a task\n\nA project may have the run itself run a command before a task and one after it, `task.before` and `task.after` of its settings", text, StringComparison.Ordinal);
        Assert.Contains("This project runs `scripts/dev.sh ports free` before a task and `scripts/dev.sh server stop` after it.", text, StringComparison.Ordinal);
        Assert.Contains("`TOOBUSY_TASK` is the number of the task, `TOOBUSY_TASK_TITLE` its title and `TOOBUSY_TASK_URL` its address.", text, StringComparison.Ordinal);
        Assert.Contains("When it fails, the run stops and says so with what the command printed, and the task is not touched.", text, StringComparison.Ordinal);
        Assert.Contains("When it fails, the task stays as it ended, and the run stops.", text, StringComparison.Ordinal);
        Assert.Contains("A command is given up after 5 min, and counts as failed then. After a kill of the run, `after` has 20 s.", text, StringComparison.Ordinal);
        Assert.Contains("What these commands do, the instructions and the skill of a project do not tell a session to do as well", text, StringComparison.Ordinal);
        Assert.Contains("but for what the run does itself, as told under “The branch of the tasks” and “The commands of the project around a task”;", text, StringComparison.Ordinal);
    }

    [Fact]
    public void TheBranchOfTheTasksIsToldWithHowASessionBringsItUpToDateBeforeItsPush()
    {
        var text = About.Text(Rules, RunPolicy.Default, branch: "develop");

        Assert.Contains("## The branch of the tasks\n\nA project may name the branch its tasks are done on, `workspace.branch` of its settings. This project names `develop`.", text, StringComparison.Ordinal);
        Assert.Contains("On another one the run stops and says so: what is checked out is the owner's, and the run does not change it.", text, StringComparison.Ordinal);
        Assert.Contains("Before a task is taken, and before the command before it, the branch is brought up to date with its upstream", text, StringComparison.Ordinal);
        Assert.Contains("A task that a run goes on with, whose session may have left its changes in the working tree, is checked for the branch as any other, and nothing is moved under it.", text, StringComparison.Ordinal);
        Assert.Contains("its first message tells it to do so as the instructions of the project say, by a rebase or by a merge, and to push again.", text, StringComparison.Ordinal);
        Assert.Contains("- how the branch is brought up to date when it has moved on the remote by the time a session pushes: by a rebase or by a merge", text, StringComparison.Ordinal);
        Assert.Contains("The branch may have moved on the remote by the time you push: then bring it up to date as the instructions of the project say, and push again.", text, StringComparison.Ordinal);
        Assert.True(text.IndexOf("## The branch of the tasks", StringComparison.Ordinal) < text.IndexOf("## The commands of the project around a task", StringComparison.Ordinal));
    }

    [Fact]
    public void AProjectThatNamesNoBranchOrNoSettingsIsToldSo()
    {
        Assert.Contains("This project names none: its tasks are done on whatever is checked out, as it stands. `toobusy init --branch <name>` names one.", About.Text(Rules, RunPolicy.Default), StringComparison.Ordinal);
        Assert.Contains("`workspace.branch` of its settings. No settings are read here, so it is not said whether this project names one.", About.Text(null, RunPolicy.Default, branch: "develop"), StringComparison.Ordinal);
    }

    [Fact]
    public void ThePermissionModeOfASessionIsSaidWhereNobodyAnswersItsPrompts()
    {
        const string nobody = "Nobody answers its questions or its permission prompts.";
        const string kept = "What a session is allowed and refused in particular is said where Claude Code keeps it, in the settings of the project in `.claude/`, and toobusy does not say it again.";

        Assert.Contains(
            $"{nobody} This project names the permission mode `dontAsk` of Claude Code in its settings, `assistant.permission-mode`: a session is started in it, where whatever the settings of Claude Code do not allow is refused. {kept}",
            About.Text(Rules, RunPolicy.Default, permissionMode: "dontAsk"),
            StringComparison.Ordinal);
        Assert.Contains(
            "This project names the permission mode `auto` of Claude Code in its settings, `assistant.permission-mode`: a session is started in it, where a model of Claude Code looks at every action before it is done.",
            About.Text(Rules, RunPolicy.Default, permissionMode: "auto"),
            StringComparison.Ordinal);
        Assert.Contains(
            $"{nobody} This project names no permission mode, so a session is started in `auto` of Claude Code, where a model of Claude Code looks at every action before it is done. `toobusy init --permission-mode <mode>` names one: `auto` or `dontAsk`. {kept}",
            About.Text(Rules, RunPolicy.Default),
            StringComparison.Ordinal);
        Assert.Contains(
            $"{nobody} A session is started in the permission mode `auto` of Claude Code, where a model of Claude Code looks at every action before it is done, unless the settings of a project name another as `assistant.permission-mode`. No settings are read here, so it is not said whether this project names one. {kept}",
            About.Text(null, RunPolicy.Default, permissionMode: "dontAsk"),
            StringComparison.Ordinal);
    }

    [Fact]
    public void AProjectWithOneCommandOrNoneOrNoSettingsIsToldSo()
    {
        Assert.Contains("This project runs `make free` before a task, and nothing after it.", About.Text(Rules, RunPolicy.Default, commands: new("make free", null)), StringComparison.Ordinal);
        Assert.Contains("This project runs `make stop` after a task, and nothing before it.", About.Text(Rules, RunPolicy.Default, commands: new(null, "make stop")), StringComparison.Ordinal);
        Assert.Contains("This project has none: `toobusy init --before <command>` and `toobusy init --after <command>` set them.", About.Text(Rules, RunPolicy.Default), StringComparison.Ordinal);
        Assert.Contains("No settings are read here, so it is not said whether this project has them.", About.Text(null, RunPolicy.Default), StringComparison.Ordinal);
    }

    [Fact]
    public void EveryLineForToobusyIsTold()
    {
        var text = About.Text(null, RunPolicy.Default);

        foreach (var line in new[] { "done", "partial", "owner", "interrupted", "failed <the reason>" })
            Assert.Contains($"| `TOOBUSY: {line}` |", text, StringComparison.Ordinal);
        Assert.Contains("| `TOOBUSY: partial` | the same, and the report names another task, as `#34` or by its address: the task of what is left, which the session made | puts the report into the task as a comment, closes the task and moves it to `Done`; it makes no task |", text, StringComparison.Ordinal);
        Assert.DoesNotContain("TOOBUSY-REST", text, StringComparison.Ordinal);
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
