# toobusy

A command-line tool that works through the tasks of your project while you are too busy to do it yourself. It reads the task queue from your tracker, takes the tasks one after another according to your rules, and has an AI coding assistant do each of them in your working copy.

## Status

toobusy is in development, and there is no release yet: it is built from source, and it says `1.0.0.dev` as its version.

What works today:

- **Setting a project up**: `toobusy init` asks what it needs and writes the settings, on a screen or by options alone.
- **Checking the machine**: `toobusy doctor` says whether everything a run needs is there, and how to fix what is not.
- **Your own choices**: the milestone to work on, and the model and the effort the tasks are done with.
- **A run** with Claude Code over GitHub Issues, with or without a GitHub Projects board: tasks one after another, a session that waits for you and is told to go on alone, usage limits, stopping, and going on with the same session after any interruption.
- **Demos** of all of it over made-up data, which change nothing.

What does not, yet:

- No installer and no released binaries.
- GitHub is the only tracker and Claude Code the only assistant. Codex, OpenCode and other trackers are planned behind the same interfaces.
- Only macOS is tried by hand. The code is meant for Linux and Windows too, but keeping the machine awake and the notifications of a run are done on macOS only.
- The settings hold the board and the labels, and nothing else. The messages a session gets, the permissions of the assistant, the names of the board's statuses and the checks around a task are not settings yet: a run knows the statuses `Todo`, `In Progress` and `Done`, and takes one task at a time.

## The idea

1. Get the `toobusy` binary.
2. Run `toobusy init` in a project folder. It asks about the board and the labels and writes `.toobusy/settings.toml`; nothing is written by hand.
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

`--demo` is an option of every command: it shows the tool over made-up data, and nothing is made, linked, written or remembered. The demos read only what is safe to read: the `origin` remote of the folder and, through `gh`, your boards and the open milestones. Run them in a terminal, inside any git repository:

```sh
toobusy --demo           # the whole tool: the menu, and from it a run, the choices and the setup
toobusy init --demo      # the setup, question by question
toobusy run --demo       # a run over made-up tasks and made-up sessions
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
| Blocking labels | a task with any of these labels is never taken |
| Labels to take | a task needs one of these labels; none chosen means any task |
| Owner's label | the label a run puts on a task that waits for you; such a task is not taken while it has it. `needs-owner` is proposed |
| Interrupt label | the label a run puts on a task it had to stop; such a task is taken first. `interrupted` is proposed |

The last page shows what saving will do. Until you choose `Save and exit` nothing is changed anywhere. Saving writes `.toobusy/settings.toml` and does on GitHub what you asked for: it makes a new board, links the board to the repository, and makes the two labels when the repository does not have them. It never touches the working copy or git.

Commit `.toobusy/settings.toml`. It holds nothing personal, and everyone who clones the project gets the same settings. Running `init` again proposes the current values, so the settings are changed in the same way.

Without questions, as in a script: `toobusy init --yes` takes what the options say and, for the rest, what the setup proposes. `toobusy init --help` lists the options.

### `toobusy doctor`

```sh
toobusy doctor
```

It checks everything a run needs: `git`, `gh` and Claude Code with their logins, the settings, the repository, the board and the labels. It prints a line per check and, under a failed one, the command that fixes it. It changes nothing, and exits with 0 when nothing failed.

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

What to know before the first run:

- **Which tasks are taken.** The open issues of your milestone that have a label to take and no blocking label, the lowest number first. In a project with a board a task is taken only from the status `Todo`.
- **The working tree must be clean.** A run does the tasks in the working copy itself, one at a time, and stops when it finds changes that are not its own.
- **Each task gets a session of Claude Code of its own**, a background one in the permission mode `auto`, with nobody to answer its prompts. Every line of a run that speaks of a session names the command that opens it: `claude attach <id>`.
- **The session does the work as the instructions of your project say** (`CLAUDE.md`, `AGENTS.md`): it changes the code, checks it, commits and pushes. toobusy keeps the tracker: it moves the task on the board, writes the report of the session into the task as a comment, and closes it.
- **A task that needs you is not guessed at.** What can be done without you is done, and what is left becomes a new task with the owner's label and the questions for you. Answer in it, take the label off, and a later run takes it.
- **Stopping.** Press `/` during a run for its menu: `stop` finishes the current task and takes no other, `abort` stops as soon as possible with nothing committed. Ctrl+C twice kills the run; the next run goes on with the same session.

`toobusy about` tells how a run works in full, with every message a session gets, word for word: it is written for you and for the assistant that helps you fit the instructions of your project to a run. The rest is in [docs/SPEC.md](docs/SPEC.md#running-the-tasks): which tasks are held and why, how a task ends, usage limits, and going on after an interruption.

## Working on toobusy

```sh
dotnet build
dotnet test
dotnet run --project src/TooBusy.Cli -- --help
```

See [CONTRIBUTING.md](CONTRIBUTING.md).

## License

[MIT](LICENSE)
