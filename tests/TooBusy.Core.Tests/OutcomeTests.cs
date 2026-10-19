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

    [Theory]
    [InlineData("Half of it; the rest is #34.\nTOOBUSY: partial", true)]
    [InlineData("Half of it; the rest is #34.\n\n**TOOBUSY: partial**\n\n```\n", true)]
    [InlineData("I will write `TOOBUSY: failed` if it breaks.\nTOOBUSY: done\n", true)]
    [InlineData("Half of it; the rest is #34.\nTOOBUSY: partial\n\nShall I take #34 too?", false)]
    [InlineData("TOOBUSY: done\nTOOBUSY: maybe", false)]
    [InlineData("Shall I go on?", false)]
    [InlineData(null, false)]
    public void AReplyEndsWithTheLineWhenNothingButEmptyLinesStandsAfterIt(string? reply, bool ends)
    {
        Assert.Equal(ends, Outcome.Ends(reply));
    }

    [Fact]
    public void AReplyIsReadWithoutABlockOfWhatIsLeft()
    {
        var outcome = Outcome.Read("The export is written.\n\nTOOBUSY-REST: Choose the format of the dates\nThe dates are left as they were.\nTOOBUSY: partial");

        Assert.Equal(OutcomeKind.Partial, outcome!.Kind);
        Assert.Equal("The export is written.\n\nTOOBUSY-REST: Choose the format of the dates\nThe dates are left as they were.", outcome.Report);
    }

    [Theory]
    [InlineData("What is left is #34.", true)]
    [InlineData("Made https://github.com/acme/rocket/issues/34 of what is left.", true)]
    [InlineData("Left: (#34), #12 is done.", true)]
    [InlineData("Half of #12 is done.", false)]
    [InlineData("See https://github.com/acme/rocket/issues/12.", false)]
    [InlineData("The rest is task # 34, or C# 13.", false)]
    [InlineData("Done up to step #2 of 5.", true)]
    [InlineData("", false)]
    public void AReportNamesATaskOtherThanItsOwnByItsNumberOrItsAddress(string report, bool names)
    {
        var outcome = Outcome.Read($"{report}\nTOOBUSY: partial");

        Assert.Equal(names, outcome!.Names(12));
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
