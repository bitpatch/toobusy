using TooBusy.Core.Queue;
using TooBusy.Core.Run;

namespace TooBusy.Core.Tests;

public class OutcomeTests
{
    [Theory]
    [InlineData("done", OutcomeKind.Done)]
    [InlineData("partial", OutcomeKind.Partial)]
    [InlineData("owner", OutcomeKind.Owner)]
    [InlineData("failed", OutcomeKind.Failed)]
    [InlineData("interrupted", OutcomeKind.Interrupted)]
    public void TheLastLineForToobusySaysHowTheTaskWent(string word, OutcomeKind kind)
    {
        var outcome = Outcome.Read($"The export is written.\n\nChecked with the tests.\nTOOBUSY: {word}\n");

        Assert.Equal(kind, outcome!.Kind);
        Assert.Equal("The export is written.\n\nChecked with the tests.", outcome.Report);
        Assert.Null(outcome.Reason);
        Assert.Null(outcome.Rest);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("Shall I go on?")]
    [InlineData("TOOBUSY: finished")]
    [InlineData("TOOBUSY:")]
    [InlineData("I will end with TOOBUSY later.")]
    public void AReplyWithoutSuchALineSaysNothing(string? reply)
    {
        Assert.Null(Outcome.Read(reply));
    }

    [Fact]
    public void AFailureGivesItsReason()
    {
        var outcome = Outcome.Read("Nothing helps.\nTOOBUSY: failed the tests do not pass: 3 of them");

        Assert.Equal(OutcomeKind.Failed, outcome!.Kind);
        Assert.Equal("the tests do not pass: 3 of them", outcome.Reason);
    }

    [Theory]
    [InlineData("**TOOBUSY: done**")]
    [InlineData("  `TOOBUSY: done`  \r")]
    [InlineData("> toobusy: Done.")]
    public void TheLineIsReadWhateverStandsAroundIt(string line)
    {
        Assert.Equal(OutcomeKind.Done, Outcome.Read($"Report.\n{line}\n")!.Kind);
    }

    [Fact]
    public void TheLastOfSeveralLinesCounts()
    {
        var outcome = Outcome.Read("I will write `TOOBUSY: failed` if it breaks.\nTOOBUSY: failed first try\nIt works now.\nTOOBUSY: done");

        Assert.Equal(OutcomeKind.Done, outcome!.Kind);
        Assert.Contains("It works now.", outcome.Report, StringComparison.Ordinal);
    }

    [Fact]
    public void ATaskDoneInPartNamesWhatIsLeft()
    {
        var outcome = Outcome.Read("""
            The export is written.

            TOOBUSY-REST: Choose the format of the dates
            The dates are left as they were.

            - [ ] ISO or the format of the country?
            TOOBUSY: partial
            """);

        Assert.Equal(OutcomeKind.Partial, outcome!.Kind);
        Assert.Equal("The export is written.", outcome.Report);
        Assert.Equal(new RestTask("Choose the format of the dates", "The dates are left as they were.\n\n- [ ] ISO or the format of the country?"), outcome.Rest);
    }

    [Fact]
    public void ATaskDoneInPartMayLeaveTheRestUnnamed()
    {
        var outcome = Outcome.Read("Half of it.\nTOOBUSY: partial");

        Assert.Equal(OutcomeKind.Partial, outcome!.Kind);
        Assert.Equal("Half of it.", outcome.Report);
        Assert.Null(outcome.Rest);
    }

    [Fact]
    public void WhatIsLeftBelongsOnlyToATaskDoneInPart()
    {
        var outcome = Outcome.Read("Report.\nTOOBUSY-REST: Something\nMore.\nTOOBUSY: done");

        Assert.Null(outcome!.Rest);
        Assert.Contains("TOOBUSY-REST: Something", outcome.Report, StringComparison.Ordinal);
    }

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
}
