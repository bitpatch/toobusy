# Contributing

toobusy is at an early stage: the design is still being written down, so please open an issue to discuss a change before you start on it.

## Setting up

Install the [.NET 10 SDK](https://dotnet.microsoft.com/download), then:

```sh
dotnet build
dotnet test
```

## Before you open a pull request

- `dotnet build` passes; warnings are errors.
- `dotnet test` passes, and new behaviour has tests.
- `dotnet format --verify-no-changes` passes.
- The change follows [docs/ARCHITECTURE.md](docs/ARCHITECTURE.md), the dependency rules and the Native AOT constraints in particular.

Everything in the repository is written in English.
