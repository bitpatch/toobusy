using TooBusy.Core.Processes;

namespace TooBusy.Core.Tests;

public sealed class TurnsTests
{
    [Fact]
    public async Task ThoseWhoShareTakeTurnsInTheOrderTheyCame()
    {
        var turns = new Turns();
        var first = new TaskCompletionSource<int>();
        var order = new List<int>();

        var one = turns.TakeAsync(async () => order.Add(await first.Task), TestContext.Current.CancellationToken);
        var other = turns.TakeAsync(() => { order.Add(2); return Task.FromResult(2); }, TestContext.Current.CancellationToken);

        Assert.Empty(order);
        first.SetResult(1);
        await Task.WhenAll(one, other);
        Assert.Equal([1, 2], order);
    }

    [Fact]
    public async Task OneThatFailedGivesItsTurnUp()
    {
        var turns = new Turns();

        await Assert.ThrowsAsync<InvalidOperationException>(() => turns.TakeAsync<int>(() => throw new InvalidOperationException(), TestContext.Current.CancellationToken));

        Assert.Equal(2, await turns.TakeAsync(() => Task.FromResult(2), TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task OneThatIsCancelledWhileItWaitsDoesNothing()
    {
        var turns = new Turns();
        var first = new TaskCompletionSource<int>();
        using var stop = new CancellationTokenSource();
        var one = turns.TakeAsync(() => first.Task, TestContext.Current.CancellationToken);

        var waiting = turns.TakeAsync(() => Task.FromResult(2), stop.Token);
        await stop.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => waiting);
        first.SetResult(1);
        Assert.Equal(1, await one);
    }
}
