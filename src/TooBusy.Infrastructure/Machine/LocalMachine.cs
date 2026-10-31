using System.Globalization;
using TooBusy.Core.Processes;
using TooBusy.Core.Run;

namespace TooBusy.Infrastructure.Machine;

// The machine a run goes on. On macOS it is kept awake with `caffeinate`, which watches the tool and ends with it,
// and the owner is told things with a notification of the system. On other systems neither is done yet.
public sealed class LocalMachine(IProcessRunner processes, bool mac, int process) : IMachine
{
    static readonly TimeSpan Patience = TimeSpan.FromSeconds(10);

    public LocalMachine(IProcessRunner processes)
        : this(processes, OperatingSystem.IsMacOS(), Environment.ProcessId)
    {
    }

    public IDisposable KeepAwake()
    {
        var awake = new CancellationTokenSource();
        if (mac)
        {
            // It runs until the tool ends or the run does; stopping it is what cancelling the command does.
            _ = Quietly(processes.RunAsync("caffeinate", ["-ims", "-w", process.ToString(CultureInfo.InvariantCulture)], Timeout.InfiniteTimeSpan, awake.Token));
        }

        return new Release(awake);
    }

    public void Notify(string text)
    {
        if (mac)
            _ = Quietly(processes.RunAsync("osascript", ["-e", $"display notification {Quoted(text)} with title \"toobusy\""], Patience, CancellationToken.None));
    }

    // A text as AppleScript writes one.
    static string Quoted(string text) => "\"" + text.Replace("\\", "\\\\", StringComparison.Ordinal).Replace("\"", "\\\"", StringComparison.Ordinal) + "\"";

    // What is done for good measure and waited for by nobody: its end, however it ends, is nobody's.
    static async Task Quietly(Task<ProcessResult> command)
    {
        try
        {
            await command;
        }
        catch (OperationCanceledException)
        {
        }
    }

    sealed class Release(CancellationTokenSource awake) : IDisposable
    {
        public void Dispose()
        {
            awake.Cancel();
            awake.Dispose();
        }
    }
}
