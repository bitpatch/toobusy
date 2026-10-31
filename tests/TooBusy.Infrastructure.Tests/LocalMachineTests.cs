using TooBusy.Core.Processes;
using TooBusy.Infrastructure.Machine;

namespace TooBusy.Infrastructure.Tests;

public class LocalMachineTests
{
    readonly ScriptedProcesses processes = new((_, _) => new ProcessResult(ProcessStatus.Exited, 0, "", ""));

    [Fact]
    public void OnAMacTheMachineIsKeptAwakeUntilTheRunLetsItGo()
    {
        var awake = new LocalMachine(processes, mac: true, process: 4242).KeepAwake();

        var (command, arguments, timeout, _, token) = Assert.Single(processes.Asked);
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

        var (command, arguments, _, _, _) = Assert.Single(processes.Asked);
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
}
