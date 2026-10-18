using System.Globalization;
using System.Text;
using TooBusy.Core.Assistant;
using TooBusy.Core.Queue;
using TooBusy.Core.Run;
using TooBusy.Core.Settings;

namespace TooBusy.Core.Setup;

// What a step of the walk chooses among, and so what is read from the tracker for it.
public enum AssistReading
{
    Nothing,
    Labels,
    Boards,
    Milestones,
}

// A board the user can reach, with what it calls its statuses; null when they cannot be read. The title is empty
// when it is not known.
public sealed record AssistBoard(string Address, string Title, bool Linked, IReadOnlyList<string>? Statuses);

// How a project stands when a step of the walk is asked for. Settings are those of the file, null when there is
// none, and Path is how the file is named. Repository is the one of the `origin` remote when it is a GitHub one.
// Skill is what is found of the skill the settings name. Going is what the sign of a run that goes on in the
// working copy says. The readings of the tracker are those of the step alone: each is null when the step does not
// choose among it or when it cannot be read, and Unread says why the tracker cannot be read at all.
public sealed record AssistGround(SettingsValidation? Settings, string Path, string? Repository)
{
    public MilestoneChoice? Milestone { get; init; }

    public ModelChoice? Model { get; init; }

    public string? Effort { get; init; }

    public SkillLook? Skill { get; init; }

    public string? Going { get; init; }

    public string? Unread { get; init; }

    public IReadOnlyList<string>? Labels { get; init; }

    public IReadOnlyList<AssistBoard>? Boards { get; init; }

    public IReadOnlyList<Milestone>? Milestones { get; init; }
}

// What `toobusy init --assist` prints: the walk through the setup of a project, told to the assistant of its owner
// one step at a time. It is a function of how the project stands and gives the text, as About does for a run: it
// asks for nothing itself and changes nothing. A step tells what the tool does at the time: a change of what can
// be set changes its step.
public static class Assist
{
    // The steps by their names, in their order.
    public static IReadOnlyList<string> Steps { get; } =
    [
        "The machine",
        "Which tasks are taken",
        "The board",
        "The owner's own choices",
        "The instructions of the project",
        "The skill a session starts with",
        "The branch and the commands around a task",
        "What a session may do",
        "The last check",
    ];

    // What a number after the last step is answered with.
    public const string Complete = "All the steps are done: the setup is complete.\n";

    // What the first setup of `toobusy init` proposes to call the two labels of a run and the skill.
    const string ProposedOwner = ProjectSetup.ProposedOwner;
    const string ProposedInterrupted = ProjectSetup.ProposedInterrupted;
    const string ProposedSkill = ProjectSetup.ProposedSkill;

    // What the step of this number chooses among in the tracker.
    public static AssistReading Reads(int step) => step switch
    {
        2 => AssistReading.Labels,
        3 => AssistReading.Boards,
        4 => AssistReading.Milestones,
        _ => AssistReading.Nothing,
    };

    // The command that prints the step of this number.
    public static string Command(int step) => string.Create(CultureInfo.InvariantCulture, $"toobusy init --assist {step}");

    // The introduction, the list of the steps with how each of them stands, and the first step. Nothing of the
    // tracker is in it.
    public static string Introduction(AssistGround ground)
    {
        var text = new StringBuilder();
        text.Append("""
            # Setting a project up for toobusy

            toobusy takes the tasks of a project's tracker one after another and has an AI coding assistant do each of them, in a session of its own and with nobody at the keyboard. This is the walk through its setup, for the assistant that sets a project up for its owner: it fits toobusy to the project you are in, one step at a time. How a run works is told by `toobusy about`, and is not said again here.

            A setup is made of three things:

            - the settings of the project, `.toobusy/settings.toml`: which tasks a run takes, the board, the skill a session starts with, the branch of the tasks, the commands around a task and the permission mode a session works in. They are committed, and everyone who clones the project gets the same;
            - the choices of the owner: the milestone to work on, the model and the effort. They are kept on the owner's machine and are not committed;
            - what is the project's own: its instructions, its skill and its commands around a task.

            ## The rules of the walk

            - Go step by step, in order. A step whose “As things are” already says what the project needs is passed.
            - Ask the owner whatever you do not know, and whatever a step says is theirs to decide.
            - Set things with the commands the steps name, and do not write `.toobusy/settings.toml` by hand; a file that does not validate is the exception.
            - Start no run: a run commits, pushes and closes tasks. Commit nothing of the setup before the owner agrees, where the instructions of the project say so.
            - The steps are written in English; speak to the owner in their language.
            - The walk changes nothing and keeps nothing, and has no record of the steps that are done: a step can be asked for again at any time, and tells how things stand then. So the same walk sets a project up for the first time and changes a setup that exists.


            """);
        if (ground.Going is { } going)
            text.Append(CultureInfo.InvariantCulture, $"**A run is going on in this working copy: {going}.** The setup waits for it: set nothing while it goes on. `toobusy status` exits with 0 when it is over.\n\n");
        text.Append(Broken(ground));

        text.Append("## The steps\n\n");
        string[] standing =
        [
            ground.Repository is { } repository ? $"`toobusy doctor` tells; the `origin` remote is {repository}" : "`toobusy doctor` tells; the `origin` remote is not a GitHub repository",
            LabelsInShort(ground),
            Stated(ground) is { } settings ? settings.Tracker.Board is { } board ? $"the board is {board}" : "no board" : Unstated(ground),
            $"milestone: {MilestoneInShort(ground.Milestone)}; model: {ModelInShort(ground.Model)}; effort: {ground.Effort ?? "not chosen"}",
            "the project's own; toobusy does not read them",
            SkillInShort(ground),
            Stated(ground) is { } kept ? $"branch: {Quoted(kept.Branch)}; before: {Quoted(kept.Commands.Before)}; after: {Quoted(kept.Commands.After)}" : Unstated(ground),
            Stated(ground) is { } mode ? $"permission mode: {ModeInShort(mode)}; the settings of Claude Code in the project are not read by toobusy" : Unstated(ground),
            "`toobusy doctor` passes every check",
        ];
        for (var index = 0; index < Steps.Count; index++)
            text.Append(CultureInfo.InvariantCulture, $"{index + 1}. **{Steps[index]}.** {Capital(standing[index])}.\n");
        text.Append(CultureInfo.InvariantCulture, $"\n`toobusy init --assist <n>` tells step `n` alone. The first one follows.\n\n");
        text.Append(StepText(1, ground, alone: false));
        return text.ToString();
    }

    // The step of this number alone, and after the last one that all of them are done.
    public static string Step(int step, AssistGround ground) => step > Steps.Count ? Complete : StepText(step, ground, alone: true);

    static string StepText(int step, AssistGround ground, bool alone)
    {
        var text = new StringBuilder();
        text.Append(CultureInfo.InvariantCulture, $"## Step {step} of {Steps.Count}: {Steps[step - 1]}\n\n");

        // The introduction has said it over its list already.
        if (alone)
            text.Append(Broken(ground));

        switch (step)
        {
            case 1:
                Machine(text, ground);
                break;
            case 2:
                TasksTaken(text, ground);
                break;
            case 3:
                Board(text, ground);
                break;
            case 4:
                Choices(text, ground);
                break;
            case 5:
                Instructions(text, ground);
                break;
            case 6:
                Skill(text, ground);
                break;
            case 7:
                Commands(text, ground);
                break;
            case 8:
                Permissions(text, ground);
                break;
            default:
                LastCheck(text, ground);
                break;
        }

        text.Append(CultureInfo.InvariantCulture, $"Next: `{Command(step + 1)}`\n");
        return text.ToString();
    }

    static void Machine(StringBuilder text, AssistGround ground)
    {
        Part(text, "As things are", ground.Repository is { } repository
            ? $"the `origin` remote is the GitHub repository {repository}: its issues are the tasks. The rest is told by `toobusy doctor`."
            : "the `origin` remote is not a GitHub repository, or there is none. The tasks of a project are the issues of the GitHub repository of its `origin` remote, so nothing can be set up before it is one. The rest is told by `toobusy doctor`.");
        Part(text, "Decide", "nothing: this step makes the machine ready. A run needs `git`, the GitHub command-line tool `gh` and Claude Code, `gh` and Claude Code logged in, and an `origin` remote that is a GitHub repository.");
        Part(text, "Look at", "what `toobusy doctor` prints: a line for each check, and under a failed one the command that fixes it. Run a fix that installs a tool yourself when the owner agrees.");
        Part(text, "Ask the owner", """
            for what only they can do:

            - the logins: `gh auth login`, and `claude`, then `/login`;
            - the `project` scope of the token, which a project with a board needs: `gh auth refresh -s project`;
            - running `claude` once in the folder of the project, so that Claude Code trusts it. `toobusy doctor` does not check this: a run finds it out when it starts its first session.
            """);
        Part(text, "Check", ground.Settings is null
            ? "`toobusy doctor` passes `git`, `GitHub CLI`, `GitHub login`, `Claude Code`, `Claude Code login` and `repository`. Its check of the settings fails until the next step, which writes them, and the checks that wait for the settings are skipped until then."
            : "`toobusy doctor` passes `git`, `GitHub CLI`, `GitHub login`, `Claude Code`, `Claude Code login` and `repository`. What it says of the settings, the board, the labels and the skill is the matter of the steps that follow.");
    }

    static void TasksTaken(StringBuilder text, AssistGround ground)
    {
        var settings = Stated(ground);
        var labels = settings?.Queue.Labels;
        var things = new StringBuilder(labels is null
            ? $"{Unstated(ground)}."
            : $"the labels to take: {Named(labels.Take, "none, so any task is taken")}; the blocking labels: {Named(labels.Blocking, "none")}; the label of the owner: `{labels.Owner}`; the label of interrupted tasks: `{labels.Interrupted}`.");
        if (ground.Labels is { } there)
        {
            things.Append(CultureInfo.InvariantCulture, $" The labels of {ground.Repository}: {Named(there, "none")}.");
            string[] named = labels is null ? [] : [.. labels.Take, .. labels.Blocking, labels.Owner, labels.Interrupted];
            if (named.Distinct(StringComparer.OrdinalIgnoreCase).Where(label => !there.Contains(label, StringComparer.OrdinalIgnoreCase)).ToList() is { Count: > 0 } missing)
                things.Append(CultureInfo.InvariantCulture, $" The settings name {Named(missing, "")}, which the repository does not have.");
        }
        else
        {
            things.Append(' ').Append(NotRead(ground, "The labels of the repository"));
        }

        Part(text, "As things are", things.ToString());
        Part(text, "Decide", """
            the four settings of the labels. `toobusy about` tells what a run does with them.

            - **The labels to take.** Only a task with one of them is taken. With none, any open task of the milestone is taken.
            - **The blocking labels.** A task with any of them is never taken: the kinds of task a session must not do alone, such as a question, a discussion or what needs a person at the machine.
            - **The label of the owner.** A run puts it on a task that cannot go on without the owner and on one that failed, and no run takes a task while it has it: the owner answers and takes it off. A session puts it on the task it makes of what is left for the owner.
            - **The label of interrupted tasks.** A run puts it on a task it had to stop, and the next run takes that task first.

            What must not share a label: a label to take is not a blocking label; the label of the owner is not a label to take; the label of interrupted tasks is not a label to take, a blocking label or the label of the owner. The labels to take and the blocking labels are labels the repository has. The other two are made in the repository by the command when it does not have them, which is why they are decided here, before the board: the first writing of the settings makes them.
            """);
        Part(text, "Look at", "`CONTRIBUTING.md`, `AGENTS.md`, `CLAUDE.md`, the issue templates, and how the open tasks are labelled: `gh issue list --limit 50`.");
        Part(text, "Ask the owner", "show the four answers before you set them, and say that the last two labels are made in the repository when it does not have them: everyone who works there sees them.");

        var take = labels is null ? "--take-label <label>,<label>" : labels.Take.Count == 0 ? "--no-take-labels" : $"--take-label {Joined(labels.Take)}";
        var blocking = labels is null ? "--blocking-label <label>,<label>" : labels.Blocking.Count == 0 ? "--no-blocking-labels" : $"--blocking-label {Joined(labels.Blocking)}";
        var line = $"{take} {blocking} --owner-label {Word(labels?.Owner ?? ProposedOwner)} --interrupt-label {Word(labels?.Interrupted ?? ProposedInterrupted)}";
        Set(text, ground.Settings is null
            ? [$"toobusy init --yes --no-board --no-skill {line}"]
            : [$"toobusy init --yes {line}"]);
        var notes = new StringBuilder();
        notes.Append(labels is null
            ? $"Put the answers in place of what stands in angle brackets; `{ProposedOwner}` and `{ProposedInterrupted}` are the names a first setup proposes."
            : "The command names the labels as they are set now: change what is to change.");
        notes.Append(" An option that names labels names all of them, separated by commas. `--no-take-labels` in place of `--take-label` takes any task, and `--no-blocking-labels` in place of `--blocking-label` blocks nothing.");
        if (ground.Settings is null)
            notes.Append(" `--no-board` and `--no-skill` stand there only because nothing is set yet: steps 3 and 6 set them.");
        else
            notes.Append(" What the command does not name stays as it is: the board, the skill, the branch and the commands around a task.");
        text.Append(notes).Append("\n\n");
        Part(text, "Check", $"the command ends with `The settings are written to {ground.Path}.`, or with `Nothing to change`, and `toobusy doctor` passes `settings` and `labels`.");
    }

    static void Board(StringBuilder text, AssistGround ground)
    {
        var settings = Stated(ground);
        var things = new StringBuilder(settings is null
            ? $"{Unstated(ground)}."
            : settings.Tracker.Board is { } now ? $"the board of the project is {now}, and the settings call its statuses {Statuses(settings.Tracker.Statuses)}." : "the project works without a board.");
        if (ground.Settings is null)
            things.Append(CultureInfo.InvariantCulture, $" Step 2 writes the settings first: `{Command(2)}`.");
        if (ground.Boards is { Count: > 0 } boards)
        {
            things.Append(" The boards you can reach, each with what it calls its statuses:\n\n");
            bool IsOfSettings(AssistBoard board) => board.Address.Equals(settings?.Tracker.Board, StringComparison.OrdinalIgnoreCase);
            foreach (var board in boards)
            {
                var statuses = board.Statuses switch
                {
                    null => "its statuses cannot be read",
                    [] => "it has no statuses. **A run needs three.**",
                    { Count: < 3 } has => $"{string.Join(", ", has)}. **A run needs three statuses.**",
                    var has when IsOfSettings(board) && settings!.Tracker.Statuses.Missing(has) is { Count: > 0 } missing => $"{string.Join(", ", has)}. **It lacks {Named(missing, "")}, which the settings name.**",
                    var has when !IsOfSettings(board) && BoardStatuses.Default.Missing(has) is { Count: > 0 } => $"{string.Join(", ", has)}. **It is not named as GitHub names a board: the command says which status is which.**",
                    var has => string.Join(", ", has),
                };
                things.Append(CultureInfo.InvariantCulture, $"- {(board.Title.Length > 0 ? $"{board.Title}, " : "")}{board.Address}{(board.Linked ? $", linked to {ground.Repository}" : "")}: {statuses}\n");
            }
        }
        else if (ground.Unread is null && ground.Boards is not null)
        {
            things.Append(CultureInfo.InvariantCulture, $" No board can be read: you can reach none that is open, the token lacks the `project` scope, or GitHub did not answer. `gh auth refresh -s project` gives the scope, as step 1 says: `{Command(1)}`. The step is told without the boards.");
        }
        else
        {
            things.Append(' ').Append(NotRead(ground, "The boards"));
        }

        Part(text, "As things are", things.ToString().TrimEnd('\n'));
        Part(text, "Decide", """
            whether the project has a GitHub Projects board, and which: one of those you can reach, a new one, or none.

            - With a board, a task is taken only from one status, the one to do, and a task that is not on the board is held. A run moves a task to a second status before its session and to a third when it is done. The token needs the `project` scope.
            - A run knows the three by what the settings call them, `tracker.statuses` there, whatever the case of their letters. A status the settings do not name is called as GitHub calls it on a new board: `Todo`, `In Progress` and `Done`. Which status of the board is which of the three is decided for a board that calls them otherwise: two of them must not be one status, and a board with fewer than three statuses gets more on GitHub first.
            - A board that lacks a status the settings name gives a run no task: `toobusy doctor` fails over it.
            - Without a board a task has no status: a run takes the open tasks by their labels alone.
            """);
        Part(text, "Look at", "the boards above, where the open tasks of the project stand now, and what the instructions of the project say of a board.");
        Part(text, "Ask the owner", "always: a board is seen by everyone who works in the project, a new one is made on GitHub, and a board that is not linked to the repository is linked to it. Which status is which is theirs to say for a board that is not named as GitHub names one. Whether the open tasks are put on the board, and into the status to do, is the owner's to decide too: a task that is not there is held.");

        var rest = ground.Settings is null ? " --no-skill" : "";
        Set(text,
        [
            $"toobusy init --yes{rest} --board <URL>",
            $"toobusy init --yes{rest} --board <URL> --todo-status <name> --in-progress-status <name> --done-status <name>",
            $"toobusy init --yes{rest} --new-board \"<title>\" --board-owner <login>",
            $"toobusy init --yes{rest} --no-board",
        ]);
        text.Append("One of them: a board by its address or the address of any page of it, the same with what the board calls its statuses, a new board with its title for the user or one of their organisations, or no board. A status that no option names stays as the settings call it, and is called as GitHub calls it where they do not; the three options go without `--board` too, for the board the project has. Without `--board-owner` a new board is made for the owner of the repository.");
        text.Append(ground.Settings is null
            ? " `--no-skill` stands there only because nothing is set yet: step 6 sets it. The labels are then those a first setup proposes, which step 2 decides.\n\n"
            : " What the command does not name stays as it is.\n\n");
        Part(text, "Check", "`toobusy doctor` passes `board` and `statuses`, or skips both for a project without a board.");
    }

    static void Choices(StringBuilder text, AssistGround ground)
    {
        var things = new StringBuilder($"the milestone to work on: {MilestoneInShort(ground.Milestone)}; the model: {ModelInShort(ground.Model)}; the effort: {ground.Effort ?? "not chosen"}.");
        if (ground.Milestones is { Count: > 0 } open)
        {
            things.Append(CultureInfo.InvariantCulture, $" The open milestones of {ground.Repository}, each with its open tasks:\n\n");
            foreach (var milestone in MilestoneOrder.Sorted(open))
                things.Append(CultureInfo.InvariantCulture, $"- {milestone.Title}: {Spoken.Tasks(milestone.OpenTasks)}{(milestone.Due is { } due ? string.Create(CultureInfo.InvariantCulture, $", due {due:yyyy-MM-dd}") : "")}\n");
            if (ground.Milestone?.Title is { } chosen && MilestoneStanding.Find(open, chosen) is null)
                things.Append(CultureInfo.InvariantCulture, $"\nThe milestone “{chosen}” that is chosen is not open any more: it is chosen again.\n");
        }
        else if (ground.Milestones is not null)
        {
            things.Append(CultureInfo.InvariantCulture, $" {ground.Repository} has no open milestones.");
        }
        else
        {
            things.Append(' ').Append(NotRead(ground, "The open milestones"));
        }

        Part(text, "As things are", things.ToString().TrimEnd('\n'));
        Part(text, "Decide", $"""
            the three choices of the owner. They are kept on this machine and are not committed, so everyone who clones the project chooses their own.

            - **The milestone to work on.** A run takes the open tasks of this milestone, or, without a milestone, all the open tasks.
            - **The model** the tasks are done with: {string.Join(", ", ClaudeCodeOptions.Models.Select(model => $"`{model}`"))}, any other name Claude Code takes, or the assistant's own default.
            - **The effort**: {string.Join(", ", ClaudeCodeOptions.Efforts.Select(effort => $"`{effort}`"))}.
            """);
        Part(text, "Ask the owner", "all three: they are theirs, and none of them is yours to choose. Then show them what `toobusy queue` prints.");
        Set(text,
        [
            "toobusy milestone \"<title>\"",
            "toobusy milestone --none",
            "toobusy model <name>",
            "toobusy model --default",
            "toobusy effort <level>",
        ]);
        text.Append("A milestone by its title or `--none`, a model by its name or `--default`, and the effort by its level.\n\n");
        Part(text, "Check", $"`toobusy queue` shows which tasks a run would take, in which order, which open later and which are held, each with the reason, and changes nothing; `toobusy queue --json` prints the same for you. Show it to the owner. When it is not what they expect, the walk goes back: to the labels, `{Command(2)}`, or to the board, `{Command(3)}`.");
    }

    static void Instructions(StringBuilder text, AssistGround ground)
    {
        var owner = Stated(ground)?.Queue.Labels.Owner;
        Part(text, "As things are", "toobusy does not read the instructions of the project: they are yours to read.");
        Part(text, "Decide", """
            what the instructions of the project say for a project that is worked on by toobusy. Read `toobusy about` first: it tells what a session meets, and under “What holds over the instructions of a project” what a session does not follow of them. Then compare the instructions with it, and give them what such a project needs:

            - that an assistant that works with the owner looks at `toobusy status` before it commits: a run works in the working copy itself, and a commit made beside it lands in the middle of its task. `toobusy status` exits with 3 while a run goes on. The rule is not for a session that toobusy started;
            - where they tell an assistant to move its task, to assign it, to label it, to comment on it or to close it: that in a session of a run the task is kept by toobusy, and the session does none of that;
            - where they describe another way of running the tasks of the project without its owner, a script of the project, the sessions it started, the line it read at the end of a reply, the labels and the statuses it set: they are changed to describe toobusy, or taken out;
            - how a session brings its branch up to date when the branch has moved on the remote by the time it pushes: by a rebase or by a merge. Its first message tells it to do so as the instructions say, and to push again;
            - what a session without the owner does in another way than one with them. The skill of step 6 is the place for that, when the project has one.
            """);
        Part(text, "Look at", "`CLAUDE.md`, `AGENTS.md`, the skills and the commands in `.claude/`, `CONTRIBUTING.md` and whatever else tells an assistant how the work of this project is done. Where the project ran its tasks without its owner before, the old way is found in a script of the project that starts sessions of the assistant, a section of its process on it, a folder of its state, and tasks of the tracker that are about it: `gh issue list --search \"<the name of the script>\"`.");
        Part(text, "Ask the owner", $"what a session may do alone in this project, and what it must not. A kind of task that it must not do gets a blocking label, back in step 2: `{Command(2)}`.{(owner is null ? "" : $" A single task that waits for the owner gets the `{owner}` label.")} Where the project has an old way of running its tasks without the owner: whether the script, its state and its instructions are taken out of the project, and what becomes of the tasks about it. Nothing is deleted without the owner. Show the owner the changes of the instructions before they are kept.");
        Part(text, "Set it", "no command of toobusy sets this step: the instructions are edited as any file of the project.");
        Part(text, "Check", "read `toobusy about` once more against the instructions as they are now: nothing in them tells a session of a run to do what toobusy does.");
    }

    static void Skill(StringBuilder text, AssistGround ground)
    {
        var settings = Stated(ground);

        // A skill that is written may be one that sessions of another way of running the tasks started with.
        var written = settings?.Skill is not null && ground.Skill is { Found: true, Place: var place } ? place : null;
        Part(text, "As things are", (settings, ground.Skill) switch
        {
            (null, _) => $"{Unstated(ground)}.",
            ({ Skill: null }, _) => "the project names no skill: a session starts with the message of toobusy alone.",
            ({ Skill: { } name }, { Found: true } look) => $"the project names the skill `{name}`, and it is written: {look.Place}.",
            ({ Skill: { } name }, { Found: false } look) => $"the project names the skill `{name}`, and it is not written yet: {look.Place} is where it goes. `toobusy doctor` fails and no run starts until it is there.",
            ({ Skill: { } name }, _) => $"the project names the skill `{name}`, a skill of a plugin, which toobusy does not look for.",
        });
        Part(text, "Decide", $"""
            whether the project needs a skill for a session to start with, and what it says.

            - It needs one when work without the owner differs from work with them: checks a session runs by itself, what it does where the owner would have looked, the language of the report, which other tasks it makes. A project whose instructions say all of that already needs none.
            - `toobusy about` tells, under “A session starts with the skill of the project”, what the first message of a session says already, which the skill does not say again or against, and what is the skill's to say.
            - The skill is a file of the project, and toobusy does not make it: `.claude/skills/<name>/SKILL.md`. To be loaded by its command, `/<name>`, the file begins with a front matter between two lines of `---` that has `name: <name>`, the name of its folder, and `description:`, a line on what the skill is for; the instructions follow it. `{ProposedSkill}` is the name a first setup proposes.
            """ + (written is null ? "" : $"""


            The skill is written already, and it may be older than toobusy: one that the sessions of a script of the project or of another tool started with. Read {written} against `toobusy about`, under “A session starts with the skill of the project”, and change it so:

            - what the first message says already is taken out of it: that nobody answers, that the task is read in the tracker and kept by toobusy, the working copy and the plan of the work, what becomes of what cannot be done without the owner, and how the last reply ends;
            - what says against the first message is taken out or changed: a line a session ends its reply with for another tool, commands that move, assign, label, comment on or close the task of the session, asking the owner, and a worktree;
            - what is the project's stays: how the work is done and checked without the owner, what is done before the work and after it but for what the run does itself with the branch and the commands of step 7, the language of the report, and the other tasks a session makes;
            - where it names another tool, its script, its files or its commands, it names toobusy and its commands instead, as `toobusy queue` and `toobusy status`, or leaves them out.
            """));
        Part(text, "Look at", "the skills the project has already in `.claude/skills/` and its commands in `.claude/commands/`, and the instructions of step 5.");
        Part(text, "Ask the owner", written is null
            ? "whether the project gets a skill, and show them the skill before it is kept: it says how their tasks are done without them."
            : "whether the project keeps its skill, and show them the skill as it will be before it is kept, with what is taken out of it and why: it says how their tasks are done without them.");

        var rest = ground.Settings is null ? " --no-board" : "";
        Set(text, [$"toobusy init --yes{rest} --skill <name>", $"toobusy init --yes{rest} --no-skill"]);
        text.Append("Write the skill first, then name it; `--no-skill` is for a project that needs none.");
        text.Append(ground.Settings is null
            ? " `--no-board` stands there only because nothing is set yet: step 3 sets it. The labels are then those a first setup proposes, which step 2 decides.\n\n"
            : " What the command does not name stays as it is.\n\n");
        Part(text, "Check", "`toobusy doctor` passes `skill` and says where it found the skill, or skips it for a project that names none. `toobusy about` shows the first message of a session with the command of the skill at its head."
            + (written is null ? "" : " The skill says nothing that `toobusy about` lists under what the first message says already, and nothing against it."));
    }

    static void Commands(StringBuilder text, AssistGround ground)
    {
        var settings = Stated(ground);
        Part(text, "As things are", settings is null
            ? $"{Unstated(ground)}."
            : $"the branch of the tasks: {Quoted(settings.Branch)}; before a task: {Quoted(settings.Commands.Before)}; after a task: {Quoted(settings.Commands.After)}.");
        Part(text, "Decide", $"""
            what the run itself does around every task, whatever its session does or forgets.

            **The branch.** The branch the tasks are done on. With one named, a run checks before every task that the working copy is on it, and stops on another one: it does not change what is checked out. And before a task it brings the branch up to date with its upstream: it fetches what the remote has and moves the branch forward to it, so that the task starts from there. A branch with commits its upstream lacks, one that has parted from it, and a remote that refuses, or does not answer three tries in a row, stop the run. Without a branch, a run does the tasks on whatever is checked out, as it stands. A project that does its work on one branch names it.

            **The commands.** Whether the run runs a command of the project before every task and one after it: what is to be so around every task. Before a task, such as that the ports of the development server are free; after it, such as that the services a session brought up are stopped. The branch and its sync with the remote are not for a command: the branch does them. Most projects need no command.

            The rules of a command:

            - it is one line for the shell, `sh -c` and on Windows `cmd /c`, run at the root of the working copy, after the branch is brought up to date;
            - it exits with 0 when all is well: a `before` that fails stops the run before the task is touched, and an `after` that fails stops the run after it;
            - it is given up after {Spoken.Time(RunPolicy.Default.CommandTime)};
            - it learns the task from its environment: `TOOBUSY_TASK` is the number, `TOOBUSY_TASK_TITLE` the title and `TOOBUSY_TASK_URL` the address;
            - it gets no input and nobody answers it;
            - it leaves the working tree clean: a run starts no task over files that differ from what is committed.

            `toobusy about` tells the rest under “The branch of the tasks” and “The commands of the project around a task”. What the run does, the instructions and the skill do not tell a session to do as well.
            """);
        Part(text, "Look at", "the branch the instructions of the project do the work on, and the default branch of the repository, `gh repo view --json defaultBranchRef`; what is checked out and what it follows, `git status --short --branch`; the scripts of the project, its `Makefile` or `package.json`, and what its instructions tell an assistant to do before it starts a task and after it has finished one.");
        Part(text, "Ask the owner", "which branch the tasks are done on, and before a command is set: it runs on their machine around every task, with nobody watching.");

        var rest = ground.Settings is null ? " --no-board --no-skill" : "";
        Set(text,
        [
            $"toobusy init --yes{rest} --branch <name> --before \"<command>\" --after \"<command>\"",
            $"toobusy init --yes{rest} --no-branch --no-before --no-after",
        ]);
        text.Append("Each of the six options stands alone too: `--branch` names the branch and `--no-branch` takes it away, `--before` and `--after` set a command, `--no-before` and `--no-after` take one away, and what no option names stays as it is.");
        text.Append(ground.Settings is null
            ? " `--no-board` and `--no-skill` stand there only because nothing is set yet: steps 3 and 6 set them. The labels are then those a first setup proposes, which step 2 decides.\n\n"
            : "\n\n");
        Part(text, "Check", "`git status --short --branch` shows the branch, and after it the upstream it follows, as `## develop...origin/develop`: a branch without one is not brought up to date. Run each command by hand once, at the root of the working copy and with a task in its environment, as `TOOBUSY_TASK=1 sh -c \"<command>\"; echo $?`: it prints 0, and `git status --short` prints nothing after it.");
    }

    static void Permissions(StringBuilder text, AssistGround ground)
    {
        var settings = Stated(ground);
        Part(text, "As things are", settings is null
            ? $"the permission mode is {Unstated(ground)}. toobusy does not read the settings of Claude Code: they are yours to read."
            : $"the permission mode is {ModeInShort(settings)}. toobusy does not read the settings of Claude Code: they are yours to read.");
        Part(text, "Decide", $"""
            what a session is allowed to do, and in which permission mode of Claude Code it works. A session of a run is a background session of Claude Code, and nobody answers its prompts: what it is refused is not done, and the task is left to the owner or done without it.

            - **The mode**, `{SettingsKeys.PermissionMode}` of the settings. `auto` is the mode a session is started in when the settings name none: a model of Claude Code looks at every action before it is done, and refuses what goes beyond the task or looks dangerous. In `dontAsk` whatever the settings of Claude Code do not allow is refused, so a session does only what is allowed there: for a project that wants to say exactly what a session may do. A mode that asks or waits for a plan to be approved, as `manual`, `acceptEdits` and `plan`, is not taken: a session in it would wait for an answer that never comes. Nor is `bypassPermissions`, which checks nothing.
            - What every task needs must be allowed in the settings of Claude Code in the project: the build, the tests and the other checks of the project, `git` with its commit and its push, and `gh`. In `dontAsk` nothing else is done.
            - What waits for a person does not work: a hook that asks, a server that needs a login in a browser, a command that prompts.
            - What a session is allowed and refused in particular stays where Claude Code keeps it, in the settings of the project in `.claude/`: toobusy does not say it again.
            """);
        Part(text, "Look at", "`.claude/settings.json` and `.claude/settings.local.json` of the project, with their permissions and their hooks, `.mcp.json`, and the commands the instructions of the project tell an assistant to run.");
        Part(text, "Ask the owner", "which mode a session works in, and before anything more is allowed: what a session may do on their machine with nobody watching is theirs to decide.");

        var rest = ground.Settings is null ? " --no-board --no-skill" : "";
        Set(text, [$"toobusy init --yes{rest} --permission-mode <mode>"]);
        text.Append("The mode is `auto` or `dontAsk`; what the command does not name stays as it is. The settings of Claude Code are edited as any file of the project: no command of toobusy sets them.");
        text.Append(ground.Settings is null
            ? " `--no-board` and `--no-skill` stand there only because nothing is set yet: steps 3 and 6 set them. The labels are then those a first setup proposes, which step 2 decides.\n\n"
            : "\n\n");
        Part(text, "Check", "`toobusy about` names the mode under “A run”. Every command the instructions and the skill tell a session to run is allowed in the settings of Claude Code, and nothing there waits for a person.");
    }

    static void LastCheck(StringBuilder text, AssistGround ground)
    {
        var skill = Stated(ground)?.Skill is { } name && ground.Skill is { Found: not null } look ? $"the skill `{name}`, {look.Place}" : "the skill, when the project has one";
        Part(text, "As things are", "`toobusy doctor` tells: it checks everything a run needs, and changes nothing.");
        Part(text, "Look at", $"what `toobusy doctor` prints. A check that fails goes back to its step: the tools, the logins and the repository to `{Command(1)}`, the settings and the labels to `{Command(2)}`, the board and its statuses to `{Command(3)}`, the skill to `{Command(6)}`.");
        Part(text, "Ask the owner", "nothing but the commit of the setup, where the instructions of the project say that a commit is asked for. When the step is done, tell them in a few lines what was set: which tasks a run takes, the board, their own choices, the skill, the branch, the commands around a task, the permission mode and what was changed in the instructions; and that the setup is complete. Propose nothing after it: no demo, no first run and no next thing to try. A run is theirs to start.");
        Part(text, "Set it", $"""
            no command of toobusy: what is left is the commit of the setup, made as the instructions of the project say a commit is made. What is to be committed:

            - `{ground.Path}`;
            - {skill};
            - the instructions that were changed in step 5, and the settings of Claude Code that were changed in step 8;
            - what was taken out of the project in steps 5 and 6, the script of the old way of running the tasks, its state and its instructions, where the owner agreed to it.

            The choices of the owner, the milestone, the model and the effort, are not in the project and are not committed.
            """);
        Part(text, "Check", "`toobusy doctor` passes every check, or skips the board and the statuses of a project without a board and the skill of a project that names none.");
    }

    // What stands at the head of the introduction and of every step while the settings do not validate.
    static string Broken(AssistGround ground)
    {
        if (ground.Settings is not { Settings: null } broken)
            return "";

        var text = new StringBuilder("**The settings of this project do not validate:**\n\n");
        foreach (var error in broken.Errors)
            text.Append(CultureInfo.InvariantCulture, $"- {error.Describe(ground.Path)}\n");
        text.Append(CultureInfo.InvariantCulture, $"\nPut `{ground.Path}` right by hand first: no command of the steps writes over settings it cannot read, and the steps cannot say how the project stands until then. `toobusy doctor` passes `settings` when the file is right.\n\n");
        return text.ToString();
    }

    // Why something of the tracker is not there for a step, and the step that puts it right.
    static string NotRead(AssistGround ground, string what) => ground.Unread is { } reason
        ? $"{what} cannot be read: {reason} Step 1 puts it right: `{Command(1)}`. The step is told without them."
        : $"{what} cannot be read: GitHub did not answer. Ask for the step again, or see step 1: `{Command(1)}`. The step is told without them.";

    // The settings when the file has them and they validate.
    static ProjectSettings? Stated(AssistGround ground) => ground.Settings?.Settings;

    static string Statuses(BoardStatuses statuses) => string.Join(", ", statuses.Names.Select(name => $"`{name}`"));

    // What is said of a setting where there are no settings to read.
    static string Unstated(AssistGround ground) => ground.Settings is null ? "not set: there are no settings yet" : "not read: the settings do not validate";

    static string LabelsInShort(AssistGround ground) => Stated(ground)?.Queue.Labels is { } labels
        ? $"take: {Named(labels.Take, "any task")}; blocking: {Named(labels.Blocking, "none")}; owner: `{labels.Owner}`; interrupted: `{labels.Interrupted}`"
        : Unstated(ground);

    static string SkillInShort(AssistGround ground) => (Stated(ground), ground.Skill) switch
    {
        (null, _) => Unstated(ground),
        ({ Skill: null }, _) => "no skill",
        ({ Skill: { } name }, { Found: true }) => $"`{name}`, written",
        ({ Skill: { } name }, { Found: false }) => $"`{name}`, not written yet",
        ({ Skill: { } name }, _) => $"`{name}`, a skill of a plugin",
    };

    static string ModeInShort(ProjectSettings settings) => settings.Assistant.PermissionMode is { } named
        ? $"`{named}`"
        : $"`{settings.Assistant.SessionMode}`, as the settings name none";

    static string MilestoneInShort(MilestoneChoice? choice) => choice switch
    {
        null => "not chosen",
        { Title: null } => "no milestone",
        _ => choice.Title,
    };

    static string ModelInShort(ModelChoice? choice) => choice switch
    {
        null => "not chosen",
        { Name: null } => "the assistant's own",
        _ => choice.Name,
    };

    // A part of a step under its name; what a part says begins on the line of its name.
    static void Part(StringBuilder text, string name, string says) =>
        text.Append(CultureInfo.InvariantCulture, $"{name}: {says.ReplaceLineEndings("\n").TrimEnd('\n')}\n\n");

    // The commands of a step, each on a line of its own, as a block of code.
    static void Set(StringBuilder text, IReadOnlyList<string> commands)
    {
        text.Append("Set it:\n\n");
        foreach (var command in commands)
            text.Append("    ").Append(command).Append('\n');
        text.Append('\n');
    }

    static string Named(IReadOnlyList<string> names, string none) => names.Count == 0 ? none : string.Join(", ", names.Select(name => $"`{name}`"));

    // Labels as one value of an option; a name with a space in it makes the value one word for the shell.
    static string Joined(IReadOnlyList<string> labels) => Word(string.Join(',', labels));

    static string Word(string value) => value.Any(char.IsWhiteSpace) ? $"\"{value}\"" : value;

    static string Quoted(string? command) => command is null ? "none" : $"`{command}`";

    static string Capital(string text) => text.Length == 0 || text[0] == '`' ? text : char.ToUpperInvariant(text[0]) + text[1..];
}
