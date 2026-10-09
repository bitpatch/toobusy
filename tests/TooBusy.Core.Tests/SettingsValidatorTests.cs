using TooBusy.Core.Settings;

namespace TooBusy.Core.Tests;

public class SettingsValidatorTests
{
    [Fact]
    public void ACompleteDocumentGivesTheSettings()
    {
        var result = SettingsValidator.Validate(Document(
            (SettingsKeys.TrackerBoard, "https://github.com/orgs/bitpatch/projects/3"),
            (SettingsKeys.BlockingLabels, Labels("manual", "draft")),
            (SettingsKeys.TakeLabels, Labels("feature", "bug"))));

        Assert.Empty(result.Errors);
        Assert.Equal(new TrackerSettings("github", "bitpatch/toobusy", "https://github.com/orgs/bitpatch/projects/3"), result.Settings!.Tracker);
        Assert.Equal(new MilestoneSettings(MilestoneRule.LowestVersion, null), result.Settings.Queue.Milestone);
        Assert.Equal(["manual", "draft"], result.Settings.Queue.Labels.Blocking);
        Assert.Equal(["feature", "bug"], result.Settings.Queue.Labels.Take);
        Assert.Equal(new AssistantSettings("claude-code"), result.Settings.Assistant);
    }

    [Fact]
    public void TheBoardIsOptionalAndTheLabelListsMayBeEmpty()
    {
        var result = SettingsValidator.Validate(Document());

        Assert.Empty(result.Errors);
        Assert.Null(result.Settings!.Tracker.Board);
        Assert.Empty(result.Settings.Queue.Labels.Blocking);
        Assert.Empty(result.Settings.Queue.Labels.Take);
    }

    [Theory]
    [InlineData(SettingsKeys.Version)]
    [InlineData(SettingsKeys.TrackerType)]
    [InlineData(SettingsKeys.TrackerRepository)]
    [InlineData(SettingsKeys.MilestoneRule)]
    [InlineData(SettingsKeys.BlockingLabels)]
    [InlineData(SettingsKeys.TakeLabels)]
    [InlineData(SettingsKeys.AssistantType)]
    public void ARequiredKeyMustBeThere(string key)
    {
        var error = OnlyError(Document((key, null)));

        Assert.Equal(key, error.Key);
        Assert.Contains("missing", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void AMissingKeyGetsTheLineOfItsSection()
    {
        var withSection = Document((SettingsKeys.TrackerRepository, null));
        var withoutSection = withSection with { Sections = [] };

        Assert.Equal(LineOf("tracker"), OnlyError(withSection).Line);
        Assert.Null(OnlyError(withoutSection).Line);
    }

    [Theory]
    [InlineData(SettingsKeys.Version, "1")]
    [InlineData(SettingsKeys.TrackerType, 1L)]
    [InlineData(SettingsKeys.TrackerRepository, true)]
    [InlineData(SettingsKeys.TrackerBoard, 3L)]
    [InlineData(SettingsKeys.MilestoneRule, false)]
    [InlineData(SettingsKeys.MilestoneTitle, 2L)]
    [InlineData(SettingsKeys.BlockingLabels, "manual")]
    [InlineData(SettingsKeys.TakeLabels, 1L)]
    [InlineData(SettingsKeys.AssistantType, 0L)]
    public void AValueOfTheWrongTypeIsRefused(string key, object value)
    {
        var error = OnlyError(Document((key, value)));

        Assert.Equal(key, error.Key);
        Assert.Equal(LineOf(key), error.Line);
        Assert.StartsWith("must be a ", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void AValueOfAKindTheSettingsNeverUseIsRefused()
    {
        var error = OnlyError(Document((SettingsKeys.TrackerRepository, SettingsEntry.Other)));

        Assert.Equal(SettingsKeys.TrackerRepository, error.Key);
    }

    [Fact]
    public void AListWithSomethingButNamesIsRefused()
    {
        var error = OnlyError(Document((SettingsKeys.TakeLabels, new object[] { "bug", 1L })));

        Assert.Equal(SettingsKeys.TakeLabels, error.Key);
        Assert.Equal("must be a list of label names", error.Message);
    }

    [Fact]
    public void AnEmptyLabelNameIsRefused()
    {
        var error = OnlyError(Document((SettingsKeys.BlockingLabels, Labels("manual", " "))));

        Assert.Equal(SettingsKeys.BlockingLabels, error.Key);
    }

    [Fact]
    public void AnUnknownKeyIsRefusedWithItsLine()
    {
        var document = Document();
        document = document with { Entries = [.. document.Entries, new SettingsEntry("tracker.repo", "bitpatch/toobusy", 40)] };

        Assert.Equal(new SettingsError("tracker.repo", 40, "unknown key"), OnlyError(document));
    }

    [Fact]
    public void AnUnknownSectionIsRefusedOnceWithItsKeys()
    {
        var document = Document();
        document = document with
        {
            Entries = [.. document.Entries, new SettingsEntry("prompts.task", "Do it.", 41)],
            Sections = [.. document.Sections, new SettingsSection("prompts", 40)],
        };

        Assert.Equal(new SettingsError("prompts", 40, "unknown section"), OnlyError(document));
    }

    [Fact]
    public void AKeySetTwiceIsRefused()
    {
        var document = Document();
        document = document with { Entries = [.. document.Entries, new SettingsEntry(SettingsKeys.AssistantType, "claude-code", 40)] };

        Assert.Equal(new SettingsError(SettingsKeys.AssistantType, 40, "is set twice"), OnlyError(document));
    }

    [Fact]
    public void AHigherVersionWasMadeByANewerToobusy()
    {
        var error = OnlyError(Document((SettingsKeys.Version, 2L)));

        Assert.Equal(SettingsKeys.Version, error.Key);
        Assert.Contains("made by a newer toobusy", error.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(0L)]
    [InlineData(-1L)]
    public void ALowerVersionIsRefused(long version)
    {
        var error = OnlyError(Document((SettingsKeys.Version, version)));

        Assert.Equal("must be 1", error.Message);
    }

    [Fact]
    public void TheTrackerMustBeGitHub()
    {
        var error = OnlyError(Document((SettingsKeys.TrackerType, "jira")));

        Assert.Equal(new SettingsError(SettingsKeys.TrackerType, LineOf(SettingsKeys.TrackerType), "must be `github`"), error);
    }

    [Fact]
    public void TheAssistantMustBeClaudeCode()
    {
        var error = OnlyError(Document((SettingsKeys.AssistantType, "codex")));

        Assert.Equal(new SettingsError(SettingsKeys.AssistantType, LineOf(SettingsKeys.AssistantType), "must be `claude-code`"), error);
    }

    [Theory]
    [InlineData("bitpatch/toobusy")]
    [InlineData("denis-kondratev/my.repo_1")]
    public void ARepositoryIsOwnerAndName(string repository)
    {
        Assert.Empty(SettingsValidator.Validate(Document((SettingsKeys.TrackerRepository, repository))).Errors);
    }

    [Theory]
    [InlineData("")]
    [InlineData("toobusy")]
    [InlineData("bitpatch/")]
    [InlineData("/toobusy")]
    [InlineData("bitpatch/toobusy/issues")]
    [InlineData("bitpatch / toobusy")]
    [InlineData("https://github.com/bitpatch/toobusy")]
    public void AnythingElseIsNotARepository(string repository)
    {
        var error = OnlyError(Document((SettingsKeys.TrackerRepository, repository)));

        Assert.Equal(SettingsKeys.TrackerRepository, error.Key);
        Assert.Contains("owner/name", error.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("https://github.com/orgs/bitpatch/projects/3")]
    [InlineData("https://github.com/users/denis-kondratev/projects/12")]
    public void ABoardIsAProjectOfAnOrganisationOrAUser(string board)
    {
        Assert.Empty(SettingsValidator.Validate(Document((SettingsKeys.TrackerBoard, board))).Errors);
    }

    [Theory]
    [InlineData("")]
    [InlineData("3")]
    [InlineData("http://github.com/orgs/bitpatch/projects/3")]
    [InlineData("https://github.com/orgs/bitpatch/projects/")]
    [InlineData("https://github.com/orgs/bitpatch/projects/three")]
    [InlineData("https://github.com/bitpatch/toobusy/projects/3")]
    [InlineData("https://github.com/orgs/bitpatch/projects/3/views/1")]
    [InlineData("https://example.com/orgs/bitpatch/projects/3")]
    public void AnythingElseIsNotABoard(string board)
    {
        var error = OnlyError(Document((SettingsKeys.TrackerBoard, board)));

        Assert.Equal(SettingsKeys.TrackerBoard, error.Key);
    }

    [Theory]
    [InlineData("lowest-version", MilestoneRule.LowestVersion)]
    [InlineData("earliest-due", MilestoneRule.EarliestDue)]
    [InlineData("none", MilestoneRule.None)]
    public void AMilestoneRuleIsReadByItsName(string name, MilestoneRule rule)
    {
        var result = SettingsValidator.Validate(Document((SettingsKeys.MilestoneRule, name)));

        Assert.Equal(new MilestoneSettings(rule, null), result.Settings!.Queue.Milestone);
    }

    [Fact]
    public void AnUnknownMilestoneRuleIsRefused()
    {
        var error = OnlyError(Document((SettingsKeys.MilestoneRule, "latest")));

        Assert.Equal(SettingsKeys.MilestoneRule, error.Key);
        Assert.Contains("`lowest-version`, `earliest-due`, `fixed`, `none`", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void TheFixedRuleComesWithItsTitle()
    {
        var result = SettingsValidator.Validate(Document((SettingsKeys.MilestoneRule, "fixed"), (SettingsKeys.MilestoneTitle, "v.0.2.0")));

        Assert.Equal(new MilestoneSettings(MilestoneRule.Fixed, "v.0.2.0"), result.Settings!.Queue.Milestone);
    }

    [Fact]
    public void TheFixedRuleNeedsATitle()
    {
        var error = OnlyError(Document((SettingsKeys.MilestoneRule, "fixed")));

        Assert.Equal(SettingsKeys.MilestoneTitle, error.Key);
        Assert.Equal(LineOf("queue.milestone"), error.Line);
    }

    [Fact]
    public void TheTitleMustNotBeEmpty()
    {
        var error = OnlyError(Document((SettingsKeys.MilestoneRule, "fixed"), (SettingsKeys.MilestoneTitle, "")));

        Assert.Equal(new SettingsError(SettingsKeys.MilestoneTitle, LineOf(SettingsKeys.MilestoneTitle), "must not be empty"), error);
    }

    [Theory]
    [InlineData("lowest-version")]
    [InlineData("earliest-due")]
    [InlineData("none")]
    public void ATitleIsRefusedWithAnyOtherRule(string rule)
    {
        var error = OnlyError(Document((SettingsKeys.MilestoneRule, rule), (SettingsKeys.MilestoneTitle, "v.0.2.0")));

        Assert.Equal(SettingsKeys.MilestoneTitle, error.Key);
        Assert.Contains("`fixed` rule only", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ALabelMustNotBothBlockAndTake()
    {
        var error = OnlyError(Document(
            (SettingsKeys.BlockingLabels, Labels("manual", "draft")),
            (SettingsKeys.TakeLabels, Labels("bug", "Draft"))));

        Assert.Equal(SettingsKeys.TakeLabels, error.Key);
        Assert.Contains("`Draft`", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void EveryErrorIsReportedInTheOrderOfTheLines()
    {
        var result = SettingsValidator.Validate(Document(
            (SettingsKeys.AssistantType, "codex"),
            (SettingsKeys.TrackerRepository, "toobusy"),
            (SettingsKeys.Version, 2L)));

        Assert.Null(result.Settings);
        Assert.Equal([SettingsKeys.Version, SettingsKeys.TrackerRepository, SettingsKeys.AssistantType], result.Errors.Select(error => error.Key));
    }

    [Fact]
    public void AnErrorNamesTheFileTheLineAndTheKey()
    {
        Assert.Equal(".toobusy/settings.toml:5: tracker.repository: is missing", new SettingsError("tracker.repository", 5, "is missing").Describe(".toobusy/settings.toml"));
        Assert.Equal(".toobusy/settings.toml: tracker.repository: is missing", new SettingsError("tracker.repository", null, "is missing").Describe(".toobusy/settings.toml"));
        Assert.Equal(".toobusy/settings.toml:2: broken", new SettingsError(null, 2, "broken").Describe(".toobusy/settings.toml"));
    }

    static readonly (string Key, object Value)[] Valid =
    [
        (SettingsKeys.Version, 1L),
        ("tracker", ""),
        (SettingsKeys.TrackerType, "github"),
        (SettingsKeys.TrackerRepository, "bitpatch/toobusy"),
        ("queue.milestone", ""),
        (SettingsKeys.MilestoneRule, "lowest-version"),
        ("queue.labels", ""),
        (SettingsKeys.BlockingLabels, Array.Empty<object>()),
        (SettingsKeys.TakeLabels, Array.Empty<object>()),
        ("assistant", ""),
        (SettingsKeys.AssistantType, "claude-code"),
    ];

    static readonly string[] Order = [.. Valid.Select(pair => pair.Key), SettingsKeys.TrackerBoard, SettingsKeys.MilestoneTitle];

    // A valid document with the given keys changed, added, or left out when the value is null.
    // Every key and section has a line of its own, the same in every document.
    static SettingsDocument Document(params (string Key, object? Value)[] changes)
    {
        var values = Valid.ToDictionary(pair => pair.Key, pair => (object?)pair.Value);
        foreach (var (key, value) in changes)
            values[key] = value;

        var sections = values.Keys.Where(key => !SettingsKeys.All.Contains(key)).Select(key => new SettingsSection(key, LineOf(key)));
        var entries = values
            .Where(pair => SettingsKeys.All.Contains(pair.Key) && pair.Value is not null)
            .Select(pair => new SettingsEntry(pair.Key, pair.Value!, LineOf(pair.Key)));
        return new SettingsDocument([.. entries], [.. sections]);
    }

    static int LineOf(string key) => Array.IndexOf(Order, key) + 1;

    static object[] Labels(params string[] names) => [.. names];

    static SettingsError OnlyError(SettingsDocument document)
    {
        var result = SettingsValidator.Validate(document);
        Assert.Null(result.Settings);
        return Assert.Single(result.Errors);
    }
}
