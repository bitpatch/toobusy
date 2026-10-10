namespace TooBusy.Core.Processes;

// Runs a command of the machine and waits for it, but no longer than the timeout.
public interface IProcessRunner
{
    // A command that is missing, fails or takes too long is told by the result; only a cancellation throws.
    Task<ProcessResult> RunAsync(string command, IReadOnlyList<string> arguments, TimeSpan timeout, CancellationToken cancellationToken);
}

public enum ProcessStatus
{
    Exited,
    NotFound,
    TimedOut,
}

// ExitCode, Output and Error are those of a command that exited; otherwise they say nothing.
public sealed record ProcessResult(ProcessStatus Status, int ExitCode, string Output, string Error);
