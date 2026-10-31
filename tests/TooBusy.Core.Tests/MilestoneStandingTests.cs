using TooBusy.Core.Queue;

namespace TooBusy.Core.Tests;

public class MilestoneStandingTests
{
    static readonly Milestone[] Open = [new("v0.2.0", null, 3), new("v0.3.0", new DateOnly(2030, 1, 1), 12)];

    [Fact]
    public void WithoutAChoiceThereIsNothingToRunWith()
    {
        var standing = MilestoneStanding.Of(null, Open);

        Assert.Equal(MilestoneState.NotChosen, standing.State);
        Assert.False(standing.Ready);
        Assert.Equal("not chosen", standing.Name);
    }

    [Fact]
    public void AChosenMilestoneThatIsOpenIsFound()
    {
        var standing = MilestoneStanding.Of(new MilestoneChoice("v0.3.0"), Open);

        Assert.Equal(MilestoneState.Chosen, standing.State);
        Assert.Equal(Open[1], standing.Milestone);
        Assert.True(standing.Ready);
        Assert.Equal("v0.3.0", standing.Name);
    }

    [Fact]
    public void WorkingWithoutAMilestoneNeedsNoMilestones()
    {
        var standing = MilestoneStanding.Of(MilestoneChoice.None, null);

        Assert.Equal(MilestoneState.NoMilestone, standing.State);
        Assert.True(standing.Ready);
        Assert.Equal("no milestone", standing.Name);
    }

    [Fact]
    public void AMilestoneThatIsNoLongerOpenIsGone()
    {
        var standing = MilestoneStanding.Of(new MilestoneChoice("v0.1.0"), Open);

        Assert.Equal(MilestoneState.Gone, standing.State);
        Assert.False(standing.Ready);
        Assert.Equal("v0.1.0", standing.Name);
    }

    [Fact]
    public void AChoiceStandsWhenTheMilestonesCannotBeRead()
    {
        var standing = MilestoneStanding.Of(new MilestoneChoice("v0.1.0"), null);

        Assert.Equal(MilestoneState.Unverified, standing.State);
        Assert.True(standing.Ready);
    }

    [Fact]
    public void ATitleIsFoundWhateverTheCaseOfItsLettersUnlessTwoDifferOnlyInIt()
    {
        Assert.Equal(Open[0], MilestoneStanding.Find(Open, "V0.2.0"));
        Assert.Null(MilestoneStanding.Find([new("Next", null, 0), new("next", null, 0)], "NEXT"));
        Assert.Equal("next", MilestoneStanding.Find([new("Next", null, 0), new("next", null, 0)], "next")?.Title);
    }
}
