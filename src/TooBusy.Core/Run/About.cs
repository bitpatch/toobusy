using System.Globalization;
using System.Text;
using TooBusy.Core.Assistant;
using TooBusy.Core.Queue;
using TooBusy.Core.Settings;

namespace TooBusy.Core.Run;

// How a run works, told to the owner of a project and to the assistant that helps them fit the instructions of the
// project to it. It describes and asks for nothing. The messages are those of Briefing, word for word, and the
// times and the counts those of the policy, so that what is told here is what a session meets.
public static class About
{
    // The task the messages are shown for.
    static readonly QueueTask Sample = new(12, "<the title of the task>", "<the address of the task>", [], BoardStatus.Todo, "Todo", [], []);

    // `rules` are those of the project the text is asked in; null where no settings are read, and then the labels
    // are spoken of without their names. `skill` is the skill the settings name for a session to start with; null
    // where they name none, and where none are read. `commands` are the commands of the settings around a task,
    // `branch` the branch they name for the tasks to be done on, and `permissionMode` the permission mode they name
    // for a session to be started in.
    public static string Text(QueueRules? rules, RunPolicy policy, string? skill = null, TaskCommands? commands = null, string? branch = null, string? permissionMode = null)
    {
        var manual = rules is null ? "the manual label" : $"the `{rules.Manual}` label";

        // What the messages name of a project: its manual label where the settings are read, and the milestone
        // of the user and the address of the board, which are not read here.
        var label = rules?.Manual ?? "<the manual label>";
        var ground = new NewTasks(label, [], "<the milestone the user works on>", rules is null or { Board: true } ? "<the address of the board>" : null) { Statuses = rules?.Statuses ?? BoardStatuses.Default };
        var interrupted = rules is null ? "the label of interrupted tasks" : $"the `{rules.Interrupted}` label";
        var again = Briefing.Task(Sample, ground, interrupted: true).Split('\n').First(line => line.StartsWith("- This task was interrupted before.", StringComparison.Ordinal));
        var lost = Briefing.Task(Sample, ground, interrupted: false, lost: true).Split('\n').First(line => line.StartsWith("- A session worked on this task before", StringComparison.Ordinal));

        // What is said of the skill of this project, and how its first message begins.
        var named = rules is null ? "No settings are read here, so it is not said whether this project names one: the first message is shown below as it is without a skill."
            : skill is null ? "This project names none: its first message begins with the words of toobusy, as shown below. `toobusy init` names one."
            : $"This project names the skill `{skill}`, so its first message begins with `{Briefing.Command(skill, Sample)}`, as shown below.";
        var head = skill is null ? "It begins with the same line in every session, but for the number, so a session of a run can be told by it from any other."
            : "After the line of the skill it begins with the same words in every session, but for the number, so a session of a run can be told by them from any other.";

        // What is said of the commands of this project around a task.
        var around = (rules, commands) switch
        {
            (null, _) => "No settings are read here, so it is not said whether this project has them.",
            (_, { Before: { } first, After: { } last }) => $"This project runs `{first}` before a task and `{last}` after it.",
            (_, { Before: { } first }) => $"This project runs `{first}` before a task, and nothing after it.",
            (_, { After: { } last }) => $"This project runs `{last}` after a task, and nothing before it.",
            _ => "This project has none: `toobusy init --before <command>` and `toobusy init --after <command>` set them.",
        };

        // What is said of the branch of this project.
        var tasksBranch = (rules, branch) switch
        {
            (null, _) => "No settings are read here, so it is not said whether this project names one.",
            (_, { } name) => $"This project names `{name}`.",
            _ => "This project names none: its tasks are done on whatever is checked out, as it stands. `toobusy init --branch <name>` names one.",
        };

        // What is said of the permission mode a session of this project is started in.
        var mode = (rules, permissionMode) switch
        {
            (null, _) => $"A session is started in the permission mode `{ClaudeCodeOptions.DefaultPermissionMode}` of Claude Code, where {ClaudeCodeOptions.Checks(ClaudeCodeOptions.DefaultPermissionMode)}, unless the settings of a project name another as `{SettingsKeys.PermissionMode}`. No settings are read here, so it is not said whether this project names one.",
            (_, { } chosen) => $"This project names the permission mode `{chosen}` of Claude Code in its settings, `{SettingsKeys.PermissionMode}`: a session is started in it, where {ClaudeCodeOptions.Checks(chosen)}.",
            _ => $"This project names no permission mode, so a session is started in `{ClaudeCodeOptions.DefaultPermissionMode}` of Claude Code, where {ClaudeCodeOptions.Checks(ClaudeCodeOptions.DefaultPermissionMode)}. `toobusy init --permission-mode <mode>` names one: {string.Join(" or ", ClaudeCodeOptions.PermissionModes.Select(known => $"`{known}`"))}.",
        };

        // What the board calls the statuses a run knows: as the settings name them, and as GitHub does where none
        // are read.
        var statuses = rules?.Statuses ?? BoardStatuses.Default;
        var (todo, progress, done) = ($"`{statuses.Todo}`", $"`{statuses.InProgress}`", $"`{statuses.Done}`");

        // How often a call that GitHub does not answer is tried, and the pauses between the tries.
        var tries = $"{policy.RetryPauses.Count + 1} times in all, with pauses of {string.Join(" and ", policy.RetryPauses.Select(Spoken.Time))} between them";

        var text = new StringBuilder();
        text.Append("""
            # How toobusy works

            toobusy takes the tasks of a project's tracker one after another and has an AI coding assistant do each of them, in a session of its own and with nobody at the keyboard. This is what such a session meets: what it is told, what is done around it, and what is made of its answer. It is written for the owner of a project and for the assistant that helps them, to fit the instructions of the project to a run and to write the skill a session starts with. Nothing here has to be done for a run to work: a session is told all it needs in its first message.

            An assistant that sets a project up for its owner starts with `toobusy init --assist`: it walks through the setup one step at a time, and points here for how a run works.

            ## A run

            - The tasks are the open tasks of the milestone the user works on, or all the open ones when the user works without a milestone.

            """);
        if (rules is null)
        {
            text.Append("""
                - The settings of a project say which tasks a run takes: the labels that keep a task out, the labels a task must have one of, and whether the project has a board, where a task is taken only from the status `Todo`. No settings are read here, so its labels are not named below, and the statuses of a board are named as GitHub names them on a new one, where the settings of a project may call them otherwise: `toobusy init` sets a project up.

                """);
        }
        else
        {
            if (rules.Blocking.Count > 0)
                text.Append(CultureInfo.InvariantCulture, $"- A task with {Labels(rules.Blocking, "the label", "one of the labels")} is not taken.\n");
            if (rules.Take.Count > 0)
                text.Append(CultureInfo.InvariantCulture, $"- Only a task with {Labels(rules.Take, "the label", "one of the labels")} is taken.\n");
            text.Append(rules.Board
                ? $"- The project has a board: a task is taken only from the status {todo}. The statuses a run knows are named below as the settings of the project call them, {todo}, {progress} and {done}.\n"
                : "- The project has no board: a task has no status, and where a status is named below, nothing is moved.\n");
        }

        text.Append(CultureInfo.InvariantCulture, $"""
            - A task that has {manual} waits for the owner and is not taken.
            - A task waits for the open tasks that block it and for its own open sub-tasks.
            - The tasks that were interrupted are taken first, then the others by their numbers. A task that an earlier run was stopped over comes before any of them.
            - `toobusy run` takes tasks until none is left. `toobusy run --max 3` takes three at most: a task counts when it is done, done in part or left to the owner. `toobusy run --task 12` does task #12 and no other, and only when a run would take it now: a task that is held, waits for another or is not an open task of the milestone is not started, nor is one that is asked for while an earlier run has left another task with a session. A session is told nothing of either: it does its task as in any run.
            - `toobusy run --skip-label design` leaves the tasks with the `design` label out of that run, as if it were a label that keeps a task out, and with them the tasks that wait for them; the option is given again for another label, or takes several names separated by commas, `Skip labels` in the menu of `toobusy` chooses the same for the runs it opens, and `toobusy queue --skip-label design` shows the queue as such a run reads it. A task that an earlier run left with a session is gone on with whatever its labels. A session is told nothing of it.
            - `toobusy queue` shows which tasks a run takes as things are, in the order it takes them, those that open later and those that are held, each with the reason, and changes nothing. `toobusy queue --json` prints the same for an assistant.
            - `toobusy status` says whether a run is going on in the working copy, with the task it works on and the command that opens its session, and changes nothing. A run works in the working copy itself, so a commit made beside it lands in the middle of its task: the instructions of a project can tell an assistant that works with the owner to look before it commits. The session of a run commits while its run goes on, so such a rule is not for a session that toobusy started. It exits with 0 when no run is going on and with 3 while one is, and `toobusy status --json` prints the same for an assistant. A second run does not start in a working copy while one goes on.
            - One task is done at a time, in the working copy of the project itself. The working copy must be clean before a task, and it must be clean after it.
            - Each task is done by a session of the assistant of its own, which goes on by itself in the background. Nobody answers its questions or its permission prompts. {mode} What a session is allowed and refused in particular is said where Claude Code keeps it, in the settings of the project in `.claude/`, and toobusy does not say it again.
            - A task is gone on with by the same session whatever stopped the run over it, as told under “When a run goes on with a session”.

            ## The task of a session is kept by toobusy

            A session changes nothing of its own task: not its status, not its assignee and not its labels, and it does not comment on the task or close it. Before a session starts, toobusy moves the task to {progress}; once the session has started, it assigns the task to the user who runs it, with a board and without one, and takes {interrupted} off a task that had it. A tracker that refuses the assignment does not stop the run. After the session it does what the last line of the reply calls for, as told under “How a task ends”. A call of the tracker that GitHub does not answer, as when the network is away for a moment, is tried {tries}, before the run stops over it; a comment is written once, as one whose answer was lost may be written already.

            ## A session makes new tasks itself

            A session may make new tasks in the tracker, and toobusy makes none. toobusy asks for one of them: what cannot be done without the owner becomes a new task with {manual}, so that no run takes it before the owner answers. The first message says what such a task needs for a run: the label, the milestone, the board and the status it stands in there, and that the session decides whether it blocks what its own task blocks. The session names the new task in its report.

            Which other tasks a session makes and when, such as the findings of an audit or a proposal, and their labels, their wording and their relations, is the project's to say in its instructions. toobusy looks at none of them.

            ## A session starts with the skill of the project

            A project may name a skill in its settings, as `session.skill`, and then a session starts with it: the first message begins with the command of the skill, the number and the title of the task, and goes on as it does in any project. The assistant loads the skill by that line itself, so nothing is left to the session, and the rest of the message reaches it with the skill. {named}

            The skill is loaded once, at the start. The messages for a session that goes on do not name it, and a new session for a task whose conversation is gone starts with it again. Claude Code reads a skill of a project from `.claude/skills/<name>/SKILL.md`. `toobusy doctor` checks that the skill is there, and no run starts while it is not: a session that starts with a skill the assistant does not have does nothing at all.

            The skill says only what belongs to the project; how a run works is said by the first message. What that message says already, the skill does not say again, and does not say against:

            - that nobody answers, and that the session decides and goes on where the instructions of the project say to ask;
            - that the task is read in the tracker, and that it is kept by toobusy: its status, its assignee, its labels, its comments and closing it;
            - that the work is done in this working copy, which is clean at the end, and that a plan of it is kept in the task list;
            - what becomes of what cannot be done without the owner, and what the task of it needs;
            - how the last reply ends: the lines for toobusy.

            What is the skill's to say:

            - how the work of this project is done and checked without its owner: which checks a session runs by itself, and what it does where the owner would have looked;
            - what is done before the work and after it, such as what is brought up to date first and what is stopped or cleaned up at the end, but for what the run does itself, as told under “The branch of the tasks” and “The commands of the project around a task”;
            - how the branch is brought up to date when it has moved on the remote by the time a session pushes: by a rebase or by a merge, where the instructions of the project do not say it already;
            - the language of the report;
            - which tasks a session makes besides the one of what is left, with their labels, their wording and their relations.

            ## The branch of the tasks

            A project may name the branch its tasks are done on, `workspace.branch` of its settings. {tasksBranch}

            - Before every task, where the working tree is checked, the working copy must be on that branch. On another one the run stops and says so: what is checked out is the owner's, and the run does not change it.
            - Before a task is taken, and before the command before it, the branch is brought up to date with its upstream: what the remote has is fetched, and the branch is moved forward to it, so that the task starts from what the remote has. A branch that has commits its upstream lacks, one that has parted from it, and a remote that refuses or does not answer stop the run, and the task is not touched; a remote that does not answer is asked again first, as the tracker is. A branch without an upstream has nothing to be brought up to date with.
            - Nobody answers a prompt of a run: git's prompts are off in every command toobusy starts, and the fetch gets into the remote with what the machine keeps, a credential helper over HTTPS, an ssh agent or a key without a passphrase over ssh. A remote that asks for a login refuses.
            - A task that a run goes on with, whose session may have left its changes in the working tree, is checked for the branch as any other, and nothing is moved under it.
            - The branch may move while a session works, as when someone pushes to it meanwhile. Bringing it up to date before the push is the session's own: its first message tells it to do so as the instructions of the project say, by a rebase or by a merge, and to push again. toobusy checks the result only: a session that leaves commits its upstream lacks stops the run.
            - A demo checks nothing of it.

            What the run does here, the instructions and the skill of a project do not tell a session to do as well. They tell it how the branch is brought up to date before its push.

            ## The commands of the project around a task

            A project may have the run itself run a command before a task and one after it, `task.before` and `task.after` of its settings: what is to be so around every task, such as that the ports of its development server are free before one and that the server is stopped after it. It does not depend on a session remembering it then. {around}

            - A command is a line for the shell, `sh -c` and on Windows `cmd /c`. It runs at the root of the working copy, and the run knows of it only whether it exited with 0. It learns the task from its environment: `TOOBUSY_TASK` is the number of the task, `TOOBUSY_TASK_TITLE` its title and `TOOBUSY_TASK_URL` its address.
            - `before` runs when the working tree and the queue are read, and the branch is brought up to date where the project names one, before the task is moved to {progress} and before its session is started or told to go on. When it fails, the run stops and says so with what the command printed, and the task is not touched. A session that still works when a run finds it is not disturbed by the command.
            - `after` runs when the run has done with the task, however it went: done, done in part, left to the owner, interrupted, failed, paused by a usage limit, when the run was killed over it, and when anything else stopped the run over it. The working copy is checked and the tracker is told before it. When it fails, the task stays as it ended, and the run stops.
            - A command is given up after {Spoken.Time(policy.CommandTime)}, and counts as failed then. After a kill of the run, `after` has {Spoken.Time(policy.KilledCommandTime)}.
            - Neither runs around the wait for a usage limit inside a task, and a demo runs none.

            What these commands do, the instructions and the skill of a project do not tell a session to do as well: the run does it, whatever the session does.

            ## What a session is told

            ### The first message

            Every session starts with this message, here for a task #{Sample.Number}. {head}

            ```text
            {Briefing.Task(Sample, ground, interrupted: false, skill: skill)}
            ```

            The task is named and not passed on: the session reads its description and its comments in the tracker. The milestone is the one the user works on; for a user who works without a milestone it is the one of the task, or none. The board is named only in a project that has one. A task with a label that a task needs to be taken is told to put that label on the new task too, so that a run takes it once the owner has answered. A task that was interrupted before, by `/abort` or by a usage limit in another working copy, has one rule more, after the others:

            ```text
            {again}
            ```

            ### A session that waits for the owner

            A session that stops with a question, or ends its turn without the line for toobusy, is left for {Spoken.Time(policy.OwnerWait)}, in which the owner may answer it. Then its turn is cut, and it is told:

            ```text
            {Briefing.Alone(Sample.Number, label)}
            ```

            A task is told so {policy.Messages} times at most, and it is left to the owner sooner when {policy.IdleMessages} such messages in a row brought no step of its own.

            A session whose reply ends with the line for toobusy has reported, and is not left for the owner, even where Claude Code marks it as waiting for an answer, as it may mark a report that leaves something to the owner. A reply ends with the line when the last of its lines that is not empty is the line; a reply with a question after the line does not, and waits as a question does. Such a session is told this at once, with no countdown, and it counts as one of those messages:

            ```text
            {Briefing.NoAnswer(Sample.Number, label)}
            ```

            Once a session was told either message, a reply that ends with the line for toobusy ends the task, whatever Claude Code marks the session as, as told under “How a task ends”.

            ### After a usage limit

            No limit holds a task back: a task is started however much of a limit is used. A session that runs into a usage limit is stopped with its uncommitted changes left in the working copy. A run waits for a limit that resets soon. For one that does not, it leaves the task to the next run: the task gets {interrupted} and a comment that starts with `**Stopped by a usage limit**`, and goes back to {todo}. When the limit has reset, in the same run or in the next one, the same session is told:

            ```text
            {Briefing.AfterLimit(Sample.Number)}
            ```

            ### When a run goes on with a session

            A run writes down the task and its session on the machine as soon as the session is started, and forgets them when the task comes to an outcome. The next run looks at what is written before it reads the queue, and takes that task first, whatever its status. A task that got {manual} or a label that keeps a task out since is not taken. One that was closed since is settled when its session ended with a line for toobusy, as told under “How a task ends”, and is not taken otherwise.

            - A session that still works, because only toobusy was stopped, is watched again and told nothing.
            - A session that ended with a line for toobusy while nobody watched is settled as any other, as told under “How a task ends”.
            - A session that was stopped, as a kill of the run stops it, or a tracker that refuses while the task is being taken, goes on over the changes it left in the working copy, and is told:

            ```text
            {Briefing.AfterStop(Sample.Number)}
            ```

            - A session that wrapped its task up when the owner stopped the run goes on too, and is told:

            ```text
            {Briefing.AfterAbort(Sample.Number)}
            ```

            - Where the conversation is gone, a new session is started with the first message. After a wrap-up it has the rule of a task that was interrupted, above; otherwise it has this one:

            ```text
            {lost}
            ```

            A session that failed, or whose task did not pass the checks, is not gone on with: its task gets {manual} and waits for the owner, as told under “How a task ends”.

            ### When the owner stops the run at once

            The owner can stop a run after the task that is being done, which its session does not notice, or at once. Then the session is told:

            ```text
            {Briefing.WrapUp(Sample.Number)}
            ```

            A session that works reads it after its next step, beside what that step gave, and its turn is not cut: a subagent may read it too, which is why it says what a subagent does. A session that has not wrapped up in {Spoken.Time(policy.AbortGrace)} has its turn cut and is told again, and so is one that waits for the owner, at once: one whose turn ended without the line among them, however many messages it was told before. It is told so once: a turn that ends without the line after that ends the task as one that did not say how it went. A session whose reply ends with the line for toobusy ends the task then, whatever Claude Code marks it as, and is told nothing more.

            ## How a task ends

            The last reply of a session is the report of the task, and its last line says how the task went. The line is read whatever its case and whatever marks of emphasis stand around it, and the last such line of the reply counts, only when the reply ends with it: a line with a question after it says nothing of how the task went.

            | Last line | toobusy checks | toobusy does |
            |---|---|---|
            | `{Outcome.Mark} done` | the working copy is clean and every commit is pushed | puts the report into the task as a comment, closes the task and moves it to {done} |
            | `{Outcome.Mark} partial` | the same, and the report names another task, as `#34` or by its address: the task of what is left, which the session made | puts the report into the task as a comment, closes the task and moves it to {done}; it makes no task |
            | `{Outcome.Mark} owner` | the working copy is clean | puts {manual} on the task and the report into it as a comment, and moves it back to {todo}; no run takes it while it has the label |
            | `{Outcome.Mark} interrupted` | the working copy is clean | puts {interrupted} on the task and the report into it as a comment, and moves it back to {todo}; the next run takes it first and goes on with the same session, or with a new one where the conversation is gone |
            | `{Outcome.Mark} failed <the reason>` | nothing | puts {manual} on the task and a comment that says what went wrong, moves it back to {todo} and stops the run; the working copy is left as it is, for the owner to look at, and no run takes the task while it has the label |

            The assignee stays however the task ends: on a task that is done or done in part it says who did it, and on one that goes back to {todo} whose task it is.

            The run stops in the same way, with {manual} and such a comment on the task, when a check does not pass, as after `partial` with a report that names no other task, and when a session ended its turn without such a line and telling it to go on brought none. The owner looks at the task and takes the label off, and a run takes it as any other. The next task is never started on a doubt.

            A commit may close its task, as one with `Closes #12` in its message does when it is pushed: the instructions of a project may call for that. toobusy settles the task all the same: it checks what the session said, puts the report into the closed task as a comment and moves it to {done}. A run that was stopped between that push and the end of the task does so when it runs next, for a session that ended with its line. A closed task is never opened again and gets neither label: where the table says that a task goes back to {todo}, a task that is closed already gets the comment only, which says that it is left closed.

            A comment starts with what became of the task, `**Done.**`, `**Done in part.**`, `**Waits for the owner.**`, `**Interrupted.**` or `**Failed.**`, and ends with the session. The report between them is put in as the session wrote it, in the language the instructions of the project call for; the words of toobusy around it are in English.

            ## What holds over the instructions of a project

            The assistant does the work as the instructions of the project say, `CLAUDE.md`, `AGENTS.md`, its skills and the like: how the work is done, how it is checked, how it is committed and pushed. toobusy says nothing of that. Where they differ from the rules of the session, the rules of the session hold, and the session is told so:

            - An instruction to ask the owner, to wait for an approval or a confirmation, or to show a plan first is not followed: the session decides and goes on.
            - An instruction to move the task of the session, to assign it, to label it, to comment on it or to close it is not followed: toobusy keeps that task. An instruction to make other tasks is followed.
            - An instruction to ask the owner before a new task is made, as for its milestone, is not followed: the task of what is left goes where the first message says.
            - An instruction to work in a worktree or in another copy is not followed: the work is done in the working copy the session is started in.

            What a session without its owner should do in another way than one with the owner, such as the checks it runs by itself, is the project's to say, and so is which tasks it makes besides the one for the owner: in the skill a session starts with, as told under “A session starts with the skill of the project”, or in its instructions.

            """);
        return text.ToString();
    }

    static string Labels(IReadOnlyList<string> labels, string one, string many) =>
        $"{(labels.Count == 1 ? one : many)} {string.Join(", ", labels.Select(label => $"`{label}`"))}";
}
