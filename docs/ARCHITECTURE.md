# Architecture

This describes how the code is laid out and why. The behaviour of the tool is described in [IDEA.md](IDEA.md) and, part by part, in [SPEC.md](SPEC.md). The settings and the setup are the parts that exist; the ports named here are planned and get their final shape with the specification.

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

`TooBusy.Core` holds the model (task, queue, session state, outcome, limits, settings) and the use cases (read the queue, run it, watch a session). Everything outside the process is reached through an interface that Core defines and another project implements:

| Port | What it hides | First implementation |
|---|---|---|
| Task tracker | reading the queue, changing a task's labels and status, commenting | `TooBusy.Trackers.GitHub` |
| Assistant | starting, polling, resuming and stopping a session; reading its activity and its limits | `TooBusy.Assistants.ClaudeCode` |
| Workspace | the state of the working copy: branch, changes, sync with the remote | `TooBusy.Infrastructure` |
| Process runner | running a command with a timeout and cancellation; retries when something needs them | `ProcessRunner` in `TooBusy.Infrastructure`, behind `IProcessRunner` |
| System services | keeping the machine awake, notifications, the clock | `TooBusy.Infrastructure` |

Core is tested with hand-written fakes of these ports. A demo is such a fake offered to the user: imitated tracker and sessions behind the same ports.

Sessions are identified by the task they belong to, and nothing in Core assumes that only one runs at a time. The first version runs one; parallel sessions in git worktrees must fit the same ports.

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
| `ISetupDialog` | showing the notes, the answers and the place among the steps; asking: a selection, a multiple choice, a text, the board, a confirmation. Every question can be gone back from | `SetupScreen` in `TooBusy.Cli`; a scripted one in the tests |
| `ISetupEnvironment` | what is installed and logged in, and the `origin` remote | imitated only, until `doctor` brings the real checks; the `origin` it gives is the real one, read by `GitOrigin` in `TooBusy.Infrastructure` |
| `ISetupTracker` | whether a board can be read, the labels; making a board and linking one to the repository | imitated only, until `doctor` brings the reading of GitHub |
| `ISetupBoards` | the boards the user can reach, those linked to the repository, and who a new one can belong to | `GitHubBoards` in `TooBusy.Trackers.GitHub`, which asks `gh` through `IProcessRunner` |
| `ISettingsStore` | the settings file: loading, the text before and after a change, saving | `SettingsFile` in `TooBusy.Infrastructure` |

`TooBusy.Core/Queue` holds the milestone rules (`MilestoneRules`): which of the open milestones is the current one. The setup does not ask for the rule and the settings do not hold it; a run will choose it.

`ProjectSetup` is a walk over its steps: an answer moves it forward, going back from a question moves it to the step before, and going back from the first one leaves the setup. It keeps the answers, so that a step that is asked again proposes what was answered. Nothing is changed before the last step is confirmed; a new board is an answer like any other until then.

The setup never asks for the repository: `ISetupEnvironment` gives the one of the `origin` remote, and the labels are read from it. `BoardSuggestions` is what the question about the board does with a typed text: it finds the boards that fit it and turns the address of any page of a project into the address of its board.

`init --demo` is the real steps and the real screen over the imitations in `TooBusy.Cli/Imitation`: a machine where everything is installed, a tracker with made-up labels that makes and links nothing, and a settings file that is read and never written. The `origin` remote and the boards are not imitated: they are read through `git` and `gh`, which changes nothing, and made-up boards and owners are added after the real ones until there are enough to scroll.

## Terminal

`TooBusy.Cli/Terminal` is everything that knows it talks to a terminal.

- `Palette` is the one place that defines colours: the accent, the error, success, the warning and muted text, each for a dark and a light terminal in truecolor, and as one of the sixteen colours for a terminal that does not announce truecolor. `Palette.Detect` gives the palette without colours when `NO_COLOR` is set or the stream is not a terminal. Commands never write an escape sequence of a colour themselves.
- `TerminalDevice` is the terminal as a screen needs it: its keys, its size, taking Ctrl+C as a key, watching the size of the window, and a timer.
- `Screen` is the alternate screen. It draws a frame whole every time: the bar with the title and the status, the body, the question, the choice and the keys with a rule between them, and the foot, each line cut to the width. What waits for the user is drawn by the screen and blinks: the chosen line of the choice and the block where a text is typed. `Pulse` counts the beats, eighty milliseconds each, and rewrites only that line and that cell in the shade of the moment; drawing a frame does not start the count again. The foot is wrapped to the width, never cut. `TerminalDevice.Every` is the timer behind it, so that tests move the pulse by hand. It keeps the last frame to draw it again when the size changes.
- `Page` is what every screen of the tool shares: it opens and closes the screen, puts the body, the status and the foot it is given around the question of the moment, and reads the keys. Ctrl+C twice in a row throws `OperationCanceledException`, which the command that opened the page catches. While more keys are waiting, as in a paste, it does not draw.
- `SetupScreen` is the setup on a page and `RunScreen` the run with its commands. Every list of the setup is made of the same row: a pointer, a name, what explains it and what is to be noticed about it. `LineEditor` is the text of a field and the caret in it; it knows nothing of the screen.
- `CliContext` carries the folder, the streams, their palettes, the terminal device and the process runner into the commands, so that tests run them with string writers, scripted keys and a faked `git` and `gh`.

The screens are drawn by hand with the escape sequences every terminal knows. Spectre.Console was the first candidate and was not taken: the setup needs the whole window, with a panel that stays at its bottom, and its prompts write below one another.

## Adding a tracker or an assistant

A new project `TooBusy.Trackers.<Name>` or `TooBusy.Assistants.<Name>` implements the port, gets a rule in the architecture tests, and is registered in `TooBusy.Cli` under the name the settings use. Core does not change.

## Native AOT

The tool ships as a native binary, so every source project is built with `IsAotCompatible` and warnings are errors. In practice:

- no reflection-based serialisation: JSON goes through `System.Text.Json` source generation;
- no libraries that need runtime code generation or are not trim-safe;
- command-line parsing with `System.CommandLine`;
- TOML with the syntax tree of Tomlyn, which publishes without warnings;
- globalisation is invariant: output does not depend on the machine's culture.

A dependency is added only after checking that it publishes without AOT warnings.

## Conventions

- .NET 10, C# latest, nullable enabled, warnings as errors.
- Package versions live in `Directory.Packages.props`.
- Tests use xUnit v3.
