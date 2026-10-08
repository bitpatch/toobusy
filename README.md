# toobusy

A command-line tool that works through the tasks of your project while you are too busy to do it yourself. It reads the task queue from your tracker, takes the tasks one after another according to your rules, and has an AI coding assistant do each of them in your working copy.

> **Status: early development.** Nothing is usable yet. The repository holds the project skeleton and the first part of the specification.

## The idea

1. Install a single native binary.
2. Run `toobusy init` in a project folder to create the `.toobusy/` settings folder.
3. Describe the tracker, the queue rules and the assistant there.
4. Run `toobusy run` and leave. It takes tasks until none is left, waits out usage limits, and stops when something needs you.

The first version targets GitHub Issues with GitHub Projects, and Claude Code. Other trackers and assistants (Codex, OpenCode) are planned behind the same interfaces.

Read more in [docs/IDEA.md](docs/IDEA.md), [docs/SPEC.md](docs/SPEC.md) and [docs/ARCHITECTURE.md](docs/ARCHITECTURE.md).

## Building from source

Requires the [.NET 10 SDK](https://dotnet.microsoft.com/download).

```sh
dotnet build
dotnet test
dotnet run --project src/TooBusy.Cli -- --help
```

## Contributing

See [CONTRIBUTING.md](CONTRIBUTING.md).

## License

[MIT](LICENSE)
