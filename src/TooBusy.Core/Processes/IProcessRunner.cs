namespace TooBusy.Core.Processes;

// Runs a command of the machine and waits for it, but no longer than the timeout.
public interface IProcessRunner
{
    // A command that is missing, cannot be started, fails or takes too long is told by the result; only a cancellation
    // throws.
    Task<ProcessResult> RunAsync(string command, IReadOnlyList<string> arguments, TimeSpan timeout, CancellationToken cancellationToken);

    // A runner whose commands start in the folder; without it they start where the tool was started.
    IProcessRunner Inside(string folder);
}

public enum ProcessStatus
{
    Exited,

    // The command is not on the path.
    NotFound,

    // The command could not be started, as one that is not executable, one whose arguments are too long or one whose
    // folder is not there.
    NotStarted,

    TimedOut,
}

// ExitCode, Output and Error are those of a command that exited; otherwise they say nothing, but for the Error of a
// command that was not started, which says why.
public sealed record ProcessResult(ProcessStatus Status, int ExitCode, string Output, string Error);
