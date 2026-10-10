using TooBusy.Core.Queue;

namespace TooBusy.Core.Tests;

public class TaskLineupTests
{
    static readonly QueueRules Rules = new(["manual", "draft"], ["feature", "bug"], "needs-owner", "interrupted", Board: true);

    [Fact]
    public void TasksAreTakenByTheirNumbers()
    {
        var lineup = TaskLineup.Arrange([Task(7), Task(3), Task(5)], Rules);

        Assert.Equal([3, 5, 7], lineup.Ready.Select(task => task.Number));
        Assert.Empty(lineup.Later);
        Assert.Empty(lineup.Held);
    }

    [Fact]
    public void TasksThatWereInterruptedGoFirst()
    {
        var lineup = TaskLineup.Arrange([Task(3), Task(9, labels: ["bug", "Interrupted"]), Task(5)], Rules);

        Assert.Equal([9, 3, 5], lineup.Ready.Select(task => task.Number));
    }

    [Fact]
    public void TheInterruptedTaskARunTakesFirstIsTheOutlookOfTheQueue()
    {
        var lineup = TaskLineup.Arrange([Task(3), Task(9, labels: ["bug", "Interrupted"]), Task(5, blockedBy: [3])], Rules);

        var outlook = lineup.Outlook(Rules);

        Assert.Equal(9, outlook.Interrupted?.Number);
        Assert.Equal(3, outlook.Tasks);
    }

    [Fact]
    public void WhereNothingWasInterruptedTheOutlookOnlyCounts()
    {
        var outlook = TaskLineup.Arrange([Task(3), Task(5)], Rules).Outlook(Rules);

        Assert.Null(outlook.Interrupted);
        Assert.Equal(2, outlook.Tasks);
    }

    [Fact]
    public void AnInterruptedTaskThatIsHeldOrWaitsIsNotTheOneARunTakesFirst()
    {
        var lineup = TaskLineup.Arrange(
            [Task(3), Task(9, labels: ["bug", "interrupted", "draft"]), Task(11, labels: ["bug", "interrupted"], blockedBy: [3])],
            Rules);

        Assert.Null(lineup.Interrupted(Rules));
    }

    [Fact]
    public void ATaskWithABlockingLabelIsHeld()
    {
        var lineup = TaskLineup.Arrange([Task(3, labels: ["feature", "Draft"]), Task(5)], Rules);

        Assert.Equal([5], lineup.Ready.Select(task => task.Number));
        Assert.Equal([(3, "it has the draft label")], Held(lineup));
    }

    [Fact]
    public void ATaskThatWaitsForTheOwnerIsHeld()
    {
        var lineup = TaskLineup.Arrange([Task(3, labels: ["feature", "needs-owner"])], Rules);

        Assert.Empty(lineup.Ready);
        Assert.Equal([(3, "it waits for the owner: it has the needs-owner label")], Held(lineup));
    }

    [Fact]
    public void ATaskNeedsOneOfTheLabelsToTake()
    {
        var lineup = TaskLineup.Arrange([Task(3, labels: ["docs"]), Task(5, labels: ["Bug"])], Rules);

        Assert.Equal([5], lineup.Ready.Select(task => task.Number));
        Assert.Equal([(3, "it has none of the labels to take: feature, bug")], Held(lineup));
    }

    [Fact]
    public void WithoutLabelsToTakeAnyTaskIsTaken()
    {
        var lineup = TaskLineup.Arrange([Task(3, labels: []), Task(5, labels: ["docs"])], Rules with { Take = [] });

        Assert.Equal([3, 5], lineup.Ready.Select(task => task.Number));
    }

    [Fact]
    public void OnABoardATaskMustStandAsOneToDo()
    {
        var lineup = TaskLineup.Arrange(
            [Task(3, BoardStatus.InProgress, "In Progress"), Task(4, BoardStatus.Missing, ""), Task(5), Task(6, BoardStatus.Other, "Review")],
            Rules);

        Assert.Equal([5], lineup.Ready.Select(task => task.Number));
        Assert.Equal([(3, "its status is In Progress"), (4, "it is not on the board"), (6, "its status is Review")], Held(lineup));
    }

    [Fact]
    public void WithoutABoardTheStatusSaysNothing()
    {
        var lineup = TaskLineup.Arrange([Task(3, BoardStatus.Missing, "")], Rules with { Board = false });

        Assert.Equal([3], lineup.Ready.Select(task => task.Number));
    }

    [Fact]
    public void ATaskOpensAfterTheTasksThatBlockItAndAfterItsParts()
    {
        var lineup = TaskLineup.Arrange([Task(3, blockedBy: [5], parts: [7]), Task(5), Task(7)], Rules);

        Assert.Equal([5, 7], lineup.Ready.Select(task => task.Number));
        var later = Assert.Single(lineup.Later);
        Assert.Equal(3, later.Task.Number);
        Assert.Equal([5, 7], later.After);
    }

    [Fact]
    public void TasksOpenInTheOrderARunWillCloseThem()
    {
        var lineup = TaskLineup.Arrange([Task(2, blockedBy: [3]), Task(3, blockedBy: [9]), Task(9)], Rules);

        Assert.Equal([9], lineup.Ready.Select(task => task.Number));
        Assert.Equal([3, 2], lineup.Later.Select(later => later.Task.Number));
    }

    [Fact]
    public void ATaskThatWaitsForAHeldOneIsHeldWithItsReason()
    {
        var lineup = TaskLineup.Arrange([Task(3, blockedBy: [5]), Task(5, labels: ["bug", "manual"]), Task(8, blockedBy: [3])], Rules);

        Assert.Empty(lineup.Ready);
        Assert.Empty(lineup.Later);
        Assert.Equal(
            [(3, "it waits for #5 (it has the manual label)"), (5, "it has the manual label"), (8, "it waits for #3")],
            Held(lineup));
    }

    [Fact]
    public void ATaskThatWaitsForOneThatIsNotAmongTheTasksIsHeld()
    {
        var lineup = TaskLineup.Arrange([Task(3, blockedBy: [40])], Rules);

        Assert.Equal([(3, "it waits for #40 (not among these tasks)")], Held(lineup));
    }

    [Fact]
    public void TheRulesComeFromTheSettings()
    {
        var settings = new Settings.ProjectSettings(
            new Settings.TrackerSettings("github", null),
            new Settings.QueueSettings(new Settings.LabelSettings(["manual"], ["bug"], "ask", "paused")),
            new Settings.AssistantSettings("claude-code"));

        var rules = QueueRules.Of(settings);

        Assert.Equal(["manual"], rules.Blocking);
        Assert.Equal(["bug"], rules.Take);
        Assert.Equal(("ask", "paused", false), (rules.Owner, rules.Interrupted, rules.Board));
        Assert.True(QueueRules.Of(settings with { Tracker = new Settings.TrackerSettings("github", "https://github.com/orgs/acme/projects/1") }).Board);
    }

    static QueueTask Task(int number, BoardStatus status = BoardStatus.Todo, string statusName = "Todo", string[]? labels = null, int[]? blockedBy = null, int[]? parts = null) =>
        new(number, $"Task {number}", $"https://github.com/acme/rocket/issues/{number}", labels ?? ["feature"], status, statusName, blockedBy ?? [], parts ?? []);

    static List<(int Number, string Reason)> Held(TaskLineup lineup) => [.. lineup.Held.Select(held => (held.Task.Number, held.Reason))];
}
