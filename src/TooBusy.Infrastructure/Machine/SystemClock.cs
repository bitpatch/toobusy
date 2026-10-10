using TooBusy.Core.Run;

namespace TooBusy.Infrastructure.Machine;

// The clock of the machine.
public sealed class SystemClock : IClock
{
    public DateTimeOffset Now => DateTimeOffset.UtcNow;

    public Task DelayAsync(TimeSpan time, CancellationToken cancellationToken) => Task.Delay(time, cancellationToken);
}
