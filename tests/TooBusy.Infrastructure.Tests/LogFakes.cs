using TooBusy.Core.Run;
using TooBusy.Infrastructure.Run;

namespace TooBusy.Infrastructure.Tests;

// A clock that stands still until the test moves it.
sealed class FixedClock(DateTimeOffset start) : IClock
{
    public static readonly DateTimeOffset Start = new(2030, 1, 1, 9, 0, 0, TimeSpan.Zero);

    public DateTimeOffset Now { get; set; } = start;

    public Task DelayAsync(TimeSpan time, CancellationToken cancellationToken)
    {
        Now += time;
        return Task.CompletedTask;
    }
}

sealed class FakeCommands(Func<string, CommandEnd> answer) : IProjectCommands
{
    public Task<CommandEnd> RunAsync(string command, IReadOnlyDictionary<string, string> environment, TimeSpan time, CancellationToken cancellationToken) =>
        Task.FromResult(answer(command));
}

sealed class FakeView : IRunView
{
    public List<RunLine> Lines { get; } = [];

    public List<TaskEnd> Ends { get; } = [];

    public List<RunStatus> Statuses { get; } = [];

    public void Say(RunLine line) => Lines.Add(line);

    public void Report(TaskEnd ended) => Ends.Add(ended);

    public void Show(RunStatus status) => Statuses.Add(status);
}

// A run that does what the test wrote with the view it is given, and ends as the play says.
sealed class FakeRun(Func<IRunView, Task<RunResult>> play) : IQueueRun
{
    public List<RunCommand> Sent { get; } = [];

    public Task<RunResult> RunAsync(IRunView view, CancellationToken kill) => play(view);

    public void Send(RunCommand command) => Sent.Add(command);
}

static class Logged
{
    // The lines of a log without their times and their indents, and without the line that says when the run started.
    public static List<string> Lines(RunLogFile log) => [.. File.ReadAllLines(log.FullPath).Skip(1).Select(line => line[13..])];
}
