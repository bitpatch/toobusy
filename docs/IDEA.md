# The idea

This file records what toobusy is meant to be. It is the starting point for the detailed specification and for every working session, so that the idea does not have to be retold. It describes intent, not a finished design: where it and the specification disagree, the specification wins.

## What it is

toobusy is a command-line tool that works through the tasks of a project without its owner. It reads the task queue from a task tracker, takes the tasks one after another according to the project's rules, and has an AI coding assistant do each of them in the project's working copy. The owner starts it in a terminal, leaves, and comes back to closed tasks, pushed commits and a report on whatever needs a human.

It is for a developer or a small team who already describe their work as tracker tasks and already trust an assistant to do a well-described task from start to finish.

## How it is used

1. **Install.** One command installs a single native binary. No runtime is needed.
2. **Set up.** `toobusy init`, run in a project folder, creates the `.toobusy/` folder and asks the questions it needs to fill it in.
3. **Configure.** The owner edits the settings in `.toobusy/`: which tracker and which project, which tasks may be taken and in what order, which assistant does them and how.
4. **Run.** `toobusy run` takes tasks until none is left, until the owner stops it, or until something happens that it must not continue past.

## The `.toobusy/` folder

The folder lives in the project's repository and holds everything that is specific to the project:

- the task tracker and the project, board or repository in it;
- the queue rules: which labels mark a task type, which labels keep a task out, which status means "ready", how tasks are ordered, what blocks a task;
- the assistant and how it is started: its permissions;
- the prompts the assistant gets, as editable templates;
- the checks of the working copy before a task and after it: branch, clean tree, sync with the remote, project-specific commands;
- local state of a run, which is not committed.

What is one person's own is not in the folder: the milestone to work on, and the model and the effort the tasks are done with by default, are chosen by each user and kept on their machine.

## What it does during a run

- **Reads the queue.** Tasks that are ready now, tasks that open once others close, and tasks held by something no run can resolve, each with the reason.
- **Checks the working copy** before it starts a task.
- **Starts an assistant session** for the task and watches it: what it is doing, how much context it has used, whether it is waiting for input.
- **Reads the outcome.** The session ends with one machine-readable line that says how the task went: done, partly done, needs the owner, interrupted, failed. toobusy checks that claim against the tracker and the working copy.
- **Handles usage limits.** It waits for a limit to reset and continues the same session, or parks the task for the next run.
- **Handles a session that waits for the owner.** After a countdown it tells the session to decide alone; the owner can do that at once or cancel it.
- **Stops on request.** Gently after the current task, or as soon as possible with nothing committed and a report in the task.
- **Stops by itself** when going on would build on a broken state, and says why.
- **Shows it all** in an interactive console with a status line and commands, or as plain log lines when there is no terminal.
- **Imitates a run** without changing anything, to try the console and the rules.

## Principles

- **Nothing project-specific in the code.** Repositories, labels, branches, prompts and commands come from `.toobusy/`.
- **Trackers and assistants are replaceable.** The first version supports GitHub Issues with GitHub Projects, and Claude Code. Codex, OpenCode and other trackers come later behind the same interfaces.
- **One task at a time in the shared working copy** in the first version. The design must not prevent running several sessions in parallel in separate git worktrees later.
- **Never continue past a doubt.** A task that did not end cleanly stops the run; the next task is not built on it.
- **The owner can always look inside.** Every message names the session and how to open it.
- **A single native binary.** Built with Native AOT for macOS, Linux and Windows; macOS is the first platform.

## Where it comes from

toobusy grows out of a single-file .NET script that runs the task queue of one project. The script proves the idea and is the reference for behaviour: the queue rules, the session watching, the limit handling and the console all exist there. What changes is that everything the script hard-codes becomes configuration, the code is split into replaceable parts with tests, and the tool is installed and set up by people who never saw the script.

## Out of scope

- Planning work or writing tasks. toobusy does the tasks it is given.
- Being an assistant. It starts and supervises existing ones.
- Hosting. It runs on the owner's machine with the owner's logins.

## Open questions for the specification

- The format and the full schema of the settings in `.toobusy/`.
- How the queue rules are expressed, and how far they go beyond labels and statuses.
- The contract between toobusy and the assistant: the prompts it ships, the outcome line, what the project must provide.
- What the project-specific checks and commands look like, and when they run.
- How the tracker is reached: through its command-line tool or directly through its API.
- The set of commands and their options.
- How installation and updates work on each platform.
