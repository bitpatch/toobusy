using System.Runtime.InteropServices;
using TooBusy.Cli.Terminal;
using TooBusy.Core.Processes;
using TooBusy.Core.Run;
using TooBusy.Infrastructure.Machine;
using TooBusy.Infrastructure.Processes;
using TooBusy.Infrastructure.Settings;

namespace TooBusy.Cli;

// Where the tool runs: the folder it is started in and the streams it talks through.
// The defaults are those of a run without a terminal and without the machine around it: no colour, nobody to ask
// and no commands to run.
public sealed record CliContext(string Folder, TextWriter Output, TextWriter Error)
{
    public Palette OutputPalette { get; init; } = Palette.None;

    public Palette ErrorPalette { get; init; } = Palette.None;

    // The terminal to open a screen in; null when the tool does not talk to one.
    public TerminalDevice? Terminal { get; init; }

    // The home folder of the user, to show folders from `~`; empty when it is not known.
    public string Home { get; init; } = "";

    // Runs the commands of the machine; null when there are none to run.
    public IProcessRunner? Processes { get; init; }

    // The folder of the user's own settings, where the choices of the user are kept; null when there is none.
    public string? PersonalFolder { get; init; }

    // The time a run goes by; a test gives one that does not make it wait.
    public IClock Clock { get; init; } = new SystemClock();

    public static CliContext OfProcess() => new(Environment.CurrentDirectory, Console.Out, Console.Error)
    {
        OutputPalette = Palette.Detect(!Console.IsOutputRedirected, Environment.GetEnvironmentVariable),
        ErrorPalette = Palette.Detect(!Console.IsErrorRedirected, Environment.GetEnvironmentVariable),
        Terminal = Console.IsInputRedirected || Console.IsOutputRedirected
            ? null
            : new TerminalDevice(
                () => Console.ReadKey(intercept: true),
                () => (Console.WindowWidth is > 0 and var width ? width : 80, Console.WindowHeight is > 0 and var height ? height : 24))
            {
                KeyWaiting = () => Console.KeyAvailable,
                TakeControlC = taken => Console.TreatControlCAsInput = taken,
                WatchSize = changed => OperatingSystem.IsWindows() ? null : PosixSignalRegistration.Create(PosixSignal.SIGWINCH, _ => changed()),
                Every = (time, tick) => new Timer(_ => tick(), null, time, time),
            },
        Home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
        Processes = new ProcessRunner(),
        PersonalFolder = PersonalSettingsFile.FindFolder(
            Environment.GetEnvironmentVariable, Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), OperatingSystem.IsWindows()),
    };

    // The folder as people write it: from `~` when it is under the home folder.
    public string Shorten(string path) =>
        Home.Length == 0 ? path
        : path == Home ? "~"
        : path.StartsWith(Home + Path.DirectorySeparatorChar, StringComparison.Ordinal) ? "~" + path[Home.Length..]
        : path;
}
