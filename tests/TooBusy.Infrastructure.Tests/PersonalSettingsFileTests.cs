using TooBusy.Core.Assistant;
using TooBusy.Core.Queue;
using TooBusy.Infrastructure.Settings;

namespace TooBusy.Infrastructure.Tests;

public sealed class PersonalSettingsFileTests : IDisposable
{
    readonly DirectoryInfo folder = Directory.CreateTempSubdirectory("toobusy-personal-");

    public void Dispose() => folder.Delete(recursive: true);

    [Fact]
    public void WithoutAFileNothingIsChosen()
    {
        Assert.Null(Of("/work/rocket").LoadMilestone());
        Assert.Null(Of("/work/rocket").LoadModel());
        Assert.Null(Of("/work/rocket").LoadEffort());
    }

    // A file of a toobusy that still kept a share of the weekly limit, `limit`.
    [Fact]
    public void AChoiceThatIsNoLongerKeptIsLeftOutWhenTheFileIsWritten()
    {
        File.WriteAllText(Of("/work/rocket").Path, "[\"/work/rocket\"]\nmilestone = \"v1\"\nlimit = 80\n");

        Assert.Equal("v1", Of("/work/rocket").LoadMilestone()?.Title);
        Of("/work/rocket").SaveEffort("high");
        Assert.EndsWith("[\"/work/rocket\"]\nmilestone = \"v1\"\neffort = \"high\"\n", File.ReadAllText(Of("/work/rocket").Path), StringComparison.Ordinal);
    }

    [Fact]
    public void AChoiceIsReadBackByAnotherRun()
    {
        Of("/work/rocket").SaveMilestone(new MilestoneChoice("v0.3.0 \"moon\""));

        Assert.Equal(new MilestoneChoice("v0.3.0 \"moon\""), Of("/work/rocket").LoadMilestone());
    }

    [Fact]
    public void WorkingWithoutAMilestoneIsAChoiceToo()
    {
        Of("/work/rocket").SaveMilestone(MilestoneChoice.None);

        Assert.Equal(MilestoneChoice.None, Of("/work/rocket").LoadMilestone());
    }

    [Fact]
    public void EveryProjectHasItsOwnChoice()
    {
        Of("/work/rocket").SaveMilestone(new MilestoneChoice("v1"));
        Of(@"C:\work\moon.base").SaveMilestone(new MilestoneChoice("v2"));
        Of("/work/rocket").SaveMilestone(new MilestoneChoice("v3"));

        Assert.Equal("v3", Of("/work/rocket").LoadMilestone()?.Title);
        Assert.Equal("v2", Of(@"C:\work\moon.base").LoadMilestone()?.Title);
        Assert.Null(Of("/work").LoadMilestone());
    }

    [Fact]
    public void TheFileIsTomlThatAPersonCanRead()
    {
        Of("/work/rocket").SaveEffort("high");
        Of("/work/rocket").SaveModel(new ModelChoice("opus"));
        Of("/work/rocket").SaveMilestone(new MilestoneChoice("v0.3.0"));

        Assert.Equal(
            """
            # What you chose for each project you run toobusy in. toobusy writes this file.
            # An empty milestone means working without one; an empty model means the assistant's own.

            ["/work/rocket"]
            milestone = "v0.3.0"
            model = "opus"
            effort = "high"

            """.ReplaceLineEndings("\n"),
            File.ReadAllText(Of("/work/rocket").Path));
    }

    [Fact]
    public void AModelAndAnEffortAreReadBackByAnotherRun()
    {
        Of("/work/rocket").SaveModel(new ModelChoice("claude-opus-4"));
        Of("/work/rocket").SaveEffort("xhigh");

        Assert.Equal(new ModelChoice("claude-opus-4"), Of("/work/rocket").LoadModel());
        Assert.Equal("xhigh", Of("/work/rocket").LoadEffort());
    }

    [Fact]
    public void TheAssistantsOwnModelIsAChoiceToo()
    {
        Of("/work/rocket").SaveModel(ModelChoice.AssistantsOwn);

        Assert.Equal(ModelChoice.AssistantsOwn, Of("/work/rocket").LoadModel());
        Assert.Contains("model = \"\"", File.ReadAllText(Of("/work/rocket").Path), StringComparison.Ordinal);
    }

    [Fact]
    public void SavingOneChoiceKeepsTheOthers()
    {
        Of("/work/rocket").SaveMilestone(new MilestoneChoice("v1"));
        Of("/work/rocket").SaveModel(new ModelChoice("sonnet"));
        Of("/work/rocket").SaveEffort("low");
        Of("/work/rocket").SaveMilestone(MilestoneChoice.None);

        Assert.Equal(MilestoneChoice.None, Of("/work/rocket").LoadMilestone());
        Assert.Equal("sonnet", Of("/work/rocket").LoadModel()?.Name);
        Assert.Equal("low", Of("/work/rocket").LoadEffort());
    }

    [Fact]
    public void AFileWithOnlyAMilestoneSaysNothingOfTheModelAndTheEffort()
    {
        File.WriteAllText(Of("/work/rocket").Path, "[\"/work/rocket\"]\nmilestone = \"v1\"\n");

        Assert.Null(Of("/work/rocket").LoadModel());
        Assert.Null(Of("/work/rocket").LoadEffort());
        Of("/work/rocket").SaveEffort("max");
        Assert.Equal("v1", Of("/work/rocket").LoadMilestone()?.Title);
    }

    [Fact]
    public void AProjectWithoutAMilestoneKeepsItsModel()
    {
        Of("/work/rocket").SaveModel(new ModelChoice("haiku"));
        Of("/work/moon").SaveMilestone(new MilestoneChoice("v1"));

        Assert.Equal("haiku", Of("/work/rocket").LoadModel()?.Name);
        Assert.Null(Of("/work/rocket").LoadMilestone());
    }

    [Theory]
    [InlineData("effort = \"ultra\"")]
    [InlineData("effort = 3")]
    public void AnEffortThatIsNotALevelCountsAsNotChosen(string line)
    {
        File.WriteAllText(Of("/work/rocket").Path, $"[\"/work/rocket\"]\nmilestone = \"v1\"\n{line}\n");

        Assert.Null(Of("/work/rocket").LoadEffort());
        Assert.Equal("v1", Of("/work/rocket").LoadMilestone()?.Title);
    }

    [Fact]
    public void TheFolderIsMadeWhenItIsMissing()
    {
        var deep = Path.Combine(folder.FullName, "settings", "toobusy");

        new PersonalSettingsFile(deep, "/work/rocket").SaveMilestone(MilestoneChoice.None);

        Assert.True(File.Exists(Path.Combine(deep, "projects.toml")));
    }

    [Fact]
    public void AFileThatIsNotTomlSaysNothingAndIsReplaced()
    {
        File.WriteAllText(Of("/work/rocket").Path, "[\"/work/rocket\nmilestone =");

        Assert.Null(Of("/work/rocket").LoadMilestone());
        Of("/work/rocket").SaveMilestone(new MilestoneChoice("v1"));
        Assert.Equal("v1", Of("/work/rocket").LoadMilestone()?.Title);
    }

    [Theory]
    [InlineData("/settings", null, false, "/settings")]
    [InlineData(null, null, false, "/home/ann/.config")]
    [InlineData("", @"C:\Users\ann\AppData\Roaming", true, @"C:\Users\ann\AppData\Roaming")]
    public void TheFolderFollowsTheCustomOfThePlatform(string? given, string? roaming, bool windows, string settings)
    {
        var found = PersonalSettingsFile.FindFolder(name => name == "XDG_CONFIG_HOME" ? given : name == "APPDATA" ? roaming : null, "/home/ann", windows);

        Assert.Equal(Path.Combine(settings.Replace("/home/ann/.config", Path.Combine("/home/ann", ".config"), StringComparison.Ordinal), "toobusy"), found);
    }

    [Fact]
    public void WithoutAHomeThereIsNoFolder()
    {
        Assert.Null(PersonalSettingsFile.FindFolder(_ => null, "", windows: false));
    }

    PersonalSettingsFile Of(string root) => new(folder.FullName, root);
}
