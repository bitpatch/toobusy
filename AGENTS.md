# Instructions for AI assistants

toobusy is a command-line tool that works through the tasks of a project's tracker with AI coding assistants. Read [docs/IDEA.md](docs/IDEA.md) for what it is meant to be, [docs/SPEC.md](docs/SPEC.md) for what it does in detail and [docs/ARCHITECTURE.md](docs/ARCHITECTURE.md) for how the code is laid out before changing anything.

## Language

This is a public repository. Everything in it and around it is written in English: code, comments, documentation, commit messages, issues, pull requests and the project board. This holds even when the conversation with the owner is in another language.

## Commands

Run from the repository root:

```sh
dotnet build                          # build everything; warnings are errors
dotnet test                           # run all tests
dotnet format --verify-no-changes     # check formatting; `dotnet format` fixes it
dotnet run --project src/TooBusy.Cli -- --help
dotnet run --project src/TooBusy.Cli -- --demo            # the whole tool over made-up data; changes nothing
dotnet run --project src/TooBusy.Cli -- init --demo       # imitated setup; writes nothing
dotnet run --project src/TooBusy.Cli -- run --demo        # a run over made-up tasks and sessions; changes nothing
dotnet run --project src/TooBusy.Cli -- about             # how a run works, with every message a session gets
dotnet run --project src/TooBusy.Cli -- doctor            # checks everything a run needs; changes nothing
dotnet run --project src/TooBusy.Cli -- doctor --demo     # the same over a made-up machine that has it all
dotnet publish src/TooBusy.Cli -c Release -r osx-arm64    # native binary
tools/screenshot.py init --demo -- shot tab shot blink    # pictures of the screen in artifacts/screens
tools/screenshot.py run --demo -- "until:Edit src" shot type:/ shot   # a run goes on by itself: wait for what it shows
```

A change is ready when the build, the tests and the format check pass. A test run is green only when its summary says so: `total`, `failed: 0`, `succeeded`. The whole run takes a few seconds; one that prints no summary has hung, and is not a pass.

## Rules

- Follow the dependency rules in docs/ARCHITECTURE.md. `TooBusy.Core` depends on nothing; adapters depend on Core only. The architecture tests enforce it.
- Keep the code Native AOT compatible: no reflection-based serialisation, no dependency that is not trim-safe. Check a new package by publishing the native binary.
- Nothing project-specific in the code: repositories, labels, branches, prompts and commands belong to the settings in `.toobusy/`.
- A change of what a screen looks like is looked at before it is shown: take pictures with `tools/screenshot.py` and read them. The tests see the text of a screen, not its colours.
- New behaviour comes with tests. Core is tested with hand-written fakes of its ports.
- Package versions go into `Directory.Packages.props`, not into project files.
- Do not add placeholder code for features that are not being built yet.

## Process

- Work is planned as issues of `bitpatch/toobusy` on the organisation's `toobusy` project board.
- Issue types are the labels `feature`, `bug`, `chore` and `docs`.
- A new issue goes into a milestone. When there is one open milestone, add the issue to it. When there are several, ask the owner which one before creating the issue.
- When an issue is taken into work, move it to `In Progress` on the board. When the work is finished, commit the result, push it to `develop` and close the issue. In a conversation with the owner, ask before committing, as described in Reporting.
- All development happens on `develop`, the default branch. `main` is protected and holds released code only: it changes through a release pull request from `develop`. Never commit or push to `main`.
- CI runs only for release pull requests into `main`, not for `develop`. Run the build, the tests and the format check locally before pushing.
- The original script that toobusy grows out of is not in this repository. Ask the owner when its behaviour matters.

## Reporting

Every reply to the owner ends with a separate last line that says what is expected of them:

- `✅ Completed` — the task is fully finished: the work is done, committed and pushed, and nothing more is required from the owner. For a request that changes nothing in the repository, such as a question, it means the request is fully answered.
- `📦 Ready — commit and push?` — the work is fully done and checked, and only the commit and the push are left. In a conversation with the owner, stop here instead of committing. When the owner agrees, commit, push and end that reply with `✅ Completed`.
- `❓ Your answer is needed` — the work cannot go on, or cannot be finished, without the owner: a question, a choice, an approval or a step only they can do. State what exactly is needed right above this line.

Never write `✅ Completed` while anything is left: an unpushed commit, a failing check, an open question.
