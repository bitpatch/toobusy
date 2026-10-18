# toobusy

A command-line tool that works through the tasks of your project while you are too busy to do it yourself. It reads the task queue from your tracker, takes the tasks one after another according to your rules, and has an AI coding assistant do each of them in your working copy.

## Status

toobusy is in development, and there is no release yet: it is built from source, and it says `1.0.0.dev` as its version.

What works today:

- **Setting a project up**: `toobusy init` asks what it needs and writes the settings, on a screen or by options alone.
- **Setting it up with your assistant**: `toobusy init --assist` tells the assistant how, one step at a time, and changes nothing itself.
- **Checking the machine**: `toobusy doctor` says whether everything a run needs is there, and how to fix what is not.
- **Your own choices**: the milestone to work on, and the model and the effort the tasks are done with.
- **Looking at the queue**: `toobusy queue` shows which tasks a run would take, which open later and which are held, and why, without running anything.
- **Knowing that a run goes on**: `toobusy status` says whether one works in the working copy right now, and on which task, so that nobody commits in the middle of it.
- **A run** with Claude Code over GitHub Issues, with or without a GitHub Projects board: tasks one after another, a session that waits for you and is told to go on alone, usage limits, stopping, and going on with the same session after any interruption.
- **Demos** of all of it over made-up data, which change nothing.

What does not, yet:

- No installer and no released binaries.
- GitHub is the only tracker and Claude Code the only assistant. Codex, OpenCode and other trackers are planned behind the same interfaces.
- Only macOS is tried by hand. The code is meant for Linux and Windows too, but keeping the machine awake and the notifications of a run are done on macOS only.
- The settings hold the board, the labels, the skill a session starts with, the commands a run runs around a task and the branch the tasks are done on, and nothing else to choose: the tracker and the assistant are named there, with one value each. What the board calls its statuses is named there too, where it is not what GitHub calls them. The messages a session gets and the permissions of the assistant are not settings yet: a run knows three statuses of a board, the one to do, the one in progress and the one for what is done, and takes one task at a time.

## The idea

1. Get the `toobusy` binary.
2. Run `toobusy init` in a project folder. It asks about the board, the labels and the skill a session starts with, and writes `.toobusy/settings.toml`; nothing is written by hand.
3. Commit that file, so that everyone who clones the project has the same settings.
4. Run `toobusy run` and leave. It takes tasks until none is left, waits out usage limits, and stops when something needs you.

Read more in [docs/IDEA.md](docs/IDEA.md), [docs/SPEC.md](docs/SPEC.md) and [docs/ARCHITECTURE.md](docs/ARCHITECTURE.md).

## Getting the binary

It takes the [.NET 10 SDK](https://dotnet.microsoft.com/download) and, on macOS, the command line tools of Xcode (`xcode-select --install`). In a clone of this repository:

```sh
dotnet publish src/TooBusy.Cli -c Release -r osx-arm64
```

The result is one native file that needs no runtime, `src/TooBusy.Cli/bin/Release/net10.0/osx-arm64/publish/toobusy`. Copy it to a folder that is on your `PATH`, and check it:

```sh
toobusy --version
```

For another machine put its [runtime identifier](https://learn.microsoft.com/dotnet/core/rid-catalog) in the place of `osx-arm64`, here and in the path.

## Trying it without changing anything

`--demo` is an option of every command: it shows the tool over made-up data, and nothing is made, linked, written or remembered. The demos read only what is safe to read: the settings of the project when it has them, the `origin` remote of the folder and, through `gh`, your boards and the open milestones. Run them in a terminal, inside any git repository:

```sh
toobusy --demo           # the whole tool: the menu, and from it a run, the choices and the setup
toobusy init --demo      # the setup, question by question
toobusy run --demo       # a run over made-up tasks and made-up sessions
toobusy queue --demo     # the queue of that run: what is ready, what opens later, what is held
toobusy status --demo    # what is said of a run that goes on
toobusy doctor --demo    # the checks over a made-up machine that has it all
```

The bar of the screen says `demo`. A demo of a run takes about a minute and shows a task that is done, a session that stops with a question, a task that is done in part, a usage limit and tasks that are held.

## Setting a project up

A project is a git repository whose `origin` remote is a GitHub repository. Its tasks are the issues of that repository.

### What is needed on the machine

- [`git`](https://git-scm.com/downloads).
- [`gh`](https://cli.github.com/), the GitHub CLI, logged in: `gh auth login`. For a project with a board the token needs the `project` scope: `gh auth refresh -s project`.
- [Claude Code](https://claude.com/claude-code), logged in: run `claude`, then `/login`. Run `claude` once in the folder of the project as well, so that it trusts the folder.

### `toobusy init`

Run it in a terminal, in the folder of the project. It asks:

| Question | What it is |
|---|---|
| Project | the GitHub Projects board that keeps the statuses of the tasks: one of yours, one by its address, a new one, or none |
| Todo status, In Progress status, Done status | which status of the board is which; asked only of a board that does not call them as GitHub does |
| Blocking labels | a task with any of these labels is never taken |
| Labels to take | a task needs one of these labels; none chosen means any task |
| Owner's label | the label a run puts on a task that waits for you; such a task is not taken while it has it. `needs-owner` is proposed |
| Interrupt label | the label a run puts on a task it had to stop; such a task is taken first. `interrupted` is proposed |
| Skill | the skill of your project a session starts with, by its name; leave it empty for none. `task-auto` is proposed |

The last page shows what saving will do. Until you choose `Save and exit` nothing is changed anywhere. Saving writes `.toobusy/settings.toml` and does on GitHub what you asked for: it makes a new board, links the board to the repository, and makes the two labels when the repository does not have them. It never touches the working copy or git.

A run knows a board by three statuses: it takes a task only from the first, moves it to the second before its session and to the third when it is done. GitHub names them `Todo`, `In Progress` and `Done` on a new board, and a board that keeps those names is asked nothing about. For a board that calls them otherwise, `Ready`, `Doing`, `Shipped`, `init` asks which of its statuses is which and writes the names to the settings:

```toml
[tracker.statuses]
todo = "Ready"
in-progress = "Doing"
done = "Shipped"
```

A status that is not named there is called as GitHub calls it, and a name is the same whatever the case of its letters. With `--yes` the options `--todo-status`, `--in-progress-status` and `--done-status` name them. While the board lacks a status the settings name, `toobusy doctor` fails and no run starts. `toobusy init --no-board` works without a board.

The skill is yours to write: toobusy names it and does not make it. It is a skill of Claude Code in your project, `.claude/skills/task-auto/SKILL.md`, that says what belongs to the project: how a task is done and checked there without you, what is done before and after it, the language of the report, and which other tasks a session makes. How a run works it need not say: the first message of a session does. `toobusy about` tells what that message says already and what is left to the skill, for you and for the assistant that writes it. While the settings name a skill that is not there, `toobusy doctor` fails and no run starts; `toobusy init --no-skill` names none.

Three settings are not asked for. Two are the commands a run runs around every task, for what must not depend on a session remembering it. Options of `init` set them, `toobusy init --before "scripts/dev.sh ports free" --after "scripts/dev.sh server stop"`, and `--no-before` and `--no-after` take them away; the last page shows what changes before it is saved.

```toml
[task]
before = "scripts/dev.sh ports free"    # before a task is taken; when it fails, the run stops and the task is not touched
after = "scripts/dev.sh server stop"    # after the session of a task, however the task went
```

Each is optional, and a line for the shell that runs at the root of the working copy; the task is in its environment, as `TOOBUSY_TASK`, `TOOBUSY_TASK_TITLE` and `TOOBUSY_TASK_URL`. Without an option `init` keeps them, `doctor` does not run them, and a demo runs none. What they do, the instructions and the skill of your project need not tell a session to do.

The third is the branch the tasks are done on: `toobusy init --branch develop` names it, and `--no-branch` takes it away.

```toml
[workspace]
branch = "develop"    # the working copy must be on it, and it is brought up to date with its upstream before a task
```

Before every task the run checks that the working copy is on that branch, and stops on another one: what is checked out is yours, and it does not change it. Then it fetches what the remote has and moves the branch forward to it, so that the task starts from there; a branch with commits its upstream lacks, one that has parted from it, and a remote that does not answer stop the run before the task is touched. A session that finds the branch moved when it pushes brings it up to date as the instructions of your project say, by a rebase or by a merge, and pushes again. Without a branch, a run does the tasks on whatever is checked out, as it stands.

Commit `.toobusy/settings.toml`. It holds nothing personal, and everyone who clones the project gets the same settings. Running `init` again proposes the current values, so the settings are changed in the same way.

Without questions, as in a script: `toobusy init --yes` takes what the options say and, for the rest, what the setup proposes. `toobusy init --help` lists the options.

### With an assistant

Your assistant can set the project up with you. Tell it, in the folder of the project:

> Set toobusy up here: run `toobusy init --assist` and follow it.

The command prints a walk through the setup for the assistant, one step at a time, and changes nothing itself: the machine, which tasks are taken, the board, your own choices, the instructions of the project, the skill, the branch and the commands around a task, what a session may do, and a last check. Each step says how things stand now, what is to be decided, where to look in the project, what to ask you, the command that sets it and how to check it; `toobusy init --assist 3` prints step 3 alone. The assistant asks you whatever is yours to decide, starts no run, and commits nothing before you agree. The same walk changes a setup that exists.

It is you who tells the assistant to follow the steps: an assistant does not take what a tool prints for an order.

### `toobusy doctor`

```sh
toobusy doctor
```

It checks everything a run needs: `git`, `gh` and Claude Code with their logins, the settings, the repository, the board and its statuses, the labels and the skill. It prints a line per check and, under a failed one, the command that fixes it. It changes nothing, and exits with 0 when nothing failed. One thing it does not check is that Claude Code trusts the folder: a run says so when it cannot start its first session.

## Starting a run

Three choices are yours and not the project's: the milestone whose tasks are taken, and the model and the effort they are done with. They are kept on your machine and are not committed. `toobusy` without a command asks for whatever is missing and then opens its menu, where `Run` starts a run:

```sh
toobusy
```

The same from the command line:

```sh
toobusy milestone v1.0.0    # an open milestone by its title; `--none` takes tasks whatever their milestone
toobusy model opus          # a model by its name; `--default` leaves the choice to Claude Code
toobusy effort high         # low, medium, high, xhigh or max
toobusy run
```

Either way a run starts with the checks of `toobusy doctor`: while one fails it does not start, and says what failed and how to fix it.

A run takes tasks until none is left. Two options of `toobusy run` make it shorter:

```sh
toobusy run --task 12    # task #12 and no other; a task that a run would not take now is not started, and the run says why
toobusy run --max 3      # three tasks at most, then it stops as after `stop`
```

`toobusy queue` shows what a run would do before you start one, and changes nothing:

```text
Ready
  #71 Paint the windows yellow         interrupted
  #12 Export the orders as a file

Later
  #20 Deploy                           waits for #12

Held
  #9  Draft of the new page            it has the draft label
```

It needs the settings and your milestone, not the model and the effort. `toobusy queue --json` prints the same lists for an assistant or a script, each task with its number, its title, its address and its labels.

`toobusy status` says whether a run is going on in the working copy, and changes nothing:

```text
A run is going on: #12 Export the orders as a file · claude attach 1a2b3c4d
```

```text
No run is going on.
```

A run works in the working copy itself, so a commit made beside it lands in the middle of its task. `status` needs nothing but the working copy, and exits with 3 while a run goes on and with 0 when none does: `toobusy status && git commit` commits only then. Tell the assistant you work with to look before it commits, and not the sessions of a run, which commit while their run goes on; `toobusy status --json` prints the same for an assistant. A second run does not start in a working copy while one goes on.

What to know before the first run:

- **Which tasks are taken.** The open issues of your milestone that have a label to take, no blocking label and not the owner's label. A task that a run had to stop is taken first, then the lowest number first; a task that waits for others, those that block it and its own sub-issues, is taken after them. In a project with a board a task is taken only from the status `Todo`.
- **The working tree must be clean.** A run does the tasks in the working copy itself, one at a time, and stops when it finds changes that are not its own. Where the settings name a branch, the working copy must be on it, and the run brings it up to date with the remote before a task.
- **Each task gets a session of Claude Code of its own**, a background one in the permission mode `auto`, with nobody to answer its prompts. The lines of a run that call for a look at a session name the command that opens it: `claude attach <id>`.
- **The session does the work as the instructions of your project say** (`CLAUDE.md`, `AGENTS.md`), and starts with the skill of your project when the settings name one: it changes the code, checks it, commits and pushes. toobusy keeps the task: it moves it on the board, assigns it to you, writes the report of the session into it as a comment, and closes it. New tasks are the session's to make: the one of what is left for you, and those the instructions of your project call for.
- **The commands of your project run around every task**, when the settings have them: one before a task is taken and one after its session, however the task went. A command that fails stops the run.
- **A task that needs you is not guessed at.** What can be done without you is done, and of what is left the session makes a new task with the owner's label and the questions for you. Answer in it, take the label off, and a later run takes it.
- **A task that went wrong stops the run.** When its session failed, did not say how the task went, or left changes or commits that are not pushed, the task gets the owner's label and a comment that says what went wrong and how its session is opened. No run takes it while it has the label: look at it, take the label off, and a later run takes it.
- **Stopping.** Press `/` during a run for its menu: `stop` finishes the current task and takes no other, `abort` stops as soon as possible with nothing committed. Ctrl+C twice kills the run; the next run goes on with the same session.

`toobusy about` tells how a run works in full, with every message a session gets, word for word: it is written for you and for the assistant that helps you fit the instructions of your project to a run and write its skill. The rest is in [docs/SPEC.md](docs/SPEC.md#running-the-tasks): which tasks are held and why, how a task ends, usage limits, and going on after an interruption.

## Working on toobusy

```sh
dotnet build
dotnet test
dotnet run --project src/TooBusy.Cli -- --help
```

See [CONTRIBUTING.md](CONTRIBUTING.md).

## License

[MIT](LICENSE)
