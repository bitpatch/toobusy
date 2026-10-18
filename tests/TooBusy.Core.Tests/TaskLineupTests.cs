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
    public void ATaskThatARunHasASessionForIsGoneOnWithWhateverItsStatusAndWhateverItWaitsFor()
    {
        QueueTask[] open = [Task(3), Task(5, BoardStatus.InProgress, "In Progress"), Task(7, blockedBy: [5]), Task(9, blockedBy: [3])];

        var (recorded, why) = TaskLineup.Recorded(open, Rules, 5);
        var lineup = TaskLineup.Arrange(open, Rules).Taking(recorded!);

        Assert.Null(why);
        Assert.Equal([5, 3], lineup.Ready.Select(task => task.Number));
        Assert.Equal([9], lineup.Later.Select(later => later.Task.Number));
        Assert.Equal([(7, "it waits for #5 (its status is In Progress)")], Held(lineup));

        // It is what a run goes on with first, and it counts among the tasks a run would take.
        Assert.Equal(new QueueOutlook(3, recorded), TaskLineup.Arrange(open, Rules).Outlook(Rules, recorded));
        Assert.Equal([9, 3], TaskLineup.Arrange(open, Rules).Taking(open[3]).Ready.Select(task => task.Number));
    }

    [Fact]
    public void ATaskThatARunHasASessionForIsNotGoneOnWithWhenItIsClosedOrALabelKeepsItOut()
    {
        QueueTask[] open = [Task(3, labels: ["feature", "manual"]), Task(5, labels: ["feature", "needs-owner"]), Task(7, labels: ["docs"])];

        Assert.Equal((null, "it has the manual label"), TaskLineup.Recorded(open, Rules, 3));
        Assert.Equal((null, "it waits for the owner: it has the needs-owner label"), TaskLineup.Recorded(open, Rules, 5));
        Assert.Equal((null, "it has none of the labels to take: feature, bug"), TaskLineup.Recorded(open, Rules, 7));
        Assert.Equal((null, "it is not among the open tasks"), TaskLineup.Recorded(open, Rules, 9));
    }

    [Fact]
    public void ATaskWithABlockingLabelIsHeld()
    {
        var lineup = TaskLineup.Arrange([Task(3, labels: ["feature", "Draft"]), Task(5)], Rules);

        Assert.Equal([5], lineup.Ready.Select(task => task.Number));
        Assert.Equal([(3, "it has the draft label")], Held(lineup));
    }

    [Fact]
    public void ATaskWithALabelThisRunSkipsIsHeldWithTheTasksThatWaitForIt()
    {
        var rules = Rules with { Skip = ["Design", "sketch"] };

        var lineup = TaskLineup.Arrange([Task(3, labels: ["feature", "design"]), Task(5, blockedBy: [3]), Task(7), Task(9, labels: ["bug", "Sketch"])], rules);

        // A label is compared whatever its case, and named as the task has it.
        Assert.Equal([7], lineup.Ready.Select(task => task.Number));
        Assert.Equal(
            [(3, "it has the design label, which this run skips"), (5, "it waits for #3 (it has the design label, which this run skips)"), (9, "it has the Sketch label, which this run skips")],
            Held(lineup));
        Assert.Equal("it has the design label, which this run skips", lineup.Holds(3));
    }

    [Fact]
    public void ALabelThatKeepsATaskOutForGoodIsSaidBeforeALabelToSkip()
    {
        var rules = Rules with { Skip = ["design", "nobody-has-it"] };

        var lineup = TaskLineup.Arrange([Task(3, labels: ["feature", "design", "manual"]), Task(5, labels: ["docs", "design"]), Task(7)], rules);

        // A label to skip that no task has skips nothing.
        Assert.Equal([7], lineup.Ready.Select(task => task.Number));
        Assert.Equal([(3, "it has the manual label"), (5, "it has none of the labels to take: feature, bug")], Held(lineup));
    }

    [Fact]
    public void ATaskThatARunHasASessionForIsGoneOnWithWhateverLabelsTheRunSkips()
    {
        var rules = Rules with { Skip = ["design"] };
        QueueTask[] open = [Task(3), Task(5, BoardStatus.InProgress, "In Progress", labels: ["feature", "design"])];

        var (recorded, why) = TaskLineup.Recorded(open, rules, 5);
        var lineup = TaskLineup.Arrange(open, rules);

        Assert.Null(why);
        Assert.Equal([5, 3], lineup.Taking(recorded!).Ready.Select(task => task.Number));
        Assert.Empty(lineup.Taking(recorded!).Held);
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
    public void ALineupSaysWhyARunDoesNotTakeATaskNow()
    {
        var lineup = TaskLineup.Arrange([Task(2, blockedBy: [3, 9]), Task(3), Task(5, labels: ["bug", "manual"]), Task(9)], Rules);

        Assert.Equal("it waits for #3, #9", lineup.Holds(2));
        Assert.Equal("it has the manual label", lineup.Holds(5));

        // A task that can be taken is not kept, and nothing is said of a number that is no task.
        Assert.Null(lineup.Holds(3));
        Assert.Null(lineup.Holds(40));
    }

    [Fact]
    public void TheLineupOfOneTaskHasNoOther()
    {
        var lineup = TaskLineup.Of(Task(3));

        Assert.Equal([3], lineup.Ready.Select(task => task.Number));
        Assert.Empty(lineup.Later);
        Assert.Empty(lineup.Held);
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
        Assert.Equal(("ask", "paused", false), (rules.Manual, rules.Interrupted, rules.Board));
        Assert.Empty(rules.Skip);
        Assert.True(QueueRules.Of(settings with { Tracker = new Settings.TrackerSettings("github", "https://github.com/orgs/acme/projects/1") }).Board);
    }

    static QueueTask Task(int number, BoardStatus status = BoardStatus.Todo, string statusName = "Todo", string[]? labels = null, int[]? blockedBy = null, int[]? parts = null) =>
        new(number, $"Task {number}", $"https://github.com/acme/rocket/issues/{number}", labels ?? ["feature"], status, statusName, blockedBy ?? [], parts ?? []);

    static List<(int Number, string Reason)> Held(TaskLineup lineup) => [.. lineup.Held.Select(held => (held.Task.Number, held.Reason))];
}
