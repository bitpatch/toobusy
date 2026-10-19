using TooBusy.Core.Queue;
using TooBusy.Core.Run;

namespace TooBusy.Core.Tests;

// What a run says of a time, a count and a task, and of itself on its sign.
public class RunModelTests
{
    [Theory]
    [InlineData(0, "0 s")]
    [InlineData(45, "45 s")]
    [InlineData(90, "90 s")]
    [InlineData(120, "2 min")]
    [InlineData(725, "12 min")]
    [InlineData(3900, "1 h 05 min")]
    [InlineData(-5, "0 s")]
    public void ATimeIsSaidInTheUnitsThatMatter(int seconds, string said)
    {
        Assert.Equal(said, Spoken.Time(TimeSpan.FromSeconds(seconds)));
    }

    [Fact]
    public void CountsAndSharesAreSaidPlainly()
    {
        Assert.Equal("1 task", Spoken.Tasks(1));
        Assert.Equal("12 tasks", Spoken.Tasks(12));
        Assert.Equal("96%", Spoken.Percent(96.7));
    }

    [Fact]
    public void ATaskIsNamedByItsNumberAndItsTitle()
    {
        var task = new QueueTask(71, "Paint the windows yellow", "https://github.com/acme/rocket/issues/71", [], BoardStatus.Todo, "Todo", [], []);

        Assert.Equal("#71 Paint the windows yellow", Spoken.Task(task));
    }

    [Theory]
    [InlineData(RunPhase.Preparing, null, null, false, "between tasks")]
    [InlineData(RunPhase.WaitingForLimit, null, null, false, "it waits for a usage limit")]
    [InlineData(RunPhase.Preparing, 12, null, false, "#12 Export the orders as a file")]
    [InlineData(RunPhase.Working, 12, "claude attach 1a2b3c4d", false, "#12 Export the orders as a file · claude attach 1a2b3c4d")]
    [InlineData(RunPhase.WaitingForOwner, 12, "claude attach 1a2b3c4d", false, "#12 Export the orders as a file · its session waits for the owner · claude attach 1a2b3c4d")]
    [InlineData(RunPhase.WaitingForLimit, 12, "claude attach 1a2b3c4d", false, "#12 Export the orders as a file · waits for a usage limit · claude attach 1a2b3c4d")]
    [InlineData(RunPhase.WaitingForLimit, 12, "claude attach 1a2b3c4d", true, "#12 Export the orders as a file · waits for a usage limit until 2030-01-01 10:01 UTC · claude attach 1a2b3c4d")]
    public void TheSignOfARunSaysInAFewWordsWhatTheRunDoes(RunPhase phase, int? task, string? open, bool until, string told)
    {
        var said = new RunSign(phase)
        {
            Task = task,
            Title = task is null ? null : "Export the orders as a file",
            Open = open,
            Until = until ? FakeClock.Start + TimeSpan.FromMinutes(61) : null,
        };

        Assert.Equal(told, said.Told);
    }
}
