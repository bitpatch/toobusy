using TooBusy.Core.Run;

namespace TooBusy.Core.Tests;

public class UsageLimitsTests
{
    static readonly DateTimeOffset Now = new(2030, 1, 1, 9, 0, 0, TimeSpan.Zero);

    [Fact]
    public void AWindowWhoseResetIsStillToComeStaysAsItWasToldOf()
    {
        var near = new UsageWindow("5-hour", 41, Now + TimeSpan.FromMinutes(1));
        var far = new UsageWindow("weekly", 12, Now + TimeSpan.FromDays(3));

        Assert.Equal(new UsageLimits(near, far), new UsageLimits(near, far).At(Now));
    }

    [Fact]
    public void AWindowWhoseResetHasPassedIsNotKnown()
    {
        var limits = new UsageLimits(new UsageWindow("5-hour", 11, Now - TimeSpan.FromHours(3)), new UsageWindow("weekly", 100, Now));

        Assert.Equal(UsageLimits.Unknown, limits.At(Now));
    }

    [Fact]
    public void OnlyTheWindowWhoseResetHasPassedIsDropped()
    {
        var far = new UsageWindow("weekly", 4, Now + TimeSpan.FromDays(2));

        var limits = new UsageLimits(new UsageWindow("5-hour", 11, Now - TimeSpan.FromMinutes(1)), far).At(Now);

        Assert.Equal(new UsageLimits(null, far), limits);
    }

    [Fact]
    public void AWindowWithoutAResetTimeStays()
    {
        var limits = new UsageLimits(new UsageWindow("5-hour", 97, null), null);

        Assert.Equal(limits, limits.At(Now));
    }
}
