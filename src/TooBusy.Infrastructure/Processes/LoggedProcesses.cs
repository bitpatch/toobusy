using TooBusy.Core.Processes;
using TooBusy.Core.Run;
using TooBusy.Infrastructure.Run;

namespace TooBusy.Infrastructure.Processes;

// A runner that writes every command into the log of the run: the command as it is run, then how it ended, how long
// it took and what it answered, as `Printed` shows them. A cancellation, which is how a command that runs for the
// whole run ends, is written as cut, and thrown on.
//
// A command that is run again and again, as the list of the sessions is while one works, would fill the log with
// the same lines. So a command that passed, run again right after itself with nothing else written in between, is
// not written once more when it passes again: it is counted, and the count is written before whatever comes next in
// the log. A command that fails is always written in full. Runners made by Inside share what was run last, as they
// share the log.
public sealed class LoggedProcesses : IProcessRunner
{
    readonly IProcessRunner inner;
    readonly RunLogFile log;
    readonly Repeats repeats;

    public LoggedProcesses(IProcessRunner inner, RunLogFile log)
        : this(inner, log, new Repeats(log))
    {
    }

    LoggedProcesses(IProcessRunner inner, RunLogFile log, Repeats repeats) => (this.inner, this.log, this.repeats) = (inner, log, repeats);

    public IProcessRunner Inside(string folder) => new LoggedProcesses(inner.Inside(folder), log, repeats);

    public async Task<ProcessResult> RunAsync(string command, IReadOnlyList<string> arguments, TimeSpan timeout, CancellationToken cancellationToken)
    {
        var line = $"$ {Printed.Line(command, arguments)}";

        // A command like the last one is written only once it is known that it did not pass again.
        var again = repeats.Last(line);
        if (!again)
            log.Write(line);
        var started = log.Now;
        ProcessResult result;
        try
        {
            result = await inner.RunAsync(command, arguments, timeout, cancellationToken);
        }
        catch (OperationCanceledException)
        {
            if (again)
                log.Write(line);
            log.Write($"→ {command} was cut after {Printed.Took(log.Now - started)}");
            repeats.Forget();
            throw;
        }

        var failed = result.Status != ProcessStatus.Exited || result.ExitCode != 0;
        if (!failed && again && repeats.Count(line, started))
            return result;

        if (again)
            log.Write(line);
        log.Write(result.Status switch
        {
            ProcessStatus.NotFound => $"→ {command} is not found",
            ProcessStatus.NotStarted => $"→ {command} could not be started: {result.Error}",
            ProcessStatus.TimedOut => $"→ {command} did not end in {Spoken.Time(timeout)}, and was stopped",
            _ => $"→ {command} exited {result.ExitCode} in {Printed.Took(log.Now - started)}" + Printed.Of(result.Output, result.Error, failed),
        });
        if (failed)
            repeats.Forget();
        else
            repeats.Wrote(line);
        return result;
    }

    // The command that passed and was written last, as long as nothing else was written after it, and how many
    // times it passed again since, from when.
    sealed class Repeats(RunLogFile log)
    {
        readonly object gate = new();
        string? line;
        long mark;
        int count;
        DateTimeOffset first;

        public bool Last(string next)
        {
            lock (gate)
                return line == next && mark == log.Written;
        }

        // Counts one more run that passed, and holds the line of the count for the log; false when something else was
        // written in the meantime, so that the run is not one right after the last.
        public bool Count(string next, DateTimeOffset started)
        {
            lock (gate)
            {
                if (line != next || mark != log.Written)
                    return false;

                if (count++ == 0)
                    first = started;
                log.Hold($"  the same {count} {(count == 1 ? "time" : "times")} more in {Printed.Took(log.Now - first)}, each exited 0");
                return true;
            }
        }

        public void Wrote(string next)
        {
            lock (gate)
                (line, mark, count) = (next, log.Written, 0);
        }

        public void Forget()
        {
            lock (gate)
                (line, count) = (null, 0);
        }
    }
}
