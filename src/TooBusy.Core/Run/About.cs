using System.Globalization;
using System.Text;
using TooBusy.Core.Queue;

namespace TooBusy.Core.Run;

// How a run works, told to the owner of a project and to the assistant that helps them fit the instructions of the
// project to it. It describes and asks for nothing. The messages are those of Briefing, word for word, and the
// times and the counts those of the policy, so that what is told here is what a session meets.
public static class About
{
    // The task the messages are shown for.
    static readonly QueueTask Sample = new(12, "<the title of the task>", "<the address of the task>", [], BoardStatus.Todo, "Todo", [], []);

    // `rules` are those of the project the text is asked in; null where no settings are read, and then the labels
    // are spoken of without their names.
    public static string Text(QueueRules? rules, RunPolicy policy)
    {
        var owner = rules is null ? "the label of the owner" : $"the `{rules.Owner}` label";
        var interrupted = rules is null ? "the label of interrupted tasks" : $"the `{rules.Interrupted}` label";
        var again = Briefing.Task(Sample, interrupted: true).Split('\n').First(line => line.StartsWith("- This task was interrupted before.", StringComparison.Ordinal));
        var lost = Briefing.Task(Sample, interrupted: false, lost: true).Split('\n').First(line => line.StartsWith("- A session worked on this task before", StringComparison.Ordinal));

        var text = new StringBuilder();
        text.Append("""
            # How toobusy works

            toobusy takes the tasks of a project's tracker one after another and has an AI coding assistant do each of them, in a session of its own and with nobody at the keyboard. This is what such a session meets: what it is told, what is done around it, and what is made of its answer. It is written for the owner of a project and for the assistant that helps them, to fit the instructions of the project to a run. Nothing here has to be done for a run to work: a session is told all it needs in its first message.

            ## A run

            - The tasks are the open tasks of the milestone the user works on, or all the open ones when the user works without a milestone.

            """);
        if (rules is null)
        {
            text.Append("""
                - The settings of a project say which tasks a run takes: the labels that keep a task out, the labels a task must have one of, and whether the project has a board, where a task is taken only from the status `Todo`. No settings are read here, so its labels are not named below: `toobusy init` sets a project up.

                """);
        }
        else
        {
            if (rules.Blocking.Count > 0)
                text.Append(CultureInfo.InvariantCulture, $"- A task with {Labels(rules.Blocking, "the label", "one of the labels")} is not taken.\n");
            if (rules.Take.Count > 0)
                text.Append(CultureInfo.InvariantCulture, $"- Only a task with {Labels(rules.Take, "the label", "one of the labels")} is taken.\n");
            text.Append(rules.Board
                ? "- The project has a board: a task is taken only from the status `Todo`.\n"
                : "- The project has no board: a task has no status, and where a status is named below, nothing is moved.\n");
        }

        text.Append(CultureInfo.InvariantCulture, $"""
            - A task that has {owner} waits for the owner and is not taken.
            - A task waits for the open tasks that block it and for its own open sub-tasks.
            - The tasks that were interrupted are taken first, then the others by their numbers. A task that an earlier run was stopped over comes before any of them.
            - One task is done at a time, in the working copy of the project itself. The working copy must be clean before a task, and it must be clean after it.
            - Each task is done by a session of the assistant of its own, which goes on by itself in the background. Nobody answers its questions or its permission prompts.
            - A task is gone on with by the same session whatever stopped the run over it, as told under “When a run goes on with a session”.

            ## The tracker is kept by toobusy

            A session changes nothing in the tracker: not the status of its task and not its labels, and it does not comment on the task or close it. Before a session starts, toobusy moves the task to `In Progress` and takes {interrupted} off a task that had it. After the session it does what the last line of the reply calls for, as told under “How a task ends”.

            ## What a session is told

            ### The first message

            Every session starts with this message, here for a task #{Sample.Number}. Its first line is always the same but for the number, so a session of a run can be told by it from any other.

            ```text
            {Briefing.Task(Sample, interrupted: false)}
            ```

            The task is named and not passed on: the session reads its description and its comments in the tracker. A task that was interrupted before has one rule more, after the others:

            ```text
            {again}
            ```

            ### A session that waits for the owner

            A session that stops with a question, or ends its turn without the line for toobusy, is left for {Spoken.Time(policy.OwnerWait)}, in which the owner may answer it. Then its turn is cut, and it is told:

            ```text
            {Briefing.Alone(Sample.Number)}
            ```

            A task is told so {policy.Messages} times at most, and it is left to the owner sooner when {policy.IdleMessages} such messages in a row brought no step of its own.

            ### After a usage limit

            A session that runs into a usage limit is stopped with its uncommitted changes left in the working copy. A run waits for a limit that resets soon. For one that does not, it leaves the task to the next run: the task gets {interrupted} and a comment that starts with `**Stopped by a usage limit**`, and goes back to `Todo`. When the limit has reset, in the same run or in the next one, the same session is told:

            ```text
            {Briefing.AfterLimit(Sample.Number)}
            ```

            ### When a run goes on with a session

            A run writes down the task and its session on the machine as soon as the session is started, and forgets them when the task comes to an outcome. The next run looks at what is written before it reads the queue, and takes that task first, whatever its status. A task that was closed since, or got {owner} or a label that keeps a task out, is not taken.

            - A session that still works, because only toobusy was stopped, is watched again and told nothing.
            - A session that ended with a line for toobusy while nobody watched is settled as any other, as told under “How a task ends”.
            - A session that was stopped, as a kill of the run stops it, goes on over the changes it left in the working copy, and is told:

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

            A session that failed, or whose task did not pass the checks, is not gone on with: that is for the owner.

            ### When the owner stops the run at once

            The owner can stop a run after the task that is being done, which its session does not notice, or at once. Then the session is told:

            ```text
            {Briefing.WrapUp(Sample.Number)}
            ```

            A session that works reads it after its next step, beside what that step gave, and its turn is not cut: a subagent may read it too, which is why it says what a subagent does. A session that has not wrapped up in {Spoken.Time(policy.AbortGrace)} has its turn cut and is told again, and so is one that waits for the owner, at once.

            ## How a task ends

            The last reply of a session is the report of the task, and its last line says how the task went. The line is read whatever its case and whatever marks of emphasis stand around it, and the last such line of the reply counts.

            | Last line | toobusy checks | toobusy does |
            |---|---|---|
            | `{Outcome.Mark} done` | the working copy is clean and every commit is pushed | puts the report into the task as a comment, closes the task and moves it to `Done` |
            | `{Outcome.Mark} partial` | the same | makes a new task of what the reply says after `{Outcome.RestMark}`, its title on that line and its description under it, with {owner}, the labels of the first task that let a task in, and the same milestone; puts the report into the first task as a comment that names the new one, closes it and moves it to `Done` |
            | `{Outcome.Mark} owner` | the working copy is clean | puts {owner} on the task and the report into it as a comment, and moves it back to `Todo`; no run takes it while it has the label |
            | `{Outcome.Mark} interrupted` | the working copy is clean | puts {interrupted} on the task and the report into it as a comment, and moves it back to `Todo`; the next run takes it first and goes on with the same session, or with a new one where the conversation is gone |
            | `{Outcome.Mark} failed <the reason>` | nothing | stops the run; the task stays in `In Progress` and the working copy as it is, for the owner to look at |

            The run stops as well, with the task left as it is, when a check does not pass, and when a session ended its turn without such a line and telling it to go on brought none. The next task is never started on a doubt.

            A comment starts with what became of the task, `**Done.**`, `**Done in part.**`, `**Waits for the owner.**` or `**Interrupted.**`, and ends with the session.

            ## What holds over the instructions of a project

            The assistant does the work as the instructions of the project say, `CLAUDE.md`, `AGENTS.md`, its skills and the like: how the work is done, how it is checked, how it is committed and pushed. toobusy says nothing of that. Where they differ from the rules of the session, the rules of the session hold, and the session is told so:

            - An instruction to ask the owner, to wait for an approval or a confirmation, or to show a plan first is not followed: the session decides and goes on.
            - An instruction to move the task, to label it, to comment on it or to close it is not followed: toobusy keeps the tracker.
            - An instruction to work in a worktree or in another copy is not followed: the work is done in the working copy the session is started in.

            What a session without its owner should do in another way than one with the owner, such as the checks it runs by itself, is the project's to say in its instructions.

            """);
        return text.ToString();
    }

    static string Labels(IReadOnlyList<string> labels, string one, string many) =>
        $"{(labels.Count == 1 ? one : many)} {string.Join(", ", labels.Select(label => $"`{label}`"))}";
}
