using TooBusy.Core.Processes;
using TooBusy.Infrastructure.Machine;

namespace TooBusy.Infrastructure.Tests;

public class LocalMachineTests
{
    readonly Recorded processes = new();

    [Fact]
    public void OnAMacTheMachineIsKeptAwakeUntilTheRunLetsItGo()
    {
        var awake = new LocalMachine(processes, mac: true, process: 4242).KeepAwake();

        var (command, arguments, timeout, token) = Assert.Single(processes.Asked);
        Assert.Equal(("caffeinate", Timeout.InfiniteTimeSpan), (command, timeout));
        Assert.Equal(["-ims", "-w", "4242"], arguments);
        Assert.False(token.IsCancellationRequested);

        awake.Dispose();

        Assert.True(token.IsCancellationRequested);
    }

    [Fact]
    public void OnAMacTheOwnerIsToldWithANotification()
    {
        new LocalMachine(processes, mac: true, process: 4242).Notify("#12 \"Export\" waits \\ for an answer");

        var (command, arguments, _, _) = Assert.Single(processes.Asked);
        Assert.Equal("osascript", command);
        Assert.Equal(["-e", "display notification \"#12 \\\"Export\\\" waits \\\\ for an answer\" with title \"toobusy\""], arguments);
    }

    [Fact]
    public void ElsewhereNeitherIsDone()
    {
        var machine = new LocalMachine(processes, mac: false, process: 4242);

        machine.KeepAwake().Dispose();
        machine.Notify("No task is left");

        Assert.Empty(processes.Asked);
    }

    sealed class Recorded : IProcessRunner
    {
        public List<(string Command, IReadOnlyList<string> Arguments, TimeSpan Timeout, CancellationToken Token)> Asked { get; } = [];

        public Task<ProcessResult> RunAsync(string command, IReadOnlyList<string> arguments, TimeSpan timeout, CancellationToken cancellationToken)
        {
            Asked.Add((command, arguments, timeout, cancellationToken));
            return Task.FromResult(new ProcessResult(ProcessStatus.Exited, 0, "", ""));
        }

        public IProcessRunner Inside(string folder) => this;
    }
}
