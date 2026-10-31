using TooBusy.Cli.Imitation;
using TooBusy.Core.Queue;
using TooBusy.Core.Settings;

namespace TooBusy.Cli.Tests;

public class ImitatedRunsTests
{
    // `Settings` of the menu of a demo changes the settings it remembers: the tasks it made up are made anew for
    // them, and the demo starts over with its task that a run was killed over.
    [Fact]
    public async Task TheTasksOfADemoAreMadeAnewForLabelsThatWereChanged()
    {
        var runs = new ImitatedRuns(new InstantClock(), watched: false);

        var first = await runs.ReadOpenAsync(Settings(["feature"]), null, TestContext.Current.CancellationToken);
        var changed = await runs.ReadOpenAsync(Settings(["enhancement"]), null, TestContext.Current.CancellationToken);

        Assert.Contains(first!, task => task.Has("feature"));
        Assert.DoesNotContain(changed!, task => task.Has("feature"));
        Assert.Contains(changed!, task => task.Has("enhancement"));
        Assert.Equal(ImitatedTasks.Killed, Assert.Single(runs.Records()).Number);
    }

    [Fact]
    public void TheTasksOfADemoFitTheSameRulesReadAgainWhateverTheyAreToldToSkip()
    {
        var tasks = new ImitatedTasks(Rules(), killed: true);

        Assert.True(tasks.Fits(Rules()));
        Assert.True(tasks.Fits(Rules() with { Skip = ["docs"] }));
    }

    [Fact]
    public void TheTasksOfADemoDoNotFitOtherLabelsAnotherBoardOrOtherStatuses()
    {
        var tasks = new ImitatedTasks(Rules(), killed: true);

        Assert.False(tasks.Fits(Rules(take: ["feature"])));
        Assert.False(tasks.Fits(Rules(blocking: [])));
        Assert.False(tasks.Fits(Rules(manual: "owner")));
        Assert.False(tasks.Fits(Rules(interrupted: "stopped")));
        Assert.False(tasks.Fits(Rules(board: null)));
        Assert.False(tasks.Fits(Rules() with { Statuses = new BoardStatuses("Ready", "Doing", "Shipped") }));
    }

    // Settings that hold no task by a label and have no board leave out the tasks a label or a status would hold;
    // the task held for the owner stays, with a label to take of the settings, from the first again where they
    // have fewer than the four of the demo.
    [Fact]
    public async Task TheTasksOfADemoLeaveOutThoseTheRulesCannotHold()
    {
        var tasks = await new ImitatedTasks(Rules(blocking: [], board: null), killed: false).ReadOpenAsync(null, TestContext.Current.CancellationToken);

        Assert.Equal(Enumerable.Range(101, 12), tasks.Select(task => task.Number));
        Assert.Equal(["feature", "manual"], tasks[^1].Labels);
    }

    static QueueRules Rules(IReadOnlyList<string>? take = null, IReadOnlyList<string>? blocking = null, string manual = "manual", string interrupted = "interrupted", string? board = DemoProject.Board) =>
        QueueRules.Of(Settings(take ?? ["feature", "bug"], blocking ?? ["draft"], manual, interrupted, board));

    static ProjectSettings Settings(IReadOnlyList<string> take, IReadOnlyList<string>? blocking = null, string manual = "manual", string interrupted = "interrupted", string? board = DemoProject.Board) => new(
        new TrackerSettings(SettingsKeys.GitHubTracker, board),
        new QueueSettings(new LabelSettings(blocking ?? ["draft"], take, manual, interrupted)),
        new AssistantSettings(SettingsKeys.ClaudeCodeAssistant));
}
