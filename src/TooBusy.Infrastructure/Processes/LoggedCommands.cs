using TooBusy.Core.Run;
using TooBusy.Infrastructure.Run;

namespace TooBusy.Infrastructure.Processes;

// The commands of the project with every one written into the log of the run: the line as it is run and what it is
// told in its environment, then how it ended, how long it took and what it printed, as `Printed` shows it.
public sealed class LoggedCommands(IProjectCommands inner, RunLogFile log) : IProjectCommands
{
    public async Task<CommandEnd> RunAsync(string command, IReadOnlyDictionary<string, string> environment, TimeSpan time, CancellationToken cancellationToken)
    {
        var told = string.Join(' ', environment.Select(pair => $"{pair.Key}={Printed.Argument(pair.Value)}"));
        log.Write($"$ {command}{(told.Length == 0 ? "" : " · " + told)}");
        var started = log.Now;
        CommandEnd end;
        try
        {
            end = await inner.RunAsync(command, environment, time, cancellationToken);
        }
        catch (OperationCanceledException)
        {
            log.Write($"→ the command was cut after {Spoken.Time(log.Now - started)}");
            throw;
        }

        var ended = end.Status switch
        {
            CommandStatus.NotStarted => "→ the shell could not be run",
            CommandStatus.TimedOut => $"→ the command did not end in {Spoken.Time(time)}, and was stopped",
            _ => $"→ the command exited {end.ExitCode} in {Spoken.Time(log.Now - started)}",
        };
        log.Write(ended + Printed.Of(end.Printed, "", whole: end.Status != CommandStatus.Exited || end.ExitCode != 0));
        return end;
    }
}
