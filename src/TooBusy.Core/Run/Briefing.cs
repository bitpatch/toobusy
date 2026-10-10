using System.Globalization;
using System.Text;
using TooBusy.Core.Queue;

namespace TooBusy.Core.Run;

// Everything toobusy says to the assistant, and what it writes into the tracker from what the assistant answers.
// The texts are those of the tool and know nothing of a project: how the work is done, checked and committed is
// said by the instructions of the project itself, which the assistant reads on its own.
public static class Briefing
{
    // As many of the last comments of a task are given to the session, and as much of each text.
    const int Comments = 30;
    const int Longest = 20_000;

    // The first message of a session: the task, the rules of a session without the owner, and how its last reply
    // is to end.
    public static string Task(QueueTask task, TaskText text, bool interrupted)
    {
        var message = new StringBuilder();
        message.Append(CultureInfo.InvariantCulture, $"""
            You are doing task #{task.Number} of this project without its owner. toobusy, the tool that takes the tasks of the project one after another, started this session and reads how it ends.

            # Task #{task.Number}: {task.Title}

            {task.Url}

            {(text.Description.Trim().Length == 0 ? "(The task has no description.)" : Cut(text.Description.Trim()))}

            """);

        if (text.Comments.Count > 0)
        {
            message.Append("\n## Comments\n");
            if (text.Comments.Count > Comments)
                message.Append(CultureInfo.InvariantCulture, $"\n(The {text.Comments.Count - Comments} comments before these are left out.)\n");
            foreach (var comment in text.Comments.TakeLast(Comments))
                message.Append(CultureInfo.InvariantCulture, $"\n**{comment.Author}:**\n\n{Cut(comment.Text.Trim())}\n");
        }

        message.Append(CultureInfo.InvariantCulture, $"""

            # Rules of this session

            - Nobody will answer. Ask no questions, wait for no approval and do not enter plan mode: decide yourself and go on. Where the instructions of the project (CLAUDE.md, AGENTS.md and the like) tell you to ask the owner or to wait for a confirmation, do not: the owner started this run to have the task done.
            - Follow the instructions of the project in everything else: how the work is done, how it is checked, how it is committed and pushed. Commit and push the finished work as they say.
            - The tracker is kept by toobusy. It has already moved the task to In Progress. Do not change the status or the labels of the task, do not close it and do not comment on it: toobusy does all of that from your last reply.
            - Work in this working copy: make no worktree and do not move to another one. It was clean when you started and it must be clean when you end: everything of yours is committed and pushed, or undone.

            """);
        if (interrupted)
        {
            message.Append("""
                - This task was interrupted before. The report of that session is among the comments above: go on with what it says is left, and do again what it says was undone.

                """);
        }

        message.Append(CultureInfo.InvariantCulture, $"""

            # What cannot be done without the owner

            Do everything that does not depend on the owner, leave nothing broken, and commit and push that part. What is left becomes a new task that waits for the owner. Describe it so that a session that has never seen this one knows exactly what to do: what is left, what is done already and where, and the questions for the owner with the answers you see. When nothing at all can be done without the owner, undo your changes and say what you need.

            # Your last reply

            Your last reply is put into the task as its report: say what was done, what you decided on your own and why, and how it was checked. Its last line is for toobusy, one of:

            - `{Outcome.Mark} done`: the task is done, committed and pushed.
            - `{Outcome.Mark} partial`: a part is done, committed and pushed. After the report and before this line put the line `{Outcome.RestMark} <the title of the new task>` and under it the description of that task.
            - `{Outcome.Mark} owner`: nothing could be done without the owner. The reply says what is needed, and your changes are undone.
            - `{Outcome.Mark} failed <the reason>`: the work stopped on something you cannot fix, such as a check that fails or a tool that refuses. Leave the working copy as it is.
            """);
        return message.ToString();
    }

    // What a session that waits for the owner is told.
    public static string Alone(int task) => string.Create(CultureInfo.InvariantCulture, $"""
        The owner is away: no answer to your question and no approval will come. Decide yourself and go on with task #{task} from where you stopped. What cannot be decided without the owner goes into the new task for the owner, as the rules of this session say. End your reply with a `{Outcome.Mark}` line.
        """);

    // What a session is told when the usage limit it ran into has reset.
    public static string AfterLimit(int task) => string.Create(CultureInfo.InvariantCulture, $"""
        The usage limit has reset: go on with task #{task} from where you stopped. The uncommitted changes in the working copy are yours. End your reply with a `{Outcome.Mark}` line, as the rules of this session say.
        """);

    // What a session is told when the owner aborts the run.
    public static string WrapUp(int task) => string.Create(CultureInfo.InvariantCulture, $"""
        The owner asked toobusy to stop: wrap task #{task} up as soon as possible. Let the command that is running finish, and start no new step. Commit nothing and push nothing. Undo your uncommitted changes, so that the working copy is as clean as it was when you started: restore the files you changed and delete the ones you made. Then reply with a report for the session that will go on with the task: what was found out and decided, what was undone, file by file, so that it can be done again from the description, and what is left. End the reply with the line `{Outcome.Mark} interrupted`. A subagent that reads this does none of it: it stops its work and returns what it has to the session that started it.
        """);

    // The comment a task gets when its session ends: what became of it, the report of the session, and the session.
    public static string Comment(OutcomeKind kind, string report, string session, int? rest = null, string? label = null)
    {
        var head = kind switch
        {
            OutcomeKind.Done => "**Done.**",
            OutcomeKind.Partial => string.Create(CultureInfo.InvariantCulture, $"**Done in part.** What is left is #{rest}, which waits for the owner."),
            OutcomeKind.Owner => $"**Waits for the owner.** Nothing could be done without you. Take the label `{label}` off when this is answered, and a run takes the task again.",
            OutcomeKind.Interrupted => "**Interrupted.** Nothing is committed; a run takes the task again.",
            _ => "**Failed.**",
        };
        return Joined(head, report, $"_Session: `{session}`_");
    }

    // The comment of a task that a usage limit stopped.
    public static string Paused(string why, string session) =>
        Joined($"**Stopped by a usage limit** ({why}). The uncommitted changes are left in the working copy, and the next run goes on with the same session.", "", $"_Session: `{session}`_");

    // The description of the task that is made of what is left of another: what the session wrote, where it comes
    // from, and the description of that task, so that nothing has to be looked up.
    public static string Rest(RestTask rest, QueueTask from, string description) => Joined(
        rest.Description,
        string.Create(CultureInfo.InvariantCulture, $"---\n\nLeft from #{from.Number} “{from.Title}”, which toobusy closed as done in part."),
        description.Trim().Length == 0 ? "" : string.Create(CultureInfo.InvariantCulture, $"<details>\n<summary>The description of #{from.Number}</summary>\n\n{description.Trim()}\n\n</details>"));

    // What a task for the rest is called when the session did not name it.
    public static string RestTitle(QueueTask from) => $"What is left of “{from.Title}”";

    static string Joined(params string[] parts) => string.Join("\n\n", parts.Where(part => part.Trim().Length > 0).Select(part => part.Trim()));

    static string Cut(string text) => text.Length <= Longest ? text : text[..Longest] + "\n\n(The text is cut here: it is longer than toobusy passes on.)";
}
