# Architecture

This describes how the code is laid out and why. The behaviour of the tool is described in [IDEA.md](IDEA.md) and, part by part, in [SPEC.md](SPEC.md). Only the solution skeleton exists so far; the ports named here are planned and get their final shape with the specification.

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

## Adding a tracker or an assistant

A new project `TooBusy.Trackers.<Name>` or `TooBusy.Assistants.<Name>` implements the port, gets a rule in the architecture tests, and is registered in `TooBusy.Cli` under the name the settings use. Core does not change.

## Native AOT

The tool ships as a native binary, so every source project is built with `IsAotCompatible` and warnings are errors. In practice:

- no reflection-based serialisation: JSON goes through `System.Text.Json` source generation;
- no libraries that need runtime code generation or are not trim-safe;
- command-line parsing with `System.CommandLine`;
- globalisation is invariant: output does not depend on the machine's culture.

A dependency is added only after checking that it publishes without AOT warnings.

## Conventions

- .NET 10, C# latest, nullable enabled, warnings as errors.
- Package versions live in `Directory.Packages.props`.
- Tests use xUnit v3.
