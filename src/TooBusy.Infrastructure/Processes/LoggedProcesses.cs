using TooBusy.Core.Processes;
using TooBusy.Core.Run;
using TooBusy.Infrastructure.Run;

namespace TooBusy.Infrastructure.Processes;

// A runner that writes every command into the log of the run: the command as it is run, then how it ended, how long
// it took and what it answered, as `Printed` shows them. A cancellation, which is how a command that runs for the
// whole run ends, is written as cut, and thrown on.
public sealed class LoggedProcesses(IProcessRunner inner, RunLogFile log) : IProcessRunner
{
    public IProcessRunner Inside(string folder) => new LoggedProcesses(inner.Inside(folder), log);

    public async Task<ProcessResult> RunAsync(string command, IReadOnlyList<string> arguments, TimeSpan timeout, CancellationToken cancellationToken)
    {
        log.Write($"$ {Printed.Line(command, arguments)}");
        var started = log.Now;
        ProcessResult result;
        try
        {
            result = await inner.RunAsync(command, arguments, timeout, cancellationToken);
        }
        catch (OperationCanceledException)
        {
            log.Write($"→ {command} was cut after {Spoken.Time(log.Now - started)}");
            throw;
        }

        var ended = result.Status switch
        {
            ProcessStatus.NotFound => $"→ {command} is not found",
            ProcessStatus.TimedOut => $"→ {command} did not end in {Spoken.Time(timeout)}, and was stopped",
            _ => $"→ {command} exited {result.ExitCode} in {Spoken.Time(log.Now - started)}",
        };
        log.Write(ended + Printed.Of(result.Output, result.Error, whole: result.Status != ProcessStatus.Exited || result.ExitCode != 0));
        return result;
    }
}
