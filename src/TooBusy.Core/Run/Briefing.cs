using System.Globalization;
using System.Text;
using TooBusy.Core.Queue;

namespace TooBusy.Core.Run;

// Everything toobusy says to the assistant, and what it writes into the tracker from what the assistant answers.
// The texts are those of the tool and know nothing of a project: how the work is done, checked and committed is
// said by the instructions of the project itself, which the assistant reads on its own.
public static class Briefing
{
    // The first message of a session: the task, the rules of a session without the owner, and how its last reply
    // is to end. The task is named and not passed on: the session reads it in the tracker, whole and as it is now.
    // `interrupted` is a task taken by its label with nothing of its session here: wrapped up after `/abort`, with a
    // report, or paused by a usage limit elsewhere, with none. Its last comment says which.
    // `lost` is a task that a session worked on before whose conversation is gone, with no report of it.
    // `ground` is what the session needs to make a new task: its own task stays the run's, and new ones are its.
    // `skill` is the skill of the project the session starts with: the message begins with its command, which the
    // assistant loads the skill by, and goes on as it does in a project that names none.
    public static string Task(QueueTask task, NewTasks ground, bool interrupted, bool lost = false, string? skill = null)
    {
        // A task that waits for the owner must be one a run takes once the owner has answered: it gets the labels
        // of this task that let a task in.
        var take = task.Labels.Where(label => ground.Take.Contains(label, StringComparer.OrdinalIgnoreCase) && !label.Equals(ground.Owner, StringComparison.OrdinalIgnoreCase)).ToList();
        var labels = take.Count == 0 ? ""
            : $", and {(take.Count == 1 ? "the label" : "the labels")} {string.Join(", ", take.Select(label => $"`{label}`"))}, as task #{task.Number} has, so that a run takes it after that";
        var milestone = ground.Milestone is { } works ? $"Put it into the milestone `{works}`, the one the owner works on."
            : task.Milestone is { } own ? $"Put it into the milestone `{own}`, the one of task #{task.Number}."
            : $"Put it into no milestone, as task #{task.Number} is in none.";
        var board = ground.Board is null ? "" : $"\n- Put it on the board {ground.Board} with the status `Todo`.";
        var message = new StringBuilder();
        if (skill is not null)
            message.Append(CultureInfo.InvariantCulture, $"{Command(skill, task)}\n\n");
        message.Append(CultureInfo.InvariantCulture, $"""
            You are doing task #{task.Number} of this project without its owner. toobusy, the tool that takes the tasks of the project one after another, started this session and reads how it ends.

            # Task #{task.Number}: {task.Title}

            {task.Url}

            Read the task and its comments before you start: toobusy passes on nothing of them.

            # Rules of this session

            - Nobody will answer. Ask no questions, wait for no approval and do not enter plan mode: decide yourself and go on. Where the instructions of the project (CLAUDE.md, AGENTS.md, its skills and the like) tell you to ask the owner or to wait for a confirmation, do not: the owner started this run to have the task done.
            - Follow the instructions of the project in everything else: how the work is done, how it is checked, how it is committed and pushed. Commit and push the finished work as they say. Where they differ from the rules of this session, these rules hold.
            - Your task is kept by toobusy. It has already moved the task to In Progress, and it assigns the task to the user who runs it. Do not change the status, the assignee or the labels of the task, do not close it and do not comment on it: toobusy does all of that from your last reply.
            - You may make new tasks in the tracker. What cannot be done without the owner becomes one, as said below. Which other tasks a session makes and when, and their labels, their wording and their relations, is said by the instructions of the project.
            - Work in this working copy: make no worktree and do not move to another one. It was clean when you started and it must be clean when you end: everything of yours is committed and pushed, or undone.
            - Keep a plan of the work in your task list. Before you start, lay the work out there as a list of steps; mark a step when you begin it and when it is done, and change the list when the plan changes. toobusy shows the list to the owner as the progress of the task.

            """);
        if (interrupted)
        {
            message.Append("""
                - This task was interrupted before. The last comment of the task that starts with **Interrupted.** or with **Stopped by a usage limit** says how. One that starts with **Interrupted.** is the report of the session before: go on with what it says is left, and do again what it says was undone. One that starts with **Stopped by a usage limit** has no report: the session before worked elsewhere, and its uncommitted changes are not here, so do the task from its description.

                """);
        }

        if (lost)
        {
            message.Append("""
                - A session worked on this task before, and its conversation is gone: it left no report. What it changed and did not commit may still be in the working copy: the changes that `git status` shows before you start are its. Look at them with `git diff`, keep what is right, undo what is not, and go on from there.

                """);
        }

        message.Append(CultureInfo.InvariantCulture, $"""

            # What cannot be done without the owner

            Do everything that does not depend on the owner, leave nothing broken, and commit and push that part. What is left, make a new task of it yourself:

            - Describe it so that a session that has never seen this one knows exactly what to do: what is left, what is done already and where, and the questions for the owner with the answers you see. Name task #{task.Number} in it.
            - Put the label `{ground.Owner}` on it, so that no run takes it before the owner answers{labels}.
            - {milestone}{board}
            - Decide whether it blocks what task #{task.Number} blocks: toobusy closes task #{task.Number}, and what waits for it opens.

            When nothing at all can be done without the owner, make no task: undo your changes and say what you need.

            # Your last reply

            Your last reply is put into the task as its report: say what was done, what you decided on your own and why, and how it was checked. Its last line is for toobusy, one of:

            - `{Outcome.Mark} done`: the task is done, committed and pushed.
            - `{Outcome.Mark} partial`: a part is done, committed and pushed, and you have made the task of what is left. Name that task in the report by its number, as `#<number>`: toobusy closes task #{task.Number} only when the report names it.
            - `{Outcome.Mark} owner`: nothing could be done without the owner. The reply says what is needed, and your changes are undone.
            - `{Outcome.Mark} failed <the reason>`: the work stopped on something you cannot fix, such as a check that fails or a tool that refuses. Leave the working copy as it is.
            """);
        return message.ToString();
    }

    // The line a first message begins with in a project that names a skill: the command of the skill, and the task
    // by its number and its title.
    public static string Command(string skill, QueueTask task) => string.Create(CultureInfo.InvariantCulture, $"/{skill} #{task.Number} {task.Title}");

    // What a session that waits for the owner is told.
    // It says in full what becomes of what the owner must decide, and names the label of the owner: `owner`.
    public static string Alone(int task, string owner) => string.Create(CultureInfo.InvariantCulture, $"""
        The owner is away: no answer to your question and no approval will come. Decide yourself and go on with task #{task} from where you stopped. What cannot be decided without the owner, make a new task of it yourself: describe there what is left and your questions for the owner, and put the label `{owner}` on it, so that no run takes it before the owner answers. End your reply with a `{Outcome.Mark}` line.
        """);

    // What a session is told when the usage limit it ran into has reset.
    public static string AfterLimit(int task) => string.Create(CultureInfo.InvariantCulture, $"""
        The usage limit has reset: go on with task #{task} from where you stopped. The uncommitted changes in the working copy are yours. End your reply with a `{Outcome.Mark}` line, as the rules of this session say.
        """);

    // What a session is told when a later run goes on with it after it was stopped, by a kill or by anything else.
    public static string AfterStop(int task) => string.Create(CultureInfo.InvariantCulture, $"""
        toobusy was stopped while you worked, and it runs again: go on with task #{task} from where you stopped. The uncommitted changes in the working copy are yours. The rules of this session hold as before: nobody will answer, and your last reply ends with a `{Outcome.Mark}` line.
        """);

    // What a session that wrapped its task up after `/abort` is told when a later run goes on with it.
    public static string AfterAbort(int task) => string.Create(CultureInfo.InvariantCulture, $"""
        toobusy runs again: go on with task #{task}. You wrapped it up when the owner stopped the run, and your report is the last comment of the task that starts with **Interrupted.**: do again what you undid, and do what is left. The rules of this session hold as before: commit and push the finished work as the instructions of the project say, and end your last reply with a `{Outcome.Mark}` line.
        """);

    // What a session is told when the owner aborts the run.
    public static string WrapUp(int task) => string.Create(CultureInfo.InvariantCulture, $"""
        The owner asked toobusy to stop: wrap task #{task} up as soon as possible. Let the command that is running finish, and start no new step. Commit nothing and push nothing. Undo your uncommitted changes, so that the working copy is as clean as it was when you started: restore the files you changed and delete the ones you made. Then reply with a report for the session that will go on with the task: what was found out and decided, what was undone, file by file, so that it can be done again from the description, and what is left. End the reply with the line `{Outcome.Mark} interrupted`. A subagent that reads this does none of it: it stops its work and returns what it has to the session that started it.
        """);

    // What is said of a task that was closed before its session ended, as by a commit of the session: the run does
    // not open it again.
    const string Shut = "The task was closed before its session ended, and it is left closed:";

    // The comment a task gets when its session ends: what became of it, the report of the session, and the session.
    // `closed` is a task that is closed already and is not put back among those to do.
    public static string Comment(OutcomeKind kind, string report, string session, string? label = null, bool closed = false)
    {
        var head = kind switch
        {
            OutcomeKind.Owner when closed => $"**Waits for the owner.** Nothing could be done without you. {Shut} open it again when this is answered, and a run takes it.",
            OutcomeKind.Interrupted when closed => $"**Interrupted.** Nothing is committed. {Shut} open it again, and a run takes it.",
            OutcomeKind.Done => "**Done.**",
            OutcomeKind.Partial => "**Done in part.** What is left is a task that the session made: the report names it.",
            OutcomeKind.Owner => $"**Waits for the owner.** Nothing could be done without you. Take the label `{label}` off when this is answered, and a run takes the task again.",
            _ => "**Interrupted.** Nothing is committed; a run takes the task again.",
        };
        return Joined(head, report, $"_Session: `{session}`_");
    }

    // The comment of a task that a run stopped over: what went wrong, what its session said last, and the session.
    // A task that is closed already gets no label, and is not opened again.
    public static string Stopped(string why, string report, string session, string label, bool closed = false) =>
        Joined(
            $"**Failed.** The run stopped over this task: {why}. "
                + (closed ? $"{Shut} look at what went wrong, open it again, and a run takes it." : $"Look at what went wrong, take the label `{label}` off, and a run takes the task again."),
            report,
            $"_Session: `{session}`_");

    // The comment of a task that a usage limit stopped.
    public static string Paused(string why, string session) =>
        Joined($"**Stopped by a usage limit** ({why}). The uncommitted changes are left in the working copy, and the next run goes on with the same session.", "", $"_Session: `{session}`_");

    static string Joined(params string[] parts) => string.Join("\n\n", parts.Where(part => part.Trim().Length > 0).Select(part => part.Trim()));
}
