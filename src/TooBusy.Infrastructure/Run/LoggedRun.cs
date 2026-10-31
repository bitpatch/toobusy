using TooBusy.Core.Run;

namespace TooBusy.Infrastructure.Run;

// A run with its log on disk: what it says goes to the view and into the log, and the log is closed when the run is
// over, however it ended. The log is named under the last line of the run, however it ended, so that the owner knows
// where to look.
public sealed class LoggedRun(IQueueRun inner, RunLogFile log) : IQueueRun
{
    public async Task<RunResult> RunAsync(IRunView view, CancellationToken kill)
    {
        var logged = new LoggedView(view, log);
        try
        {
            var result = await inner.RunAsync(logged, kill);
            if (log.Unwritable is null)
                logged.Say(new RunLine(RunMark.Note, $"the log of the run: {log.DisplayPath}"));
            log.Write($"run ended: {result.End.ToString().ToLowerInvariant()}{(result.Why is null ? "" : $" · {result.Why}")} · {result.Done} done");
            return result;
        }
        catch (Exception failed)
        {
            log.Write($"run ended with an error:\n{failed}");
            throw;
        }
        finally
        {
            log.Dispose();
        }
    }

    public void Send(RunCommand command, int? task = null) => inner.Send(command, task);

    public void Note(string text) => log.Write(text);
}
