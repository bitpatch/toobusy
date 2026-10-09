using TooBusy.Core.Queue;
using TooBusy.Core.Settings;

namespace TooBusy.Core.Tests;

public class MilestoneRulesTests
{
    static readonly Milestone[] Open =
    [
        new("Backlog", null),
        new("v.0.10.0", new DateOnly(2030, 1, 1)),
        new("v.0.2.0", null),
        new("Release 1.0", new DateOnly(2029, 6, 1)),
        new("Polish", new DateOnly(2029, 12, 1)),
    ];

    [Fact]
    public void LowestVersionComparesNumberByNumber()
    {
        Assert.Equal("v.0.2.0", Choose(MilestoneRule.LowestVersion, Open)?.Title);
    }

    [Theory]
    [InlineData("v.0.2.0", 0L, 2L, 0L)]
    [InlineData("v1.4", 1L, 4L)]
    [InlineData("Release 2.0 (spring 2030)", 2L, 0L)]
    [InlineData("Sprint 7", 7L)]
    public void TheVersionIsTheFirstRunOfDotSeparatedNumbers(string title, params long[] version)
    {
        Assert.Equal(version, MilestoneRules.VersionOf(title));
    }

    [Fact]
    public void ATitleWithoutNumbersHasNoVersion()
    {
        Assert.Null(MilestoneRules.VersionOf("Backlog"));
    }

    [Fact]
    public void AShorterVersionCountsZerosForTheRest()
    {
        Milestone[] open = [new("v1.0.1", null), new("v1", null)];

        Assert.Equal("v1", Choose(MilestoneRule.LowestVersion, open)?.Title);
    }

    [Fact]
    public void EarliestDueIgnoresMilestonesWithoutADueDate()
    {
        Assert.Equal("Release 1.0", Choose(MilestoneRule.EarliestDue, Open)?.Title);
    }

    [Fact]
    public void FixedIsTheOpenMilestoneWithTheTitle()
    {
        Assert.Equal(Open[4], MilestoneRules.Choose(new MilestoneSettings(MilestoneRule.Fixed, "Polish"), Open));
        Assert.Null(MilestoneRules.Choose(new MilestoneSettings(MilestoneRule.Fixed, "Closed long ago"), Open));
    }

    [Fact]
    public void NoneChoosesNoMilestone()
    {
        Assert.Null(Choose(MilestoneRule.None, Open));
    }

    [Theory]
    [InlineData(MilestoneRule.LowestVersion)]
    [InlineData(MilestoneRule.EarliestDue)]
    public void ARuleFindsNothingWhenNoMilestoneFits(MilestoneRule rule)
    {
        Assert.Null(Choose(rule, [new Milestone("Backlog", null)]));
        Assert.Null(Choose(rule, []));
    }

    static Milestone? Choose(MilestoneRule rule, Milestone[] open) => MilestoneRules.Choose(new MilestoneSettings(rule, null), open);
}
