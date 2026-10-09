# Architecture

This describes how the code is laid out and why. The behaviour of the tool is described in [IDEA.md](IDEA.md) and, part by part, in [SPEC.md](SPEC.md). The settings are the first part that exists; the ports named here are planned and get their final shape with the specification.

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
| Process runner | running a command with a timeout, retries and cancellation | `TooBusy.Infrastructure` |
| System services | keeping the machine awake, notifications, the clock | `TooBusy.Infrastructure` |

Core is tested with hand-written fakes of these ports. A dry run is such a fake offered to the user: imitated tracker and sessions behind the same ports.

Sessions are identified by the task they belong to, and nothing in Core assumes that only one runs at a time. The first version runs one; parallel sessions in git worktrees must fit the same ports.

## Settings

The settings of a project live in three places:

- `TooBusy.Core/Settings` holds the model (`ProjectSettings`), the names of the keys (`SettingsKeys`) and the validation rules (`SettingsValidator`). The validator knows nothing of TOML: it takes a `SettingsDocument`, every value under its full dotted key with the line it is written on, and gives back the settings or the errors, each with its key and line.
- `TooBusy.Infrastructure/Settings` reaches the disk. `ProjectLocator` walks up to the root of the git working copy, `SettingsFile` is `.toobusy/settings.toml` at that root, and `SettingsToml` turns TOML text into a `SettingsDocument` and settings back into text.
- `TooBusy.Cli` asks for the project and its settings before a command runs, prints the "not set up" message, and maps the outcomes to the exit codes in `ExitCode`.

TOML is parsed with [Tomlyn](https://github.com/xoofx/Tomlyn), through its syntax tree only (`SyntaxParser`): the tree keeps the place of every value, and nothing of it needs reflection. The object serializer of Tomlyn is not used. Writing does not print the tree: `SettingsToml.Write` edits the existing text at the places the tree names, replacing a value that differs, adding a missing key at the end of its section and a missing section, with its comment, at the end of the file. Everything else, comments and unknown keys included, stays character for character, and a new file is the same edit of an empty text. The result is parsed again before it is returned; a layout that cannot be edited this way, such as a section written as an inline table, is refused with a `FormatException` instead of being written wrong.

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
