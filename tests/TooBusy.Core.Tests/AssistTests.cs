using System.Text.RegularExpressions;
using TooBusy.Core.Assistant;
using TooBusy.Core.Queue;
using TooBusy.Core.Settings;
using TooBusy.Core.Setup;

namespace TooBusy.Core.Tests;

public class AssistTests
{
    const string File = ".toobusy/settings.toml";

    const string Rocket = "https://github.com/orgs/acme/projects/1";

    // The parts of a step, in the order every step has them.
    static readonly string[] Parts = ["As things are", "Decide", "Look at", "Ask the owner", "Set it", "Check", "Next"];

    // A project that toobusy has never seen.
    static readonly AssistGround Unset = new(null, File, "acme/rocket");

    // One whose settings cannot be read.
    static readonly AssistGround Broken = new(
        new SettingsValidation(null, [new SettingsError("tracker.type", 4, "must be `github`"), new SettingsError(null, null, "the file is not TOML")]), File, "acme/rocket");

    static AssistGround Set(string? board = null, string? skill = null, TaskCommands? commands = null, string[]? take = null, string[]? blocking = null, string? branch = null, string? mode = null) => new(
        new SettingsValidation(
            new ProjectSettings(
                new TrackerSettings("github", board),
                new QueueSettings(new LabelSettings(blocking ?? ["question"], take ?? ["feature", "bug"], "manual", "interrupted")),
                new AssistantSettings("claude-code") { PermissionMode = mode })
            {
                Session = new SessionSettings(skill),
                Commands = commands ?? TaskCommands.None,
                Workspace = new WorkspaceSettings(branch),
            },
            []),
        File,
        "acme/rocket");

    public static TheoryData<int> Numbers => [.. Enumerable.Range(1, Assist.Steps.Count)];

    [Fact]
    public void TheIntroductionTellsWhatASetupIsMadeOfTheRulesTheListAndTheFirstStep()
    {
        var text = Assist.Introduction(Unset);

        Assert.StartsWith("# Setting a project up for toobusy\n\ntoobusy takes the tasks of a project's tracker one after another", text, StringComparison.Ordinal);
        Assert.Contains("How a run works is told by `toobusy about`, and is not said again here.", text, StringComparison.Ordinal);
        Assert.Contains("- the settings of the project, `.toobusy/settings.toml`:", text, StringComparison.Ordinal);
        Assert.Contains("- the choices of the owner: the milestone to work on, the model and the effort. They are kept on the owner's machine and are not committed;", text, StringComparison.Ordinal);
        Assert.Contains("- what is the project's own: its instructions, its skill and its commands around a task.", text, StringComparison.Ordinal);
        Assert.Contains(
            "- Go step by step, in order. A step whose “As things are” already says what the project needs is passed.\n"
            + "- Ask the owner whatever you do not know, and whatever a step says is theirs to decide.\n"
            + "- Set things with the commands the steps name, and do not write `.toobusy/settings.toml` by hand; a file that does not validate is the exception.\n"
            + "- Start no run: a run commits, pushes and closes tasks. Commit nothing of the setup before the owner agrees, where the instructions of the project say so.\n"
            + "- The steps are written in English; speak to the owner in their language.\n",
            text,
            StringComparison.Ordinal);
        Assert.Contains(
            """
            ## The steps

            1. **The machine.** `toobusy doctor` tells; the `origin` remote is acme/rocket.
            2. **Which tasks are taken.** Not set: there are no settings yet.
            3. **The board.** Not set: there are no settings yet.
            4. **The owner's own choices.** Milestone: not chosen; model: not chosen; effort: not chosen.
            5. **The instructions of the project.** The project's own; toobusy does not read them.
            6. **The skill a session starts with.** Not set: there are no settings yet.
            7. **The branch and the commands around a task.** Not set: there are no settings yet.
            8. **What a session may do.** Not set: there are no settings yet.
            9. **The last check.** `toobusy doctor` passes every check.

            `toobusy init --assist <n>` tells step `n` alone. The first one follows.

            ## Step 1 of 9: The machine

            """.ReplaceLineEndings("\n"),
            text,
            StringComparison.Ordinal);
        Assert.EndsWith("Next: `toobusy init --assist 2`\n", text, StringComparison.Ordinal);
        Assert.DoesNotContain("## Step 2", text, StringComparison.Ordinal);
        Assert.DoesNotContain("A run is going on", text, StringComparison.Ordinal);
    }

    [Fact]
    public void TheListSaysHowEveryStepStandsInAProjectThatIsSetUp()
    {
        var ground = Set(Rocket, "task-auto", new TaskCommands("scripts/dev.sh ports free", null), branch: "develop") with
        {
            Milestone = new MilestoneChoice("v1"),
            Model = ModelChoice.AssistantsOwn,
            Effort = "high",
            Skill = new SkillLook(true, ".claude/skills/task-auto/SKILL.md"),
        };

        var text = Assist.Introduction(ground);

        Assert.Contains(
            """
            2. **Which tasks are taken.** Take: `feature`, `bug`; blocking: `question`; manual: `manual`; interrupted: `interrupted`.
            3. **The board.** The board is https://github.com/orgs/acme/projects/1.
            4. **The owner's own choices.** Milestone: v1; model: the assistant's own; effort: high.
            5. **The instructions of the project.** The project's own; toobusy does not read them.
            6. **The skill a session starts with.** `task-auto`, written.
            7. **The branch and the commands around a task.** Branch: `develop`; before: `scripts/dev.sh ports free`; after: none.
            """.ReplaceLineEndings("\n"),
            text,
            StringComparison.Ordinal);
    }

    [Fact]
    public void TheListTellsAProjectWithoutABoardAndWithoutASkillAndASkillThatIsNotWritten()
    {
        var bare = Assist.Introduction(Set(take: [], blocking: []) with { Milestone = MilestoneChoice.None, Model = new ModelChoice("opus") });
        Assert.Contains("2. **Which tasks are taken.** Take: any task; blocking: none; manual: `manual`; interrupted: `interrupted`.\n3. **The board.** No board.\n4. **The owner's own choices.** Milestone: no milestone; model: opus; effort: not chosen.", bare, StringComparison.Ordinal);
        Assert.Contains("6. **The skill a session starts with.** No skill.\n7. **The branch and the commands around a task.** Branch: none; before: none; after: none.", bare, StringComparison.Ordinal);

        var unwritten = Assist.Introduction(Set(skill: "task-auto") with { Skill = new SkillLook(false, ".claude/skills/task-auto/SKILL.md") });
        Assert.Contains("6. **The skill a session starts with.** `task-auto`, not written yet.", unwritten, StringComparison.Ordinal);

        var elsewhere = Assist.Introduction(Unset with { Repository = null });
        Assert.Contains("1. **The machine.** `toobusy doctor` tells; the `origin` remote is not a GitHub repository.", elsewhere, StringComparison.Ordinal);
        Assert.Contains("As things are: the `origin` remote is not a GitHub repository, or there is none.", elsewhere, StringComparison.Ordinal);
    }

    [Fact]
    public void WhileARunGoesOnTheIntroductionSaysThatTheSetupWaitsForIt()
    {
        var text = Assist.Introduction(Unset with { Going = "#12 Export the orders as a file · claude attach 1a2b3c4d" });

        Assert.Contains(
            "**A run is going on in this working copy: #12 Export the orders as a file · claude attach 1a2b3c4d.** The setup waits for it: set nothing while it goes on. `toobusy status` exits with 0 when it is over.\n\n## The steps\n",
            text,
            StringComparison.Ordinal);
    }

    [Theory]
    [MemberData(nameof(Numbers))]
    public void EveryStepHasTheSamePartsUnderTheSameNamesAndEndsWithTheNextOne(int step)
    {
        foreach (var ground in (AssistGround[])[Unset, Broken, Set(), Set(Rocket, "task-auto", new TaskCommands("a", "b")) with { Skill = new SkillLook(true, "there") }])
        {
            var text = Assist.Step(step, ground);

            Assert.StartsWith($"## Step {step} of 9: {Assist.Steps[step - 1]}\n\n", text, StringComparison.Ordinal);
            Assert.EndsWith($"\n\nNext: `toobusy init --assist {step + 1}`\n", text, StringComparison.Ordinal);

            // The parts stand at the head of a paragraph, each once and in the order of the parts.
            var named = text.Split("\n\n").Select(paragraph => Parts.FirstOrDefault(part => paragraph.StartsWith(part + ":", StringComparison.Ordinal))).OfType<string>().ToList();
            Assert.Equal(Parts.Where(named.Contains), named);
            Assert.Equal("As things are", named[0]);
            Assert.Contains("Check", named);
        }
    }

    [Fact]
    public void ANumberAfterTheLastStepSaysThatAllTheStepsAreDoneAndNothingElse()
    {
        Assert.Equal("All the steps are done: the setup is complete.\n", Assist.Step(10, Unset));
        Assert.Equal("All the steps are done: the setup is complete.\n", Assist.Step(4711, Broken));
    }

    [Theory]
    [MemberData(nameof(Numbers))]
    public void TheErrorsOfSettingsThatDoNotValidateStandAtTheHeadOfEveryStep(int step)
    {
        const string Head = "**The settings of this project do not validate:**\n\n"
            + "- .toobusy/settings.toml:4: tracker.type: must be `github`\n"
            + "- .toobusy/settings.toml: the file is not TOML\n\n"
            + "Put `.toobusy/settings.toml` right by hand first: no command of the steps writes over settings it cannot read, and the steps cannot say how the project stands until then. `toobusy doctor` passes `settings` when the file is right.\n\n";

        Assert.StartsWith($"## Step {step} of 9: {Assist.Steps[step - 1]}\n\n{Head}As things are: ", Assist.Step(step, Broken), StringComparison.Ordinal);
        Assert.DoesNotContain("do not validate:**", Assist.Step(step, Set()), StringComparison.Ordinal);
        Assert.DoesNotContain("do not validate:**", Assist.Step(step, Unset), StringComparison.Ordinal);
    }

    [Fact]
    public void TheIntroductionSaysTheErrorsOfTheSettingsOnceOverItsList()
    {
        var text = Assist.Introduction(Broken);

        Assert.Single(text.Split("**The settings of this project do not validate:**").Skip(1));
        Assert.Contains("right.\n\n## The steps\n\n", text, StringComparison.Ordinal);
        Assert.Contains("2. **Which tasks are taken.** Not read: the settings do not validate.", text, StringComparison.Ordinal);
    }

    [Fact]
    public void TheFirstStepSaysThatTheCheckOfTheSettingsFailsUntilTheNextOne()
    {
        var first = Assist.Step(1, Unset);
        Assert.Contains("Ask the owner: for what only they can do:\n\n- the logins: `gh auth login`, and `claude`, then `/login`;\n- the `project` scope of the token, which a project with a board needs: `gh auth refresh -s project`;\n- running `claude` once in the folder of the project, so that Claude Code trusts it.", first, StringComparison.Ordinal);
        Assert.Contains("Its check of the settings fails until the next step, which writes them", first, StringComparison.Ordinal);
        Assert.DoesNotContain("Its check of the settings fails", Assist.Step(1, Set()), StringComparison.Ordinal);
    }

    [Fact]
    public void InAProjectWithoutSettingsTheStepOfTheLabelsWritesThemWithoutABoardAndWithoutASkillAndSaysWhy()
    {
        var text = Assist.Step(2, Unset with { Labels = ["bug", "feature", "docs", "question", "wontfix"] });

        Assert.Contains("As things are: not set: there are no settings yet. The labels of acme/rocket: `bug`, `feature`, `docs`, `question`, `wontfix`.\n", text, StringComparison.Ordinal);
        Assert.Contains("Set it:\n\n    toobusy init --yes --no-board --no-skill --take-label <label>,<label> --blocking-label <label>,<label> --manual-label manual --interrupt-label interrupted\n\n", text, StringComparison.Ordinal);
        Assert.Contains("`--no-board` and `--no-skill` stand there only because nothing is set yet: steps 3 and 6 set them.", text, StringComparison.Ordinal);
        Assert.Contains("What must not share a label: a label to take is not a blocking label; the manual label is not a label to take; the label of interrupted tasks is not a label to take, a blocking label or the manual label.", text, StringComparison.Ordinal);
        Assert.Contains("Ask the owner: show the four answers before you set them", text, StringComparison.Ordinal);
        Assert.Contains("Check: the command ends with `The settings are written to .toobusy/settings.toml.`, or with `Nothing to change`, and `toobusy doctor` passes `settings` and `labels`.", text, StringComparison.Ordinal);
    }

    [Fact]
    public void OverSettingsThatExistTheStepOfTheLabelsNamesTheOptionsOfTheLabelsAlone()
    {
        var text = Assist.Step(2, Set(Rocket, take: ["feature", "good first issue"]) with { Labels = ["feature", "question", "manual"] });

        Assert.Contains("As things are: the labels to take: `feature`, `good first issue`; the blocking labels: `question`; the manual label: `manual`; the label of interrupted tasks: `interrupted`. The labels of acme/rocket: `feature`, `question`, `manual`. The settings name `good first issue`, `interrupted`, which the repository does not have.\n", text, StringComparison.Ordinal);
        Assert.Contains("    toobusy init --yes --take-label \"feature,good first issue\" --blocking-label question --manual-label manual --interrupt-label interrupted\n", text, StringComparison.Ordinal);
        Assert.DoesNotContain("--no-board", text, StringComparison.Ordinal);
        Assert.DoesNotContain("--no-skill", text, StringComparison.Ordinal);
        Assert.Contains("What the command does not name stays as it is: the board, the skill, the branch and the commands around a task.", text, StringComparison.Ordinal);

        var none = Assist.Step(2, Set(take: [], blocking: []) with { Labels = [] });
        Assert.Contains("the labels to take: none, so any task is taken; the blocking labels: none;", none, StringComparison.Ordinal);
        Assert.Contains("The labels of acme/rocket: none.", none, StringComparison.Ordinal);
        Assert.Contains("    toobusy init --yes --no-take-labels --no-blocking-labels --manual-label manual --interrupt-label interrupted\n", none, StringComparison.Ordinal);
    }

    [Fact]
    public void WhatCannotBeReadIsSaidWithTheReasonAndTheStepThatPutsItRightAndTheStepIsToldWithoutIt()
    {
        var labels = Assist.Step(2, Unset with { Unread = "`gh` is not logged in to github.com." });
        Assert.Contains("As things are: not set: there are no settings yet. The labels of the repository cannot be read: `gh` is not logged in to github.com. Step 1 puts it right: `toobusy init --assist 1`. The step is told without them.\n", labels, StringComparison.Ordinal);
        Assert.Contains("Set it:\n\n    toobusy init --yes --no-board --no-skill", labels, StringComparison.Ordinal);

        var silent = Assist.Step(2, Set());
        Assert.Contains("The labels of the repository cannot be read: GitHub did not answer. Ask for the step again, or see step 1: `toobusy init --assist 1`. The step is told without them.", silent, StringComparison.Ordinal);

        var boards = Assist.Step(3, Set() with { Unread = "the GitHub command-line tool `gh` is not installed." });
        Assert.Contains("As things are: the project works without a board. The boards cannot be read: the GitHub command-line tool `gh` is not installed. Step 1 puts it right: `toobusy init --assist 1`.", boards, StringComparison.Ordinal);
        Assert.Contains("    toobusy init --yes --no-board\n", boards, StringComparison.Ordinal);

        var milestones = Assist.Step(4, Set() with { Unread = "the `origin` remote is not a GitHub repository.", Repository = null });
        Assert.Contains("The open milestones cannot be read: the `origin` remote is not a GitHub repository. Step 1 puts it right: `toobusy init --assist 1`.", milestones, StringComparison.Ordinal);
        Assert.Contains("    toobusy milestone --none\n", milestones, StringComparison.Ordinal);
    }

    [Fact]
    public void TheStepOfTheBoardShowsTheBoardsWithTheirStatusesAndMarksOneThatLacksAStatusOfARun()
    {
        var text = Assist.Step(3, Set(Rocket) with
        {
            Boards =
            [
                new AssistBoard(Rocket, "Rocket", true, ["Todo", "In progress", "Done"]),
                new AssistBoard("https://github.com/users/ann/projects/2", "Garden", false, ["Backlog", "Ready", "Done"]),
                new AssistBoard("https://github.com/users/ann/projects/3", "", false, []),
                new AssistBoard("https://github.com/users/ann/projects/4", "Attic", false, null),
                new AssistBoard("https://github.com/users/ann/projects/5", "Cellar", false, ["Open", "Closed"]),
            ],
        });

        Assert.Contains(
            """
            As things are: the board of the project is https://github.com/orgs/acme/projects/1, and the settings call its statuses `Todo`, `In Progress`, `Done`. The boards you can reach, each with what it calls its statuses:

            - Rocket, https://github.com/orgs/acme/projects/1, linked to acme/rocket: Todo, In progress, Done
            - Garden, https://github.com/users/ann/projects/2: Backlog, Ready, Done. **It is not named as GitHub names a board: the command says which status is which.**
            - https://github.com/users/ann/projects/3: it has no statuses. **A run needs three.**
            - Attic, https://github.com/users/ann/projects/4: its statuses cannot be read
            - Cellar, https://github.com/users/ann/projects/5: Open, Closed. **A run needs three statuses.**

            Decide: whether the project has a GitHub Projects board, and which: one of those you can reach, a new one, or none.
            """.ReplaceLineEndings("\n"),
            text,
            StringComparison.Ordinal);
        Assert.Contains("- With a board, a task is taken only from one status, the one to do, and a task that is not on the board is held.", text, StringComparison.Ordinal);
        Assert.Contains("- A run knows the three by what the settings call them, `tracker.statuses` there, whatever the case of their letters. A status the settings do not name is called as GitHub calls it on a new board: `Todo`, `In Progress` and `Done`.", text, StringComparison.Ordinal);
        Assert.Contains("The token needs the `project` scope.", text, StringComparison.Ordinal);
        Assert.Contains("Ask the owner: always: a board is seen by everyone who works in the project, a new one is made on GitHub", text, StringComparison.Ordinal);
        Assert.Contains("Which status is which is theirs to say for a board that is not named as GitHub names one. Whether the open tasks are put on the board, and into the status to do, is the owner's to decide too", text, StringComparison.Ordinal);
        Assert.Contains("Set it:\n\n    toobusy init --yes --board <URL>\n    toobusy init --yes --board <URL> --todo-status <name> --in-progress-status <name> --done-status <name>\n    toobusy init --yes --new-board \"<title>\" --board-owner <login>\n    toobusy init --yes --no-board\n\n", text, StringComparison.Ordinal);
        Assert.Contains("Check: `toobusy doctor` passes `board` and `statuses`, or skips both for a project without a board.", text, StringComparison.Ordinal);
    }

    [Fact]
    public void TheStepOfTheBoardGoesByTheStatusesTheSettingsName()
    {
        var set = Set(Rocket);
        var named = set with
        {
            Settings = new SettingsValidation(set.Settings!.Settings! with { Tracker = new TrackerSettings("github", Rocket) { Statuses = new BoardStatuses("Ready", "Doing", "Shipped") } }, []),
        };

        var has = Assist.Step(3, named with { Boards = [new AssistBoard(Rocket, "Rocket", true, ["Backlog", "ready", "Doing", "Shipped"])] });
        Assert.Contains("the settings call its statuses `Ready`, `Doing`, `Shipped`.", has, StringComparison.Ordinal);
        Assert.Contains("- Rocket, https://github.com/orgs/acme/projects/1, linked to acme/rocket: Backlog, ready, Doing, Shipped\n", has, StringComparison.Ordinal);

        var lacks = Assist.Step(3, named with { Boards = [new AssistBoard(Rocket, "Rocket", true, ["Todo", "In Progress", "Done", "Shipped"])] });
        Assert.Contains("Todo, In Progress, Done, Shipped. **It lacks `Ready`, `Doing`, which the settings name.**", lacks, StringComparison.Ordinal);
    }

    [Fact]
    public void TheStepOfTheBoardTellsAProjectWithoutOneAndOneWithoutSettingsAndBoardsThatAreNotThere()
    {
        var none = Assist.Step(3, Set() with { Boards = [] });
        Assert.Contains("As things are: the project works without a board. No board can be read: you can reach none that is open, or the token lacks the `project` scope. `gh auth refresh -s project` gives the scope, as step 1 says: `toobusy init --assist 1`. The step is told without the boards.\n", none, StringComparison.Ordinal);

        var unset = Assist.Step(3, Unset with { Boards = [new AssistBoard(Rocket, "Rocket", false, ["Todo", "In Progress", "Done"])] });
        Assert.Contains("As things are: not set: there are no settings yet. Step 2 writes the settings first: `toobusy init --assist 2`. The boards you can reach", unset, StringComparison.Ordinal);
        Assert.Contains("    toobusy init --yes --no-skill --board <URL>\n    toobusy init --yes --no-skill --board <URL> --todo-status <name> --in-progress-status <name> --done-status <name>\n    toobusy init --yes --no-skill --new-board \"<title>\" --board-owner <login>\n    toobusy init --yes --no-skill --no-board\n", unset, StringComparison.Ordinal);
        Assert.Contains("`--no-skill` stands there only because nothing is set yet: step 6 sets it.", unset, StringComparison.Ordinal);
    }

    [Fact]
    public void TheStepOfTheChoicesShowsTheOpenMilestonesWithTheirTasksAndEndsWithTheQueue()
    {
        var text = Assist.Step(4, Set() with
        {
            Milestone = new MilestoneChoice("v0"),
            Milestones = [new Milestone("v2", null, 1), new Milestone("v1", new DateOnly(2030, 3, 1), 12)],
        });

        Assert.Contains(
            """
            As things are: the milestone to work on: v0; the model: not chosen; the effort: not chosen. The open milestones of acme/rocket, each with its open tasks:

            - v1: 12 tasks, due 2030-03-01
            - v2: 1 task

            The milestone “v0” that is chosen is not open any more: it is chosen again.

            Decide: the three choices of the owner. They are kept on this machine and are not committed, so everyone who clones the project chooses their own.
            """.ReplaceLineEndings("\n"),
            text,
            StringComparison.Ordinal);
        Assert.Contains("- **The model** the tasks are done with: `fable`, `opus`, `sonnet`, `haiku`, any other name Claude Code takes, or the assistant's own default.\n- **The effort**: `low`, `medium`, `high`, `xhigh`, `max`.", text, StringComparison.Ordinal);
        Assert.Contains("Ask the owner: all three: they are theirs, and none of them is yours to choose.", text, StringComparison.Ordinal);
        Assert.Contains("Set it:\n\n    toobusy milestone \"<title>\"\n    toobusy milestone --none\n    toobusy model <name>\n    toobusy model --default\n    toobusy effort <level>\n\n", text, StringComparison.Ordinal);
        Assert.Contains("Check: `toobusy queue` shows which tasks a run would take", text, StringComparison.Ordinal);
        Assert.Contains("the walk goes back: to the labels, `toobusy init --assist 2`, or to the board, `toobusy init --assist 3`.", text, StringComparison.Ordinal);

        var chosen = Assist.Step(4, Set() with { Milestone = new MilestoneChoice("v1"), Model = new ModelChoice("opus"), Effort = "max", Milestones = [] });
        Assert.Contains("As things are: the milestone to work on: v1; the model: opus; the effort: max. acme/rocket has no open milestones.\n", chosen, StringComparison.Ordinal);
    }

    [Fact]
    public void TheStepOfTheInstructionsNamesNoCommandThatSetsItAndPointsAtAbout()
    {
        var text = Assist.Step(5, Set());

        Assert.Contains("Read `toobusy about` first", text, StringComparison.Ordinal);
        Assert.Contains("- that an assistant that works with the owner looks at `toobusy status` before it commits", text, StringComparison.Ordinal);
        Assert.Contains("that in a session of a run the task is kept by toobusy", text, StringComparison.Ordinal);
        Assert.Contains("- how a session brings its branch up to date when the branch has moved on the remote by the time it pushes: by a rebase or by a merge.", text, StringComparison.Ordinal);
        Assert.Contains("Ask the owner: what a session may do alone in this project, and what it must not. A kind of task that it must not do gets a blocking label, back in step 2: `toobusy init --assist 2`. A single task that waits for the owner gets the `manual` label.", text, StringComparison.Ordinal);
        Assert.Contains("Set it: no command of toobusy sets this step", text, StringComparison.Ordinal);
        Assert.DoesNotContain("toobusy init --yes", text, StringComparison.Ordinal);
    }

    [Fact]
    public void TheStepOfTheInstructionsTellsWhatBecomesOfAnOldWayOfRunningTheTasksWithoutTheOwner()
    {
        var text = Assist.Step(5, Set());

        Assert.Contains(
            "- where they describe another way of running the tasks of the project without its owner, a script of the project, the sessions it started, the line it read at the end of a reply, the labels and the statuses it set: they are changed to describe toobusy, or taken out;\n"
            + "- how a session brings its branch up to date",
            text,
            StringComparison.Ordinal);
        Assert.Contains(
            "Where the project ran its tasks without its owner before, the old way is found in a script of the project that starts sessions of the assistant, a section of its process on it, a folder of its state, and tasks of the tracker that are about it: `gh issue list --search \"<the name of the script>\"`.\n",
            text,
            StringComparison.Ordinal);
        Assert.Contains(
            "Where the project has an old way of running its tasks without the owner: whether the script, its state and its instructions are taken out of the project, and what becomes of the tasks about it. Nothing is deleted without the owner. Show the owner the changes of the instructions before they are kept.\n",
            text,
            StringComparison.Ordinal);
    }

    [Fact]
    public void TheStepOfTheSkillSaysWhetherItIsNamedAndWritten()
    {
        const string Place = ".claude/skills/task-auto/SKILL.md";

        var written = Assist.Step(6, Set(skill: "task-auto") with { Skill = new SkillLook(true, Place) });
        Assert.Contains($"As things are: the project names the skill `task-auto`, and it is written: {Place}.\n", written, StringComparison.Ordinal);
        Assert.Contains("Set it:\n\n    toobusy init --yes --skill <name>\n    toobusy init --yes --no-skill\n\n", written, StringComparison.Ordinal);
        Assert.Contains("`.claude/skills/<name>/SKILL.md`. To be loaded by its command, `/<name>`, the file begins with a front matter between two lines of `---` that has `name: <name>`, the name of its folder, and `description:`", written, StringComparison.Ordinal);
        Assert.Contains("Check: `toobusy doctor` passes `skill` and says where it found the skill, or skips it for a project that names none. `toobusy about` shows the first message of a session with the command of the skill at its head.", written, StringComparison.Ordinal);

        var unwritten = Assist.Step(6, Set(skill: "task-auto") with { Skill = new SkillLook(false, Place) });
        Assert.Contains($"As things are: the project names the skill `task-auto`, and it is not written yet: {Place} is where it goes. `toobusy doctor` fails and no run starts until it is there.\n", unwritten, StringComparison.Ordinal);

        var plugin = Assist.Step(6, Set(skill: "toolbox:task-auto") with { Skill = new SkillLook(null, "") });
        Assert.Contains("As things are: the project names the skill `toolbox:task-auto`, a skill of a plugin, which toobusy does not look for.\n", plugin, StringComparison.Ordinal);

        Assert.Contains("As things are: the project names no skill: a session starts with the message of toobusy alone.\n", Assist.Step(6, Set()), StringComparison.Ordinal);

        var unset = Assist.Step(6, Unset);
        Assert.Contains("As things are: not set: there are no settings yet.\n", unset, StringComparison.Ordinal);
        Assert.Contains("    toobusy init --yes --no-board --skill <name>\n    toobusy init --yes --no-board --no-skill\n", unset, StringComparison.Ordinal);
        Assert.Contains("`--no-board` stands there only because nothing is set yet: step 3 sets it.", unset, StringComparison.Ordinal);
    }

    [Fact]
    public void OverASkillThatIsWrittenTheStepOfTheSkillTellsToReadItAgainstAboutAndWhatIsTakenOutOfIt()
    {
        const string Place = ".claude/skills/task-auto/SKILL.md";

        var text = Assist.Step(6, Set(skill: "task-auto") with { Skill = new SkillLook(true, Place) });

        Assert.Contains(
            $"""
            `task-auto` is the name a first setup proposes.

            The skill is written already, and it may be older than toobusy: one that the sessions of a script of the project or of another tool started with. Read {Place} against `toobusy about`, under “A session starts with the skill of the project”, and change it so:

            - what the first message says already is taken out of it: that nobody answers, that the task is read in the tracker and kept by toobusy, the working copy and the plan of the work, what becomes of what cannot be done without the owner, and how the last reply ends;
            - what says against the first message is taken out or changed: a line a session ends its reply with for another tool, commands that move, assign, label, comment on or close the task of the session, asking the owner, and a worktree;
            - what is the project's stays: how the work is done and checked without the owner, what is done before the work and after it but for what the run does itself with the branch and the commands of step 7, the language of the report, and the other tasks a session makes;
            - where it names another tool, its script, its files or its commands, it names toobusy and its commands instead, as `toobusy queue` and `toobusy status`, or leaves them out.

            Look at:
            """.ReplaceLineEndings("\n"),
            text,
            StringComparison.Ordinal);
        Assert.Contains("Ask the owner: whether the project keeps its skill, and show them the skill as it will be before it is kept, with what is taken out of it and why: it says how their tasks are done without them.\n", text, StringComparison.Ordinal);
        Assert.Contains("`toobusy about` shows the first message of a session with the command of the skill at its head. The skill says nothing that `toobusy about` lists under what the first message says already, and nothing against it.\n", text, StringComparison.Ordinal);

        // A skill that is not written, one of a plugin and none keep the step as it was.
        foreach (var ground in (AssistGround[])[Set(skill: "task-auto") with { Skill = new SkillLook(false, Place) }, Set(skill: "toolbox:task-auto") with { Skill = new SkillLook(null, "") }, Set() with { Skill = new SkillLook(true, Place) }, Unset])
        {
            var step = Assist.Step(6, ground);
            Assert.DoesNotContain("The skill is written already", step, StringComparison.Ordinal);
            Assert.Contains("Ask the owner: whether the project gets a skill, and show them the skill before it is kept: it says how their tasks are done without them.\n", step, StringComparison.Ordinal);
            Assert.Contains("with the command of the skill at its head.\n", step, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void TheStepOfTheCommandsSaysWhatTheProjectHasAndTheRulesOfACommand()
    {
        var text = Assist.Step(7, Set(commands: new TaskCommands("scripts/dev.sh ports free", "scripts/dev.sh server stop")));

        Assert.Contains("As things are: the branch of the tasks: none; before a task: `scripts/dev.sh ports free`; after a task: `scripts/dev.sh server stop`.\n", text, StringComparison.Ordinal);
        Assert.Contains(
            """
            - it is one line for the shell, `sh -c`, run at the root of the working copy, after the branch is brought up to date;
            - it exits with 0 when all is well: a `before` that fails stops the run before the task is touched, and an `after` that fails stops the run after it;
            - it is given up after 10 min;
            - it learns the task from its environment: `TOOBUSY_TASK` is the number, `TOOBUSY_TASK_TITLE` the title and `TOOBUSY_TASK_URL` the address;
            - it gets no input and nobody answers it;
            - it leaves the working tree clean: a run starts no task over files that differ from what is committed.
            """.ReplaceLineEndings("\n"),
            text,
            StringComparison.Ordinal);
        Assert.Contains("Set it:\n\n    toobusy init --yes --branch <name> --before \"<command>\" --after \"<command>\"\n    toobusy init --yes --no-branch --no-before --no-after\n\n", text, StringComparison.Ordinal);
        Assert.Contains("Run each command by hand once", text, StringComparison.Ordinal);

        Assert.Contains("As things are: the branch of the tasks: none; before a task: none; after a task: none.\n", Assist.Step(7, Set()), StringComparison.Ordinal);
        Assert.Contains("    toobusy init --yes --no-board --no-skill --branch <name> --before \"<command>\" --after \"<command>\"\n", Assist.Step(7, Unset), StringComparison.Ordinal);
    }

    [Fact]
    public void TheStepOfTheCommandsTellsTheBranchInThePlaceOfACommandThatChecksIt()
    {
        var text = Assist.Step(7, Set(branch: "develop"));

        Assert.StartsWith("## Step 7 of 9: The branch and the commands around a task\n\n", text, StringComparison.Ordinal);
        Assert.Contains("As things are: the branch of the tasks: `develop`; before a task: none; after a task: none.\n", text, StringComparison.Ordinal);
        Assert.Contains("**The branch.** The branch the tasks are done on. With one named, a run checks before every task that the working copy is on it", text, StringComparison.Ordinal);
        Assert.Contains("The branch and its sync with the remote are not for a command: the branch does them.", text, StringComparison.Ordinal);
        Assert.DoesNotContain("that the working copy is on its branch and up to date with the remote", text, StringComparison.Ordinal);
        Assert.Contains("Ask the owner: which branch the tasks are done on, and before a command is set", text, StringComparison.Ordinal);
        Assert.Contains("Check: `git status --short --branch` shows the branch, and after it the upstream it follows, as `## develop...origin/develop`", text, StringComparison.Ordinal);
    }

    [Fact]
    public void TheStepOfWhatASessionMayDoNamesTheSettingOfTheModeAndItsOption()
    {
        var text = Assist.Step(8, Set());

        Assert.Contains("As things are: the permission mode is `auto`, as the settings name none. toobusy does not read the settings of Claude Code", text, StringComparison.Ordinal);
        Assert.Contains("A session of a run is a background session of Claude Code, and nobody answers its prompts: what it is refused is not done", text, StringComparison.Ordinal);
        Assert.Contains("- **The mode**, `assistant.permission-mode` of the settings. `auto` is the mode a session is started in when the settings name none", text, StringComparison.Ordinal);
        Assert.Contains("In `dontAsk` whatever the settings of Claude Code do not allow is refused", text, StringComparison.Ordinal);
        Assert.Contains("as `manual`, `acceptEdits` and `plan`, is not taken: a session in it would wait for an answer that never comes. Nor is `bypassPermissions`, which checks nothing.", text, StringComparison.Ordinal);
        Assert.Contains("the build, the tests and the other checks of the project, `git` with its commit and its push, and `gh`.", text, StringComparison.Ordinal);
        Assert.Contains("Ask the owner: which mode a session works in, and before anything more is allowed", text, StringComparison.Ordinal);
        Assert.Contains("Set it:\n\n    toobusy init --yes --permission-mode <mode>\n\nThe mode is `auto` or `dontAsk`; what the command does not name stays as it is.", text, StringComparison.Ordinal);
        Assert.Contains("Check: `toobusy about` names the mode under “A run”.", text, StringComparison.Ordinal);

        Assert.Contains("As things are: the permission mode is `dontAsk`. toobusy", Assist.Step(8, Set(mode: "dontAsk")), StringComparison.Ordinal);
        Assert.Contains("8. **What a session may do.** Permission mode: `dontAsk`; the settings of Claude Code in the project are not read by toobusy.", Assist.Introduction(Set(mode: "dontAsk")), StringComparison.Ordinal);

        // A project without settings is told why the command names more than its step.
        var first = Assist.Step(8, Unset);
        Assert.Contains("As things are: the permission mode is not set: there are no settings yet.", first, StringComparison.Ordinal);
        Assert.Contains("    toobusy init --yes --no-board --no-skill --permission-mode <mode>\n", first, StringComparison.Ordinal);
        Assert.Contains("`--no-board` and `--no-skill` stand there only because nothing is set yet", first, StringComparison.Ordinal);
    }

    [Fact]
    public void TheLastStepNamesWhatIsToBeCommittedAndProposesNothingMore()
    {
        var text = Assist.Step(9, Set(skill: "task-auto") with { Skill = new SkillLook(true, ".claude/skills/task-auto/SKILL.md") });

        Assert.Contains(
            "- `.toobusy/settings.toml`;\n"
            + "- the skill `task-auto`, .claude/skills/task-auto/SKILL.md;\n"
            + "- the instructions that were changed in step 5, and the settings of Claude Code that were changed in step 8;\n"
            + "- what was taken out of the project in steps 5 and 6, the script of the old way of running the tasks, its state and its instructions, where the owner agreed to it.\n\n",
            text,
            StringComparison.Ordinal);
        Assert.Contains("made as the instructions of the project say a commit is made", text, StringComparison.Ordinal);
        Assert.Contains("tell them in a few lines what was set", text, StringComparison.Ordinal);
        Assert.Contains("and that the setup is complete. Propose nothing after it: no demo, no first run and no next thing to try.", text, StringComparison.Ordinal);
        Assert.Contains("Check: `toobusy doctor` passes every check", text, StringComparison.Ordinal);
        Assert.EndsWith("Next: `toobusy init --assist 10`\n", text, StringComparison.Ordinal);

        Assert.Contains("- the skill, when the project has one;", Assist.Step(9, Set()), StringComparison.Ordinal);
    }

    [Theory]
    [MemberData(nameof(Numbers))]
    public void NoStepNamesAProjectAnotherToolOrItsScript(int step)
    {
        // Projects with nothing of their own to name: no repository, no commands, and a skill or none.
        AssistGround[] grounds =
        [
            Unset with { Repository = null },
            Set() with { Repository = null },
            Set(skill: "task-auto") with { Repository = null, Skill = new SkillLook(true, ".claude/skills/task-auto/SKILL.md") },
        ];

        foreach (var ground in grounds)
        {
            var text = Assist.Step(step, ground);

            Assert.DoesNotContain("acme", text, StringComparison.OrdinalIgnoreCase);
            foreach (var quoted in Regex.Matches(text, "`([^`]+)`").Select(match => match.Groups[1].Value))
            {
                // Neither the line another tool reads at the end of a reply nor a script.
                Assert.DoesNotMatch(@"^[A-Z][A-Z_]*:", quoted);
                Assert.DoesNotMatch(@"\.(cs|sh|py|ps1|js|ts|rb)\b", quoted);
            }
        }
    }

    [Fact]
    public void OnlyTheStepsThatChooseAmongWhatTheTrackerHasReadIt()
    {
        Assert.Equal(
            [AssistReading.Nothing, AssistReading.Labels, AssistReading.Boards, AssistReading.Milestones, AssistReading.Nothing, AssistReading.Nothing, AssistReading.Nothing, AssistReading.Nothing, AssistReading.Nothing, AssistReading.Nothing],
            Enumerable.Range(1, 10).Select(Assist.Reads));
    }
}
