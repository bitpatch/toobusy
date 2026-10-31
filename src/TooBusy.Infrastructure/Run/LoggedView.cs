using TooBusy.Core.Run;

namespace TooBusy.Infrastructure.Run;

// A view of a run that writes what the run says into its log on disk too: every line, with its mark and, under it,
// what a screen does not show of it, and every task that is over. What the run shows of itself is not written: it
// changes with every step. A log that cannot be written is said once, through the view it stands before.
public sealed class LoggedView(IRunView inner, RunLogFile log) : IRunView
{
    bool complained;

    public void Say(RunLine line)
    {
        inner.Say(line);
        log.Write(line.Detail is { } detail ? $"{line}\n{detail}" : line.ToString());
        Complain();
    }

    public void Report(TaskEnd ended)
    {
        inner.Report(ended);
        log.Write($"{new RunLine(ended.Mark, "").Symbol} #{ended.Task.Number} is over: {ended.Mark.ToString().ToLowerInvariant()} in {Spoken.Time(ended.Took)}");
        Complain();
    }

    public void Show(RunStatus status) => inner.Show(status);

    // What only the log keeps: the view it stands before is not told.
    public void Trace(string text)
    {
        log.Write(text);
        Complain();
    }

    void Complain()
    {
        if (complained || log.Unwritable is not { } why)
            return;

        complained = true;
        inner.Say(new RunLine(RunMark.Attention, $"the log of the run cannot be written: {why} · the run goes on without it"));
    }
}
