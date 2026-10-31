# toobusy

Create tasks in GitHub Projects, and `toobusy` takes them one by one and hands them to your AI assistant. You can choose which tasks exactly will be done with `toobusy`.

For now `GitHub` and `Claude Code` are supported.

Unfortunately, I work on a Mac, so I have only tested it there. If anyone helps with Windows and Linux, I will be grateful.

## Installation

Every release on GitHub has a ready binary for macOS and Linux, and nothing else is needed to run it. Download the one for your machine and put it on the `PATH`. On a Mac with Apple Silicon:

```sh
curl -fsSL https://github.com/bitpatch/toobusy/releases/latest/download/toobusy-macos-arm64.tar.gz | tar -xz
sudo mkdir -p /usr/local/bin
sudo mv toobusy /usr/local/bin/
toobusy --version
```

On another machine, put the name of its archive into the first command:

| Machine | Archive |
| --- | --- |
| macOS, Apple Silicon | `toobusy-macos-arm64.tar.gz` |
| macOS, Intel | `toobusy-macos-x64.tar.gz` |
| Linux, x64 | `toobusy-linux-x64.tar.gz` |
| Linux, arm64 | `toobusy-linux-arm64.tar.gz` |

The first release is not out yet: until then these links lead nowhere, and the way is to build from source, as said below.

Only macOS is tested so far. The Linux binaries are built and start, and that is all that is known of them. They need glibc 2.34 or newer, as on Ubuntu 22.04, Debian 12 or RHEL 9, so they do not run on Alpine.

Download with `curl`, as above, and not in a browser. The binaries are not signed yet, and macOS refuses to open one that came through a browser. If that happened, `xattr -d com.apple.quarantine toobusy` lets it run.

The file `SHA256SUMS` of a release has the checksums of its archives. To check an archive you downloaded as a file, download `SHA256SUMS` next to it and run `shasum -a 256 -c SHA256SUMS --ignore-missing` on macOS or `sha256sum -c SHA256SUMS --ignore-missing` on Linux.

### Building from source

You need the .NET 10 SDK. In a clone of the repository run

```sh
dotnet publish src/TooBusy.Cli -c Release -r osx-arm64
```

with `osx-x64`, `linux-x64` or `linux-arm64` for another machine. The binary lands in `src/TooBusy.Cli/bin/Release/net10.0/osx-arm64/publish/`, and such a build says it is one in development: `1.0.0.dev` and its commit.

### What you need on the machine

- [`git`](https://git-scm.com/downloads), which gets into the remote of the project without asking for a password: a run has nobody to answer it, and turns git's prompts off. Over HTTPS a credential helper keeps the login, as `gh auth setup-git` sets one up; over ssh the key is in an ssh agent or has no passphrase.
- [`gh`](https://cli.github.com/), the GitHub CLI, logged in: `gh auth login`. For a project with a board the token needs the `project` scope: `gh auth refresh -s project`.
- [Claude Code](https://claude.com/claude-code), logged in: run `claude`, then `/login`. Run `claude` once in the project folder too, so that it trusts the folder.

You can also run the command

```sh
toobusy doctor
```

It checks everything a run needs: `git`, `gh` and `Claude Code` with their logins, the settings, the repository, the board and its statuses, the labels and the skill.

If you run into trouble, ask your assistant to run toobusy doctor and help you figure out the problem.

## Getting started

The most convenient thing is that your AI assistant can set it up for you itself. Just ask, for example, Claude Code. Everything needed for the setup is already in the tool itself: `toobusy about` tells how it works, and `toobusy init --assist` walks the assistant through the setup step by step. So you can just type a message like this

```
Help me set up toobusy for my project. First run toobusy about to figure out how it works, then run toobusy init --assist and follow it step by step.
```

To me, this is the most preferable way to set it up. You can give more details in your prompt or ask about what the tool can do. The assistant will write the special skill for handling tasks in automatic mode itself along the way, `init --assist` leads to it.

If something goes wrong, you can always give the logs to your assistant and ask it to set everything up properly: every run leaves one in `.toobusy/local/logs/`, with every line it said, every command it ran and what came back. And if you find a bug, I would be glad if you file it in the issues of the repository and attach the logs there.

If you want to set it up yourself, run `toobusy init` in your project folder.

To have several tasks done at once, each in a git worktree of its own, choose how many sessions work side by side with `toobusy sessions 3`, or give `toobusy run --sessions 3` for one run. The sessions push to the branch of the tasks themselves, and they share the usage limits of your account.

To see what it looks like first, add `--demo` to any command, for example `toobusy --demo` or `toobusy run --demo`. A demo shows a made-up project, a small online shop with its own board and fifteen tasks, and is the same in any folder: it reads nothing of the folder it is run in, starts no session and changes nothing.

Once everything is ready, just run the `toobusy` command in your project folder. You will see how many tasks can be done in automatic mode, and choose Run.

Tasks are taken from the milestone you chose, or all of them if you do not specify one.

Again, if you run into trouble, ask your assistant to help you figure this tool out.

## License

[MIT](LICENSE)
