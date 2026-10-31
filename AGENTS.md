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
dotnet run --project src/TooBusy.Cli -- --demo            # the whole tool over a made-up project, the same in any folder; reads nothing of it and changes nothing
dotnet run --project src/TooBusy.Cli -- init --demo       # imitated setup of the made-up project, from no settings; writes nothing
dotnet run --project src/TooBusy.Cli -- init --assist     # the walk through the setup for an assistant: the introduction and the first step; `--assist 3` is step 3 alone; changes nothing
dotnet run --project src/TooBusy.Cli -- run --demo        # a run over the fifteen made-up tasks of a shop and their sessions; changes nothing
dotnet run --project src/TooBusy.Cli -- run --demo --max 2    # the same, two tasks at most; `--task 101` does that task and no other; `--skip-label docs` leaves out the tasks with that label
dotnet run --project src/TooBusy.Cli -- run --demo --sessions 3    # the same with three sessions at once, each in a made-up worktree
dotnet run --project src/TooBusy.Cli -- queue --demo      # the queue of a demo of a run: what is ready, what opens later, what is held; `--json` for an assistant
dotnet run --project src/TooBusy.Cli -- status            # whether a run is going on in this working copy; exits with 3 while one is; `--json` for an assistant; `--demo` shows a made-up one
dotnet run --project src/TooBusy.Cli -- about             # how a run works, with every message a session gets
dotnet run --project src/TooBusy.Cli -- doctor            # checks everything a run needs; changes nothing
dotnet run --project src/TooBusy.Cli -- doctor --demo     # the same over a made-up machine that has it all
dotnet publish src/TooBusy.Cli -c Release -r osx-arm64    # native binary
dotnet publish src/TooBusy.Cli -c Release -r osx-arm64 -p:VersionSuffix=    # the same as a release builds it, whose version has no suffix
gh workflow run release.yml --ref develop                 # the release workflow without the publishing, with the checks and the four binaries
tools/screenshot.py init --demo -- "until:Choose another" shot down enter until:Filter shot tab shot blink   # pictures of the screen in a new folder of artifacts/screens; the setup reads from GitHub first: wait for its question
tools/screenshot.py run --demo -- "until:Edit src" shot type:/ shot   # a run goes on by itself: wait for what it shows
```

A change is ready when the build, the tests and the format check pass. A test run is green only when its summary says so: `total`, `failed: 0`, `succeeded`. The whole run takes a few seconds; one that prints no summary has hung, and is not a pass.

## Rules

- Follow the dependency rules in docs/ARCHITECTURE.md. `TooBusy.Core` depends on nothing; adapters depend on Core only. The architecture tests enforce it.
- Keep the code Native AOT compatible: no reflection-based serialisation, no dependency that is not trim-safe. Check a new package by publishing the native binary.
- Nothing project-specific in the code. The repository is the one of the `origin` remote, and the board and the labels come from the settings in `.toobusy/`. What a project will choose later, such as the statuses of its board, branches, prompts and commands, goes into the settings too, never into the code.
- A change of what a screen looks like is looked at before it is shown: take pictures with `tools/screenshot.py` and read them. The tests see the text of a screen, not its colours. Its terminal has all the colours, so the `Basic` and `None` palettes are not seen on it, and its font is not the one of a real terminal. `tools/screenshot.py --help` has its options.
- The code and the documentation say the same thing. Before a task is finished, check whether its change calls for one in the documentation: docs/SPEC.md, docs/IDEA.md, docs/ARCHITECTURE.md, the README, the text of `toobusy about`, the steps of `toobusy init --assist`, the help of a command and this file. Change what it does, in the same commit.
- Text for users, such as the README and release notes, is written for an ordinary user: as short and as simple as it can be. Every extra line makes the tool harder to understand. Leave out what the tool can tell by itself, and details few readers need.
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
- A release is made by bumping `VersionPrefix` in `Directory.Build.props` on `develop`, opening the release pull request from `develop` into `main` and merging it. `.github/workflows/release.yml` then publishes the binaries to GitHub Releases and updates the formula in the Homebrew tap `bitpatch/homebrew-tap`, and a merge whose version already has its tag publishes nothing. docs/ARCHITECTURE.md has the details.
- The original script that toobusy grows out of is not in this repository. Ask the owner when its behaviour matters.

## Reporting

Every reply to the owner ends with a separate last line that says what is expected of them:

- `✅ Completed` — the task is fully finished: the work is done, committed and pushed, and nothing more is required from the owner. For a request that changes nothing in the repository, such as a question, it means the request is fully answered.
- `📦 Ready — commit and push?` — the work is fully done and checked, and only the commit and the push are left. In a conversation with the owner, stop here instead of committing. When the owner agrees, commit, push and end that reply with `✅ Completed`.
- `❓ Your answer is needed` — the work cannot go on, or cannot be finished, without the owner: a question, a choice, an approval or a step only they can do. State what exactly is needed right above this line.

Never write `✅ Completed` while anything is left: an unpushed commit, a failing check, an open question.
