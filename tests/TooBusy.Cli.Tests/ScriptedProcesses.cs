using TooBusy.Core.Processes;

namespace TooBusy.Cli.Tests;

// The commands of the machine as the test scripts them: the answers in `Answers` one after another, or the answer
// of the lambda it was made with. A command it has no answer for fails the test with the command named, so that a
// test that scripts too little fails there, and not on an answer nobody wrote. It remembers every command it was
// asked, with its arguments and its timeout, in their order. `hangs` tells the commands that go on until they are
// told to stop, as one that GitHub does not answer.
public sealed class ScriptedProcesses : IProcessRunner
{
    readonly Func<string, IReadOnlyList<string>, ProcessResult>? answer;
    readonly Func<string, IReadOnlyList<string>, bool>? hangs;
    readonly TaskCompletionSource stopped = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public ScriptedProcesses(params ProcessResult[] answers) => Answers = new(answers);

    public ScriptedProcesses(Func<string, IReadOnlyList<string>, ProcessResult> answer, Func<string, IReadOnlyList<string>, bool>? hangs = null) =>
        (this.answer, this.hangs, Answers) = (answer, hangs, new());

    public ScriptedProcesses(Func<string, ProcessResult> answer)
        : this((command, _) => answer(command))
    {
    }

    public Queue<ProcessResult> Answers { get; }

    public List<(string Command, IReadOnlyList<string> Arguments, TimeSpan Timeout)> Asked { get; } = [];

    // Done once a command that hangs was told to stop.
    public Task Stopped => stopped.Task;

    public async Task<ProcessResult> RunAsync(string command, IReadOnlyList<string> arguments, TimeSpan timeout, CancellationToken cancellationToken)
    {
        Asked.Add((command, arguments, timeout));
        if (hangs?.Invoke(command, arguments) == true)
        {
            try
            {
                await Task.Delay(Timeout.Infinite, cancellationToken);
            }
            finally
            {
                stopped.TrySetResult();
            }
        }

        return Answers.TryDequeue(out var queued) ? queued
            : answer?.Invoke(command, arguments) ?? throw new InvalidOperationException($"The test has no answer for command {Asked.Count}: {string.Join(' ', [command, .. arguments])}");
    }

    public IProcessRunner Inside(string folder) => this;
}
