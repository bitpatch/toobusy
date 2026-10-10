using TooBusy.Core.Processes;

namespace TooBusy.Trackers.GitHub.Tests;

// Gives the answers it is told to, one for each command, and remembers what it was asked to run.
sealed class FakeProcesses : IProcessRunner
{
    public Queue<ProcessResult> Answers { get; } = new();

    public List<(string Command, IReadOnlyList<string> Arguments)> Asked { get; } = [];

    public static ProcessResult Answered(string output) => new(ProcessStatus.Exited, 0, output, "");

    public static ProcessResult Failed(string error) => new(ProcessStatus.Exited, 1, "", error);

    public Task<ProcessResult> RunAsync(string command, IReadOnlyList<string> arguments, TimeSpan timeout, CancellationToken cancellationToken)
    {
        Asked.Add((command, arguments));
        return Task.FromResult(Answers.Count > 0 ? Answers.Dequeue() : new ProcessResult(ProcessStatus.NotFound, 0, "", ""));
    }
}
