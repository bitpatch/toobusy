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
            (SettingsKeys.TakeLabels, Labels("feature", "bug")),
            (SettingsKeys.OwnerLabel, "manual"),
            (SettingsKeys.InterruptedLabel, "paused")));

        Assert.Empty(result.Errors);
        Assert.Equal(new TrackerSettings("github", "https://github.com/orgs/bitpatch/projects/3"), result.Settings!.Tracker);
        Assert.Equal(["manual", "draft"], result.Settings.Queue.Labels.Blocking);
        Assert.Equal(["feature", "bug"], result.Settings.Queue.Labels.Take);
        Assert.Equal("manual", result.Settings.Queue.Labels.Owner);
        Assert.Equal("paused", result.Settings.Queue.Labels.Interrupted);
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
    [InlineData(SettingsKeys.BlockingLabels)]
    [InlineData(SettingsKeys.TakeLabels)]
    [InlineData(SettingsKeys.OwnerLabel)]
    [InlineData(SettingsKeys.InterruptedLabel)]
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
        var withSection = Document((SettingsKeys.TrackerType, null));
        var withoutSection = withSection with { Sections = [] };

        Assert.Equal(LineOf("tracker"), OnlyError(withSection).Line);
        Assert.Null(OnlyError(withoutSection).Line);
    }

    [Theory]
    [InlineData(SettingsKeys.Version, "1")]
    [InlineData(SettingsKeys.TrackerType, 1L)]
    [InlineData(SettingsKeys.TrackerBoard, 3L)]
    [InlineData(SettingsKeys.BlockingLabels, "manual")]
    [InlineData(SettingsKeys.TakeLabels, 1L)]
    [InlineData(SettingsKeys.OwnerLabel, 1L)]
    [InlineData(SettingsKeys.InterruptedLabel, true)]
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
        var error = OnlyError(Document((SettingsKeys.TrackerBoard, SettingsEntry.Other)));

        Assert.Equal(SettingsKeys.TrackerBoard, error.Key);
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

    [Fact]
    public void ALabelMustNotBothBlockAndTake()
    {
        var error = OnlyError(Document(
            (SettingsKeys.BlockingLabels, Labels("manual", "draft")),
            (SettingsKeys.TakeLabels, Labels("bug", "Draft"))));

        Assert.Equal(SettingsKeys.TakeLabels, error.Key);
        Assert.Contains("`Draft`", error.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(SettingsKeys.OwnerLabel)]
    [InlineData(SettingsKeys.InterruptedLabel)]
    public void ALabelOfARunNeedsAName(string key)
    {
        var error = OnlyError(Document((key, " ")));

        Assert.Equal(new SettingsError(key, LineOf(key), "a label name must not be empty"), error);
    }

    [Theory]
    [InlineData(SettingsKeys.OwnerLabel)]
    [InlineData(SettingsKeys.InterruptedLabel)]
    public void ALabelOfARunIsNotALabelToTake(string key)
    {
        var error = OnlyError(Document((SettingsKeys.TakeLabels, Labels("bug", "feature")), (key, "Bug")));

        Assert.Equal(new SettingsError(key, LineOf(key), "the label `Bug` is in `queue.labels.take` too"), error);
    }

    [Fact]
    public void TheLabelOfTheOwnerMayBeABlockingOne()
    {
        var result = SettingsValidator.Validate(Document((SettingsKeys.BlockingLabels, Labels("manual")), (SettingsKeys.OwnerLabel, "manual")));

        Assert.Empty(result.Errors);
    }

    [Fact]
    public void TheLabelOfInterruptedTasksIsNotABlockingOne()
    {
        var error = OnlyError(Document((SettingsKeys.BlockingLabels, Labels("manual", "paused")), (SettingsKeys.InterruptedLabel, "paused")));

        Assert.Equal(new SettingsError(SettingsKeys.InterruptedLabel, LineOf(SettingsKeys.InterruptedLabel), "the label `paused` is in `queue.labels.blocking` too"), error);
    }

    [Fact]
    public void TheTwoLabelsOfARunDiffer()
    {
        var error = OnlyError(Document((SettingsKeys.OwnerLabel, "manual"), (SettingsKeys.InterruptedLabel, "Manual")));

        Assert.Equal(new SettingsError(SettingsKeys.InterruptedLabel, LineOf(SettingsKeys.InterruptedLabel), "must not be the label of `queue.labels.owner`"), error);
    }

    [Fact]
    public void EveryErrorIsReportedInTheOrderOfTheLines()
    {
        var result = SettingsValidator.Validate(Document(
            (SettingsKeys.AssistantType, "codex"),
            (SettingsKeys.TrackerBoard, "toobusy"),
            (SettingsKeys.Version, 2L)));

        Assert.Null(result.Settings);
        Assert.Equal([SettingsKeys.Version, SettingsKeys.AssistantType, SettingsKeys.TrackerBoard], result.Errors.Select(error => error.Key));
    }

    [Fact]
    public void AnErrorNamesTheFileTheLineAndTheKey()
    {
        Assert.Equal(".toobusy/settings.toml:5: tracker.type: is missing", new SettingsError("tracker.type", 5, "is missing").Describe(".toobusy/settings.toml"));
        Assert.Equal(".toobusy/settings.toml: tracker.type: is missing", new SettingsError("tracker.type", null, "is missing").Describe(".toobusy/settings.toml"));
        Assert.Equal(".toobusy/settings.toml:2: broken", new SettingsError(null, 2, "broken").Describe(".toobusy/settings.toml"));
    }

    static readonly (string Key, object Value)[] Valid =
    [
        (SettingsKeys.Version, 1L),
        ("tracker", ""),
        (SettingsKeys.TrackerType, "github"),
        ("queue.labels", ""),
        (SettingsKeys.BlockingLabels, Array.Empty<object>()),
        (SettingsKeys.TakeLabels, Array.Empty<object>()),
        (SettingsKeys.OwnerLabel, "needs-owner"),
        (SettingsKeys.InterruptedLabel, "interrupted"),
        ("assistant", ""),
        (SettingsKeys.AssistantType, "claude-code"),
    ];

    static readonly string[] Order = [.. Valid.Select(pair => pair.Key), SettingsKeys.TrackerBoard];

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
