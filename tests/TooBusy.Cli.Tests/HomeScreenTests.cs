using TooBusy.Cli.Terminal;

namespace TooBusy.Cli.Tests;

public sealed class HomeScreenTests : IDisposable
{
    readonly TestTerminal terminal = new(width: 80, height: 20);

    public void Dispose() => terminal.Dispose();

    [Fact]
    public async Task WhileTheTasksAreCountedTheDotsRunAtRunAndEnterDoesNothingThere()
    {
        terminal.Device = terminal.Device with { KeyWaiting = () => terminal.Keys.Waiting };
        var counted = new TaskCompletionSource<int?>();

        // Enter while the count goes on; then the count is done, and Enter opens the run.
        terminal.Keys.Press(Keys.Enter).Hold(() =>
        {
            counted.TrySetResult(5);
            return terminal.Saw("5 tasks");
        }).Press(Keys.Enter);

        Assert.Equal(HomeAction.Run, await AskAsync(new TaskCount(() => counted.Task)));
        terminal.AssertSaw("""
             What to do
             ❯ Run        •••••••
               Assistant  own model · high
               Milestone  v0.3.0
               Settings
               Exit
             ↑↓ move · enter choose · esc exit · ctrl+c exit
            """.ReplaceLineEndings("\n").TrimEnd(' '));
        terminal.AssertSaw(" ❯ Run        5 tasks\n   Assistant  own model · high");
    }

    [Fact]
    public async Task ARunThatWouldTakeNoTaskIsNotOpenedAndEnterCountsAgain()
    {
        var counts = new Queue<int?>([0, 1]);
        terminal.Keys.Press(Keys.Enter, Keys.Enter);

        Assert.Equal(HomeAction.Run, await AskAsync(new TaskCount(() => Task.FromResult(counts.Dequeue()))));
        terminal.AssertSaw(" ❯ Run        no tasks\n");
        terminal.AssertSaw(" ❯ Run        1 task\n");
        Assert.Empty(counts);
    }

    [Fact]
    public async Task WhenTheTasksCannotBeReadRunSaysSoAndIsOpenedAllTheSame()
    {
        terminal.Keys.Press(Keys.Enter);

        Assert.Equal(HomeAction.Run, await AskAsync(new TaskCount(() => Task.FromResult<int?>(null))));
        terminal.AssertSaw(" ❯ Run        the tasks cannot be read\n");
    }

    [Fact]
    public async Task TheRowThatWaitsDoesNotBlinkAndItsDotsRun()
    {
        terminal.Device = terminal.Device with { KeyWaiting = () => terminal.Keys.Waiting };
        var palette = Palette.Dark;
        var counted = new TaskCompletionSource<int?>();
        string? waiting = null;
        // What is written when the first frame is drawn is kept; then the count is done.
        terminal.Keys.Hold(() =>
        {
            if (terminal.Frames == 0)
                return false;

            waiting ??= terminal.Output.ToString();
            counted.TrySetResult(2);
            return terminal.Saw("2 tasks");
        }).Press(Keys.Enter);

        using var page = terminal.Open(palette: palette);
        await HomeScreen.AskAsync(page, "own model · high", "v0.3.0", new TaskCount(() => counted.Task));

        // The last thing written is the light on the dots: they stand after the edge, the pointer, the name as wide
        // as the widest, and two spaces. The row itself is muted, and nothing of it blinks.
        Assert.Matches(@"\u001b\[\d+;15H(\u001b\[[0-9;]*m•\u001b\[0m){7}$", waiting);
        Assert.Contains(palette.Paint(Tone.Muted, "❯ Run      "), waiting, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(1, HomeAction.Assistant)]
    [InlineData(2, HomeAction.Milestone)]
    [InlineData(3, HomeAction.Settings)]
    [InlineData(4, HomeAction.Exit)]
    public async Task TheOtherRowsAreChosenAsInAnyList(int down, HomeAction expected)
    {
        terminal.Keys.Press([.. Enumerable.Repeat(Keys.Down, down), Keys.Enter]);

        Assert.Equal(expected, await AskAsync(new TaskCount(() => Task.FromResult<int?>(3))));
    }

    [Fact]
    public async Task EscapeTwiceIsExit()
    {
        terminal.Keys.Press(Keys.Escape, Keys.Escape);

        Assert.Equal(HomeAction.Exit, await AskAsync(new TaskCount(() => Task.FromResult<int?>(3))));
        terminal.AssertSaw(" press esc again to exit");
    }

    async Task<HomeAction> AskAsync(TaskCount tasks)
    {
        using var page = terminal.Open();
        return await HomeScreen.AskAsync(page, "own model · high", "v0.3.0", tasks);
    }
}
