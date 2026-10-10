using TooBusy.Core.Queue;

namespace TooBusy.Core.Tests;

public class MilestoneOrderTests
{
    [Fact]
    public void VersionsComeFirstTheLowestFirstAndTheRestByTitle()
    {
        Milestone[] open = [Of("Polish"), Of("v.0.10.0"), Of("backlog"), Of("v.0.2.0"), Of("Release 1.0")];

        Assert.Equal(["v.0.2.0", "v.0.10.0", "Release 1.0", "backlog", "Polish"], MilestoneOrder.Sorted(open).Select(milestone => milestone.Title));
    }

    [Theory]
    [InlineData("v.0.2.0", 0L, 2L, 0L)]
    [InlineData("v1.4", 1L, 4L)]
    [InlineData("Release 2.0 (spring 2030)", 2L, 0L)]
    [InlineData("Sprint 7", 7L)]
    public void TheVersionIsTheFirstRunOfDotSeparatedNumbers(string title, params long[] version)
    {
        Assert.Equal(version, MilestoneOrder.VersionOf(title));
    }

    [Fact]
    public void ATitleWithoutNumbersHasNoVersion()
    {
        Assert.Null(MilestoneOrder.VersionOf("Backlog"));
    }

    [Fact]
    public void AShorterVersionCountsZerosForTheRest()
    {
        Assert.Equal(["v1", "v1.0.1", "v1.1"], MilestoneOrder.Sorted([Of("v1.1"), Of("v1.0.1"), Of("v1")]).Select(milestone => milestone.Title));
    }

    static Milestone Of(string title) => new(title, null, 0);
}
