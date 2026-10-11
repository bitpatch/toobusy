# Architecture

This describes how the code is laid out and why. The behaviour of the tool is described in [IDEA.md](IDEA.md) and, part by part, in [SPEC.md](SPEC.md). The settings, the setup, the doctor and the run are the parts that exist.

## Projects

```
src/
  TooBusy.Core/                    domain and use cases; depends on nothing
  TooBusy.Infrastructure/          processes, git, operating system services
  TooBusy.Trackers.GitHub/         task tracker adapter: GitHub Issues and Projects
  TooBusy.Assistants.ClaudeCode/   assistant adapter: Claude Code
  TooBusy.Cli/                     composition root, commands, console; the `toobusy` binary
tests/
  TooBusy.Architecture.Tests/      the dependency rules below
  TooBusy.Assistants.ClaudeCode.Tests/
  TooBusy.Cli.Tests/
  TooBusy.Core.Tests/
  TooBusy.Infrastructure.Tests/
  TooBusy.Trackers.GitHub.Tests/
```

A test project is added together with the first code of the project it tests.

## Dependency rules

- `TooBusy.Core` references no other project and no package that reaches outside the process.
- `TooBusy.Infrastructure` and every adapter reference `TooBusy.Core` only. Adapters do not reference each other.
- `TooBusy.Cli` references everything and is the only place where implementations are chosen and wired together.

`TooBusy.Architecture.Tests` reads the project files and fails when a reference breaks these rules. A new project needs a rule there.

## Core and its ports

`TooBusy.Core` holds the model (task, queue, outcome, limits, settings) and the use cases (the setup, the queue, the run). Everything outside the process is reached through an interface that Core defines and another project implements:

| Port | What it hides | Implementations |
|---|---|---|
| `ITaskTracker` | reading the open tasks and the description of one, and changing one: its status on the board, its labels, a comment, closing, making a new task | `GitHubTasks` in `TooBusy.Trackers.GitHub`; `ImitatedTasks` in a demo |
| `IAssistant`, `IAssistantSession` | starting a session with a message, looking at it, telling it something more, asking it something without cutting its turn, stopping it; finding the session of an earlier run by what was kept of it, a `SessionTrace`; the limits of usage | `ClaudeCode` in `TooBusy.Assistants.ClaudeCode`; `ImitatedAssistant` in a demo |
| `IWorkspace` | the working copy: how many files differ from what is committed, how many commits are not pushed | `GitWorkspace` in `TooBusy.Infrastructure` |
| `IProcessRunner` | running a command with a timeout and cancellation, in a folder | `ProcessRunner` in `TooBusy.Infrastructure` |
| `IClock` | the time, and waiting | `SystemClock` in `TooBusy.Infrastructure` |
| `IMachine` | keeping the machine awake, notifying the owner | `LocalMachine` in `TooBusy.Infrastructure` |
| `IRunState` | what a run leaves for the next one: the record of the task it has a session for, a `TaskRecord` | `RunStateFile` in `TooBusy.Infrastructure`; `UnwrittenState` in a demo |
| `IRunView` | where a run tells what happens: the lines of its log, every task that is over, and what it is doing now | `RunFeed` and `PlainRun` in `TooBusy.Cli` |

Core is tested with hand-written fakes of these ports. A demo is such a fake offered to the user: imitated tracker and sessions behind the same ports.

Sessions are identified by the task they belong to, and nothing in the ports assumes that only one runs at a time. The first version runs one; parallel sessions in git worktrees must fit the same ports.

## Run

`TooBusy.Core/Queue` holds the queue. `TaskLineup.Arrange` is its rules, a function of the open tasks and of `QueueRules`, which come from the settings: the tasks that can be taken now and their order, those that open as others close, and those that are held, each with its reason.

`TooBusy.Core/Run` holds the run:

- `Supervisor` is the run itself, behind `IQueueRun`: the loop over the tasks, the watching of a session, the limits, the pause, and telling the tracker how a task went. It writes the record of a task when its session starts and whenever what the session is found by changes, and forgets it at an outcome; before the queue it looks at the record of an earlier run, and goes on with that session in the way it was left: `TaskLineup.Recorded` says whether the task is still gone on with, and `Taking` puts it before the others. It is one flow of control. Commands reach it through `Send`, which only puts them in a line that the run looks at in one place, so nothing in it is shared between threads; a kill is the cancellation of the run. Everything it knows of time comes from `IClock`, so the tests run it start to finish in no time, with a clock that moves only when it is waited on.
- `Outcome` is the line for toobusy at the end of a reply: the contract with the assistant, read here and written nowhere else.
- `Briefing` is every text toobusy sends to a session, and what it writes into the tracker from what the session answers. The texts are those of the tool and know nothing of a project.
- `About` is what `toobusy about` prints: how a run works, put together from the messages of `Briefing`, the lines of `Outcome`, the policy and, where there are settings, the rules of the queue, so that it tells what a run does and nothing else.
- `RunPolicy` is the times and the counts of a run; a demo has its own. `RunPlan` is what the user and the settings give it: the milestone, the rules of the queue, the model and the effort.
- `RunLine`, `TaskEnd` and `RunStatus` are what a run tells: a line of its log with the mark of what it tells; a task that is over, told once however it ended, with its mark and the time it took; and what the run is doing now, with the commands that mean something at the moment and, while a session waits for the owner, what the session said. `RunResult` says how the run ended and, when it did not simply run out of tasks, why.

The adapters know their tools and nothing of a run:

- `GitHubTasks` asks `gh` as the other GitHub adapters do, and the tool itself turns the answers into lines of tab-separated fields. The statuses of a board are known by the names GitHub gives them.
- `ClaudeCode` does a task in a background session of Claude Code. `ClaudeFolders` is where its files are; `SessionSettings` is the settings a session is started with: a hook that prints the request of the run after every step, a status line that writes the state of the session, the limits among it, to a file, and no worktree. `Transcript` reads the conversation Claude Code writes, from where the reading stopped: the steps of the session, its last reply, a usage limit; and, from its start, the plan the session keeps as a list of tasks, which a look gives as `PlanStep`s. The session is found in `claude agents --json`. A session of an earlier run is found there too, by its name or its conversation; one that is not there is lost at once, and nothing is left of one that no process has and whose conversation is not on the machine. When a session is told something, what it said before does not count any more.
- `LocalFolder` is `.toobusy/local` of a project, which keeps itself out of git with a `.gitignore` of its own. `RunStateFile` is the record there, `session.json`, put in place whole.

`Workbench.OpenRun` puts a run together: the real adapters, or `ImitatedRun`, the real `Supervisor` over made-up tasks and sessions that are plays written in the code, lasting as long as the clock says.

## Settings

The settings of a project live in three places:

- `TooBusy.Core/Settings` holds the model (`ProjectSettings`), the names of the keys (`SettingsKeys`) and the validation rules (`SettingsValidator`). The validator knows nothing of TOML: it takes a `SettingsDocument`, every value under its full dotted key with the line it is written on, and gives back the settings or the errors, each with its key and line.
- `TooBusy.Infrastructure/Settings` reaches the disk. `ProjectLocator` walks up to the root of the git working copy, `SettingsFile` is `.toobusy/settings.toml` at that root, and `SettingsToml` turns TOML text into a `SettingsDocument` and settings back into text.
- `TooBusy.Cli` asks for the project and its settings before a command runs, prints the "not set up" message, and maps the outcomes to the exit codes in `ExitCode`.

TOML is parsed with [Tomlyn](https://github.com/xoofx/Tomlyn), through its syntax tree only (`SyntaxParser`): the tree keeps the place of every value, and nothing of it needs reflection. The object serializer of Tomlyn is not used. Writing does not print the tree: `SettingsToml.Write` edits the existing text at the places the tree names, replacing a value that differs, adding a missing key at the end of its section and a missing section, with its comment, at the end of the file. Everything else, comments and unknown keys included, stays character for character, and a new file is the same edit of an empty text. The result is parsed again before it is returned; a layout that cannot be edited this way, such as a section written as an inline table, is refused with a `FormatException` instead of being written wrong.

## Setup

`TooBusy.Core/Setup` holds the steps of `toobusy init` (`ProjectSetup`) and the ports they are written against:

| Port | What it hides | Implementations |
|---|---|---|
| `ISetupDialog` | showing the notes, the answers and the place among the steps; asking: a selection, a multiple choice, a text, the board, a confirmation; waiting for something that is read. Every question can be gone back from, and so can a wait: the reading is told to stop | `SetupScreen` in `TooBusy.Cli`; a scripted one in the tests; none for `init --yes`, where `ProjectSetup` takes what every question proposes |
| `ISetupEnvironment` | what is installed and logged in, and the `origin` remote | `MachineEnvironment` in `TooBusy.Cli`, which runs the checks of the doctor that need no settings and adds the `origin` read by `GitOrigin` of `TooBusy.Infrastructure`; imitated in a demo |
| `ISetupTracker` | whether a board can be read, the labels; making a board, linking one to the repository and making a label, which throw `TrackerException` when GitHub refuses | `GitHubSetup` in `TooBusy.Trackers.GitHub`; imitated in a demo |
| `ISetupBoards` | the boards the user can reach, those linked to the repository, and who a new one can belong to | `GitHubBoards` in `TooBusy.Trackers.GitHub`, which asks `gh` through `IProcessRunner` |
| `ISettingsStore` | the settings file: loading, the text before and after a change, saving | `SettingsFile` in `TooBusy.Infrastructure` |

`TooBusy.Core/Queue` holds the milestone to work on. It is the choice of the user, not a setting of the project. `TooBusy.Core/Assistant` holds two more such choices, the model and the effort the tasks are done with by default: `ModelChoice`, and `ClaudeCodeOptions` with the models and the levels that are offered. The lists are those of Claude Code, the only assistant so far, and they stay in Core until the port of the assistant is asked for them. The choices have two ports:

| Port | What it hides | Implementations |
|---|---|---|
| `IMilestones` | the open milestones of a repository | `GitHubMilestones` in `TooBusy.Trackers.GitHub`; in a demo `ImitatedMilestones`, which adds made-up ones to them |
| `IPersonalSettings`, in `TooBusy.Core/Settings` | where the choices of the user are kept: the milestone, the model and the effort, each one apart | `PersonalSettingsFile` in `TooBusy.Infrastructure`: `projects.toml` of the user's own toobusy folder, a table for each project; in a demo `UnsavedChoice`, which forgets |

`MilestoneStanding` says where a choice stands against the open milestones: not made, open, no milestone, gone, or not checked because the milestones cannot be read. `MilestoneOrder` is the order milestones are offered in.

`ProjectSetup` is a walk over its steps: an answer moves it forward, going back from a question moves it to the step before, and going back from the first one leaves the setup. It keeps the answers, so that a step that is asked again proposes what was answered. Nothing is changed before the last step is confirmed; a new board is an answer like any other until then.

The options of `init` reach the steps as `SetupProposals`: what an option says is proposed before the value of the settings and before what a first setup proposes. The board of an option is checked when the boards are read and its labels when the labels are, once. `--yes` is the same walk without a dialog: `ProjectSetup` then answers every question with what it proposes and the last one with a yes, so a setup that asks nothing cannot differ from one where Enter is pressed all the way. What an option says and cannot be is a warning among the notes where somebody is asked, and where nobody is it ends the setup as failed, before anything is changed; so does a proposed text that its question refuses, and a board that cannot be read. The result carries the notes and a line for each thing that was done on GitHub, which `CliApp` prints as plain lines.

The setup never asks for the repository: `ISetupEnvironment` gives the one of the `origin` remote, and the labels are read from it. `BoardSuggestions` is what the question about the board does with a typed text: it finds the boards that fit it and turns the address of any page of a project into the address of its board.

`Workbench` in `TooBusy.Cli` is where these ports are put together for a project, in one place: the real adapters, or, for `--demo`, the imitations of `TooBusy.Cli/Imitation`. A demo is the real steps and the real screens over a machine where everything is installed, a tracker with made-up labels that makes and links nothing, settings that are read from the file, made up where there is none, and remembered instead of written, and choices of the milestone, the model and the effort that are forgotten when the demo ends. The `origin` remote, the boards and the milestones are not imitated: they are read through `git` and `gh`, which changes nothing, and made-up boards, owners and milestones are added after the real ones until there are enough to scroll.

`Session` is what happens on the page from the moment it is opened: the setup when the project needs it, the choices of the milestone, the model and the effort when the user has none, the menu and the run, each a screen that takes the page for a while. A run that is opened from the menu comes back to it; `toobusy run` runs the queue alone and leaves. It remembers what was done for the report that `CliApp` leaves in the terminal, which says nothing of a run again: a run leaves its own tape there. `Workbench.OutlookAsync` says what a run would take, for the menu: how many tasks, and the one it goes on with first, the task of the record or an interrupted one.

## Doctor

`TooBusy.Core/Doctor` holds the checks of everything a run needs. `Checkup` runs them in their order and knows which check waits for which: `MachineAsync` is the five that need no settings, the tools and their logins, and `RunAsync` adds the settings, the repository, the board and the labels. A check ends as a `CheckResult`: passed, failed or skipped, with what it found or what is wrong and, for a failure, its fix. `Fixes` is the table of the fixes, a function of the tool, the platform and whether its package manager is there. The checks have two ports:

| Port | What it hides | Implementations |
|---|---|---|
| `ICheckupMachine` | the platform; whether `git`, `gh` and `claude` run and whether a package manager is on the path; the logins of the tracker and of the assistant; whether the repository and the board can be read; the labels of the repository | `MachineCheckup` in `TooBusy.Cli`, which asks the tools for their versions through `IProcessRunner` and puts together `GitHubCli` and `GitHubSetup` of `TooBusy.Trackers.GitHub` and `ClaudeLogin` of `TooBusy.Assistants.ClaudeCode`; in a demo `ImitatedCheckup`, a machine that has it all |
| `ICheckupView` | where the checks tell that one starts and how each ends | `CheckList` in `TooBusy.Cli`; `NoCheckupView` where nobody watches, as in the setup |

`Workbench.Checkup` puts them together over the settings and the `origin` remote of the project. `toobusy doctor` shows every check, `toobusy run` runs them before it starts and tells the failures only, and `MachineEnvironment` turns the failed ones of the machine into the notes of the setup.

## Terminal

`TooBusy.Cli/Terminal` is everything that knows it talks to a terminal.

- `Palette` is the one place that defines colours: the accent, the error, success, the warning and muted text, each for a dark and a light terminal in truecolor, and as one of the sixteen colours for a terminal that does not announce truecolor. `Palette.Detect` gives the palette without colours when `NO_COLOR` is set or the stream is not a terminal. Commands never write an escape sequence of a colour themselves.
- `TerminalDevice` is the terminal as a screen needs it: its keys, its size, taking Ctrl+C as a key, watching the size of the window, and a timer.
- `Screen` is the alternate screen, entered with the first frame that is drawn. It draws a frame whole every time: the bar with the title and the status, the body, the question, the choice and the keys with a rule between them, and the foot, each line cut to the width. What waits for the user is drawn by the screen and blinks: the name on the chosen line of the choice, which is the first piece of that line, and the block where a text is typed. `Pulse` counts the beats, eighty milliseconds each, and rewrites only that line and that cell in the shade of the moment; drawing a frame does not start the count again. The same beat moves the light along the dots of a wait, the line that `Page.WaitAsync` puts under what is being read: `Screen.Spark` says how brightly a dot is lit at a step. The foot is wrapped to the width, never cut. `TerminalDevice.Every` is the timer behind it, so that tests move the pulse by hand. It keeps the last frame to draw it again when the size changes. The dots of a wait may stand after a name on a row: the screen finds them in the line. `Screen.Rescue` is what a program that is stopped from outside must still write, whatever has the terminal at the moment.
- `Tape` is the terminal's own screen written as a tape, for a run: `Screen.Unroll` leaves the alternate screen and prints the bar once; what is settled is written for good and scrolls into the history of the terminal, and the foot, a `Strip`, is drawn over itself under it: the cursor goes up to its first line, every line is written again, and what is left of a longer foot is erased. Nothing above the foot is erased while the window keeps its size. The tape remembers what it has settled, as pieces that lay their lines out to a width: when the size of the window changes it erases the terminal with its history and writes the bar, all of them and the foot anew. Lines are cut a column short of the window and the terminal is told not to wrap, because a line the terminal broke would make the foot taller than it is counted. The tape shares the lock and the beat of the screen: the mark of the foot turns, and its chosen line, its cursor and the lit cells of a bar blink, written where they stand by moves counted from the line the cursor rests on. The next frame rolls the tape up and enters the screen again.
- `Page` is what every screen of the tool shares: it takes the terminal and gives it back, puts the body, the status and the foot it is given around the question of the moment, and reads the keys. Ctrl+C twice in a row throws `OperationCanceledException`, which the command that opened the page catches. While more keys are waiting, as in a paste, it does not draw. `WaitAsync` runs something that takes a moment under the dots of a wait and goes on reading the keys meanwhile, looking for one every thirty milliseconds, so that `Back`, Escape and Ctrl+C work before the work ends; work that is not waited for gets its cancellation and is left behind.
- `SetupScreen` is the setup on a page, `MilestoneScreen` the list of the milestones, `ModelScreen` and `EffortScreen` the lists of the models and of the levels of effort, `HomeScreen` the menu, and `AssistantScreen` the list its `Assistant` opens.
- `RunScreen` is the page of a run, on a `Tape`: the tasks that are over and what the run warns of stay, and the foot is the task that is worked on, with the plan of its session as a bar under it, the menu of the commands between two rules, and the keys with the usage limits. The run goes on by itself on another thread and never touches the page: it speaks into a `RunFeed`, and the page takes from there what was said when it draws. The page waits for a key with `Page.ReadAsync`, which looks for one again and again and gives up when something changed, so that the foot is drawn anew; a foot that would show the same is not drawn. When the run is over the page writes how it went and gives the result; however the page is left, the run is killed and never left to go on alone. `RunLook` is what a run looks like: the lines of its log, the line of a task that is over, the bar of a plan, the summary. `PlainRun` prints the log where there is no terminal. `Picker` is the list the screens share: a row is a pointer, a name, what explains it and what is to be noticed about it, and a list that does not fit scrolls between marks. A choice may be off, and may wait: `PickAsync` draws the list again when what it waits for is done, as the menu does while the tasks are counted. Where Escape goes back and does not leave the page, a list ends with `Back`, which names its key: `Picker` adds the row by itself, and the lists that `SetupScreen` draws on its own ask it whether to. `Prompt` is the question that is answered with a text. `LineEditor` is the text of a field and the caret in it; it knows nothing of the screen.
- `CheckList` is the checks of the doctor as lines on the terminal's own screen, outside the alternate one: a mark, the name and what was found, and the fix under a failure. In a terminal the check that is running has a line with the sign of a tape, which turns with the same beat and is erased when the check ends; a list of failures only, as a run wants it, writes them where errors go and leaves nothing of the rest.
- `CliContext` carries the folder, the streams, their palettes, the terminal device, the process runner, the clock and the folder of the user's own settings into the commands, so that tests run them with string writers, scripted keys, a faked `git`, `gh` and `claude`, and a clock that makes nobody wait.

The screens are drawn by hand with the escape sequences every terminal knows. Spectre.Console was the first candidate and was not taken: the setup needs the whole window, with a panel that stays at its bottom, and its prompts write below one another.

The tests read a screen as text. What it looks like is checked with `tools/screenshot.py`: it runs the real binary in a pseudo-terminal, presses the keys of a scenario and draws the screen to pictures in `artifacts/screens`, the rows that blink at several moments of a blink. It is a Python script with its own environment for a terminal emulator and a drawing library; nothing of it is part of toobusy.

## Adding a tracker or an assistant

A new project `TooBusy.Trackers.<Name>` or `TooBusy.Assistants.<Name>` implements the port, gets a rule in the architecture tests, and is registered in `TooBusy.Cli` under the name the settings use. Core does not change.

## Native AOT

The tool ships as a native binary, so every source project is built with `IsAotCompatible` and warnings are errors. In practice:

- no reflection-based serialisation: JSON is read with `JsonDocument` and written with `Utf8JsonWriter`, and nothing is serialised from objects;
- no libraries that need runtime code generation or are not trim-safe;
- command-line parsing with `System.CommandLine`;
- TOML with the syntax tree of Tomlyn, which publishes without warnings;
- globalisation is invariant: output does not depend on the machine's culture.

A dependency is added only after checking that it publishes without AOT warnings.

## Conventions

- .NET 10, C# latest, nullable enabled, warnings as errors.
- Package versions live in `Directory.Packages.props`.
- Tests use xUnit v3.
