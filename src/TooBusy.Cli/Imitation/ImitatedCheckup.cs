using TooBusy.Core.Doctor;
using TooBusy.Core.Run;
using TooBusy.Core.Settings;

namespace TooBusy.Cli.Imitation;

// What `doctor --demo` puts in place of the machine: everything is installed and logged in, the repository and
// the board can be read, and the repository has every label the settings name. Each answer takes a moment, so
// that the check that waits for it can be looked at.
public sealed class ImitatedCheckup(IClock clock, ISettingsStore settings) : ICheckupMachine
{
    static readonly TimeSpan Wait = TimeSpan.FromMilliseconds(600);

    public Platform Platform => Platform.MacOS;

    public Task<bool> HasAsync(Tool tool, CancellationToken cancellationToken) => AfterAsync(true, cancellationToken);

    public Task<bool> HasManagerAsync(CancellationToken cancellationToken) => AfterAsync(true, cancellationToken);

    public Task<bool> TrackerLoggedInAsync(CancellationToken cancellationToken) => AfterAsync(true, cancellationToken);

    public Task<bool> AssistantLoggedInAsync(CancellationToken cancellationToken) => AfterAsync(true, cancellationToken);

    public Task<bool> CanReadRepositoryAsync(string repository, CancellationToken cancellationToken) => AfterAsync(true, cancellationToken);

    public Task<BoardAccess> CheckBoardAsync(string board, CancellationToken cancellationToken) => AfterAsync(BoardAccess.Readable, cancellationToken);

    public Task<IReadOnlyList<string>?> ReadLabelsAsync(string repository, CancellationToken cancellationToken) =>
        AfterAsync<IReadOnlyList<string>?>(
            settings.Load()?.Settings?.Queue.Labels is { } labels ? [.. labels.Blocking, .. labels.Take, labels.Owner, labels.Interrupted] : [],
            cancellationToken);

    async Task<T> AfterAsync<T>(T answer, CancellationToken cancellationToken)
    {
        await clock.DelayAsync(Wait, cancellationToken);
        return answer;
    }
}
