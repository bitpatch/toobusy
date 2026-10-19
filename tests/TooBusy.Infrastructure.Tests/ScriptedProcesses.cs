using TooBusy.Core.Processes;

namespace TooBusy.Infrastructure.Tests;

// The commands of the machine as the test scripts them: the answers it was made with one after another, or the
// answer of the lambda it was made with. A command it has no answer for fails the test with the command named, so
// that a test that scripts too little fails there, and not on an answer nobody wrote. It remembers every command it
// was asked, with its arguments, its timeout, the folder of the runner it was asked through and the token it was
// given, in their order.
sealed class ScriptedProcesses : IProcessRunner
{
    readonly Func<string, IReadOnlyList<string>, ProcessResult>? answer;

    public ScriptedProcesses(params ProcessResult[] answers) => Answers = new(answers);

    public ScriptedProcesses(Func<string, IReadOnlyList<string>, ProcessResult> answer) => (this.answer, Answers) = (answer, new());

    public Queue<ProcessResult> Answers { get; }

    public List<(string Command, IReadOnlyList<string> Arguments, TimeSpan Timeout, string? Folder, CancellationToken Token)> Asked { get; } = [];

    public Task<ProcessResult> RunAsync(string command, IReadOnlyList<string> arguments, TimeSpan timeout, CancellationToken cancellationToken) =>
        Run(null, command, arguments, timeout, cancellationToken);

    public IProcessRunner Inside(string folder) => new In(this, folder);

    Task<ProcessResult> Run(string? folder, string command, IReadOnlyList<string> arguments, TimeSpan timeout, CancellationToken cancellationToken)
    {
        Asked.Add((command, arguments, timeout, folder, cancellationToken));
        return Task.FromResult(Answers.TryDequeue(out var queued) ? queued
            : answer?.Invoke(command, arguments) ?? throw new InvalidOperationException($"The test has no answer for command {Asked.Count}: {string.Join(' ', [command, .. arguments])}"));
    }

    // The same machine, whose commands are asked in a folder.
    sealed class In(ScriptedProcesses machine, string folder) : IProcessRunner
    {
        public Task<ProcessResult> RunAsync(string command, IReadOnlyList<string> arguments, TimeSpan timeout, CancellationToken cancellationToken) =>
            machine.Run(folder, command, arguments, timeout, cancellationToken);

        public IProcessRunner Inside(string other) => new In(machine, other);
    }
}
