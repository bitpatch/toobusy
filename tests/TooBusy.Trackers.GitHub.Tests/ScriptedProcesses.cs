using TooBusy.Core.Processes;

namespace TooBusy.Trackers.GitHub.Tests;

// The commands of the machine as the test scripts them: the answers in `Answers` one after another, or the answer
// of the lambda it was made with. A command it has no answer for fails the test with the command named, so that a
// test that scripts too little fails there, and not on an answer nobody wrote, as a `gh` that is not installed. It
// remembers every command it was asked, with its arguments and its timeout, in their order.
sealed class ScriptedProcesses : IProcessRunner
{
    readonly Func<string, IReadOnlyList<string>, ProcessResult>? answer;

    public ScriptedProcesses(params ProcessResult[] answers) => Answers = new(answers);

    public ScriptedProcesses(Func<string, IReadOnlyList<string>, ProcessResult> answer) => (this.answer, Answers) = (answer, new());

    public Queue<ProcessResult> Answers { get; }

    public List<(string Command, IReadOnlyList<string> Arguments, TimeSpan Timeout)> Asked { get; } = [];

    // What a test looks at while a command runs, as a file the command is given.
    public Action<IReadOnlyList<string>>? Running { get; set; }

    public static ProcessResult Answered(string output) => new(ProcessStatus.Exited, 0, output, "");

    public static ProcessResult Failed(string error) => new(ProcessStatus.Exited, 1, "", error);

    public Task<ProcessResult> RunAsync(string command, IReadOnlyList<string> arguments, TimeSpan timeout, CancellationToken cancellationToken)
    {
        Asked.Add((command, arguments, timeout));
        Running?.Invoke(arguments);
        return Task.FromResult(Answers.TryDequeue(out var queued) ? queued
            : answer?.Invoke(command, arguments) ?? throw new InvalidOperationException($"The test has no answer for command {Asked.Count}: {string.Join(' ', [command, .. arguments])}"));
    }

    public IProcessRunner Inside(string folder) => this;
}
