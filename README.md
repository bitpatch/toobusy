# toobusy

Create tasks in GitHub Projects, and `toobusy` takes them one by one and hands them to your AI assistant. You can choose which tasks exactly will be done with `toobusy`.

For now `GitHub` and `Claude Code` are supported.

Unfortunately, I work on a Mac, so I have only tested it there. If anyone helps with Windows and Linux, I will be grateful.

## Installation

There is no release yet, but it is coming soon, and then this is where it will say how to install. For now, build from source: `dotnet publish src/TooBusy.Cli -c Release -r osx-arm64`, you need the .NET 10 SDK.

### What you need on the machine

- [`git`](https://git-scm.com/downloads).
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

If something goes wrong, you can always give the logs to your assistant and ask it to set everything up properly. And if you find a bug, I would be glad if you file it in the issues of the repository and attach the logs there.

If you want to set it up yourself, run `toobusy init` in your project folder.

Once everything is ready, just run the `toobusy` command in your project folder. You will see how many tasks can be done in automatic mode, and choose Run.

Tasks are taken from the milestone you chose, or all of them if you do not specify one.

Again, if you run into trouble, ask your assistant to help you figure this tool out.

## License

[MIT](LICENSE)
