using TooBusy.Cli.Terminal;

namespace TooBusy.Cli;

// Where the tool runs: the folder it is started in and the streams it talks through.
// The defaults are those of a run without a terminal: no colour and nobody to ask.
public sealed record CliContext(string Folder, TextWriter Output, TextWriter Error)
{
    public Palette OutputPalette { get; init; } = Palette.None;

    public Palette ErrorPalette { get; init; } = Palette.None;

    // Reads a key; null when there is no terminal to ask questions in.
    public Func<ConsoleKeyInfo>? ReadKey { get; init; }

    public static CliContext OfProcess() => new(Environment.CurrentDirectory, Console.Out, Console.Error)
    {
        OutputPalette = Palette.Detect(!Console.IsOutputRedirected, Environment.GetEnvironmentVariable),
        ErrorPalette = Palette.Detect(!Console.IsErrorRedirected, Environment.GetEnvironmentVariable),
        ReadKey = Console.IsInputRedirected || Console.IsOutputRedirected ? null : () => Console.ReadKey(intercept: true),
    };
}
