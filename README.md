# toobusy

Create tasks in GitHub Projects, and `toobusy` takes them one by one and hands them to your AI assistant. You can choose which tasks exactly will be done with `toobusy`.

For now `GitHub` and `Claude Code` are supported.

Unfortunately, I work on a Mac, so I have only tested it there. If anyone helps with Windows and Linux, I will be grateful.

## Installation

With [Homebrew](https://brew.sh), on macOS and Linux:

```sh
brew install bitpatch/tap/toobusy
```

`brew upgrade toobusy` brings a new version.

Without Homebrew, ready binaries are in the [GitHub releases](https://github.com/bitpatch/toobusy/releases/latest).

`toobusy doctor` tells you what is missing. In short, you need Claude Code, GitHub CLI and Git.

## Getting started

The most convenient thing is that your AI assistant can set everything up for you itself. Just ask Claude.

```
Help me set up toobusy for my project. First run toobusy about to figure out how it works, then run toobusy init --assist and follow it step by step.
```

To me, this is the most preferable way to set it up. In your prompt you can give more details, ask what the tool can do, and ask for the skills that automatic work needs.

If you want to set it up yourself, run `toobusy init` in your project folder. The settings are saved to `.toobusy/settings.toml` in your project folder.

## Usage

Once everything is ready, just run the `toobusy` command in your project folder. You will see how many tasks can be done in automatic mode, and choose Run.

Tasks are taken from the milestone you chose, or all of them if you do not specify one.

If something goes wrong, you can always give the logs to your assistant and ask it to set everything up properly: every run leaves one in `.toobusy/local/logs/`, with every line it said, every command it ran and what came back. And if you find a bug, I would be glad if you file it in the issues of the repository and attach the logs there.

Again, if you run into trouble, ask your assistant to help you figure this tool out.

## License

[MIT](LICENSE)
