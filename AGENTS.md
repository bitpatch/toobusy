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
dotnet run --project src/TooBusy.Cli -- run --dry-run     # imitated run; changes nothing
dotnet publish src/TooBusy.Cli -c Release -r osx-arm64    # native binary
```

A change is ready when the build, the tests and the format check pass.

## Rules

- Follow the dependency rules in docs/ARCHITECTURE.md. `TooBusy.Core` depends on nothing; adapters depend on Core only. The architecture tests enforce it.
- Keep the code Native AOT compatible: no reflection-based serialisation, no dependency that is not trim-safe. Check a new package by publishing the native binary.
- Nothing project-specific in the code: repositories, labels, branches, prompts and commands belong to the settings in `.toobusy/`.
- New behaviour comes with tests. Core is tested with hand-written fakes of its ports.
- Package versions go into `Directory.Packages.props`, not into project files.
- Do not add placeholder code for features that are not being built yet.

## Process

- Work is planned as issues of `bitpatch/toobusy` on the organisation's `toobusy` project board.
- Issue types are the labels `feature`, `bug`, `chore` and `docs`.
- All development happens on `develop`, the default branch. `main` is protected and holds released code only: it changes through a release pull request from `develop`. Never commit or push to `main`.
- CI runs only for release pull requests into `main`, not for `develop`. Run the build, the tests and the format check locally before pushing.
- The original script that toobusy grows out of is not in this repository. Ask the owner when its behaviour matters.
