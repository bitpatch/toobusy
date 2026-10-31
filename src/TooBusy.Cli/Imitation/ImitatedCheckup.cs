using TooBusy.Core.Assistant;
using TooBusy.Core.Doctor;
using TooBusy.Core.Run;
using TooBusy.Core.Settings;

namespace TooBusy.Cli.Imitation;

// What `doctor --demo` puts in place of the machine: everything is installed and logged in, the repository and
// the board can be read, the board of the settings has the statuses the settings name and every other one those of the
// boards of a demo, and the repository has the labels of the made-up project, every label the settings name and the
// skill they name. Each answer takes a moment, so that the check that waits for it can be looked at.
public sealed class ImitatedCheckup(IClock clock, ISettingsStore settings) : ICheckupMachine
{
    static readonly TimeSpan Wait = TimeSpan.FromMilliseconds(600);

    public Platform Platform => Platform.MacOS;

    public Task<ToolLook> FindAsync(Tool tool, CancellationToken cancellationToken) => AfterAsync(new ToolLook(true), cancellationToken);

    public Task<bool> HasManagerAsync(CancellationToken cancellationToken) => AfterAsync(true, cancellationToken);

    public Task<TrackerLoginState> CheckTrackerLoginAsync(CancellationToken cancellationToken) => AfterAsync(TrackerLoginState.LoggedIn, cancellationToken);

    public Task<bool> AssistantLoggedInAsync(CancellationToken cancellationToken) => AfterAsync(true, cancellationToken);

    public Task<bool> CanReadRepositoryAsync(string repository, CancellationToken cancellationToken) => AfterAsync(true, cancellationToken);

    public Task<BoardAccess> CheckBoardAsync(string board, CancellationToken cancellationToken) => AfterAsync(BoardAccess.Readable, cancellationToken);

    public Task<IReadOnlyList<string>?> ReadStatusesAsync(string board, CancellationToken cancellationToken) =>
        AfterAsync<IReadOnlyList<string>?>(
            settings.Load()?.Settings?.Tracker is { Board: { } named } tracker && named.Equals(board, StringComparison.OrdinalIgnoreCase) ? tracker.Statuses.Names : ImitatedSetup.StatusesOf(board),
            cancellationToken);

    public Task<IReadOnlyList<string>?> ReadLabelsAsync(string repository, CancellationToken cancellationToken) =>
        AfterAsync<IReadOnlyList<string>?>(
            settings.Load()?.Settings?.Queue.Labels is { } labels
                ? [.. DemoProject.Labels.Union([.. labels.Blocking, .. labels.Take, labels.Manual, labels.Interrupted], StringComparer.OrdinalIgnoreCase)]
                : DemoProject.Labels,
            cancellationToken);

    public SkillLook FindSkill(string name) => ImitatedSetup.Skill(name);

    async Task<T> AfterAsync<T>(T answer, CancellationToken cancellationToken)
    {
        await clock.DelayAsync(Wait, cancellationToken);
        return answer;
    }
}
