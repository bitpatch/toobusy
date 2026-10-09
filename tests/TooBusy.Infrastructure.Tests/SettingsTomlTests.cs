using TooBusy.Core.Settings;
using TooBusy.Infrastructure.Settings;

namespace TooBusy.Infrastructure.Tests;

public class SettingsTomlTests
{
    // The example of docs/SPEC.md.
    const string Example = """
        version = 1

        [tracker]
        type = "github"
        repository = "bitpatch/toobusy"
        board = "https://github.com/orgs/bitpatch/projects/3"   # optional

        [queue.milestone]
        rule = "lowest-version"   # lowest-version | earliest-due | fixed | none
        # title = "v.0.2.0"       # with rule = "fixed" only

        [queue.labels]
        blocking = ["manual", "draft"]              # a task with any of these is never taken
        take = ["feature", "bug", "chore", "docs"]  # a task needs one of these; empty means any task

        [assistant]
        type = "claude-code"

        """;

    static readonly ProjectSettings Settings = new(
        new TrackerSettings("github", "bitpatch/toobusy", "https://github.com/orgs/bitpatch/projects/3"),
        new QueueSettings(
            new MilestoneSettings(MilestoneRule.LowestVersion, null),
            new LabelSettings(["manual", "draft"], ["feature", "bug", "chore", "docs"])),
        new AssistantSettings("claude-code"));

    [Fact]
    public void TheExampleOfTheSpecificationIsRead()
    {
        var result = SettingsToml.Read(Example);

        Assert.Empty(result.Errors);
        AssertSame(Settings, result.Settings!);
    }

    [Fact]
    public void KeysMayBeWrittenInAnyTomlForm()
    {
        var result = SettingsToml.Read("""
            version = 1
            tracker.type = 'github'
            "tracker".repository = "bitpatch/toobusy"
            assistant.type = "claude-code"

            [queue]
            milestone.rule = "fixed"
            milestone.title = "v.0.2.0"
            labels.blocking = [
              "manual",  # by hand
            ]
            labels.take = []
            """);

        Assert.Empty(result.Errors);
        Assert.Equal(new MilestoneSettings(MilestoneRule.Fixed, "v.0.2.0"), result.Settings!.Queue.Milestone);
        Assert.Equal(["manual"], result.Settings.Queue.Labels.Blocking);
    }

    [Fact]
    public void AnErrorNamesTheKeyAndTheLine()
    {
        var result = SettingsToml.Read(Example.Replace("\"bitpatch/toobusy\"", "42", StringComparison.Ordinal));

        Assert.Null(result.Settings);
        Assert.Equal(new SettingsError("tracker.repository", 5, "must be a string"), Assert.Single(result.Errors));
    }

    [Fact]
    public void AnUnknownKeyNamesItsLine()
    {
        var result = SettingsToml.Read(Example.Replace("[assistant]", "[assistant]\nmodel = \"opus\"", StringComparison.Ordinal));

        Assert.Equal(new SettingsError("assistant.model", 17, "unknown key"), Assert.Single(result.Errors));
    }

    [Fact]
    public void AMissingKeyNamesTheLineOfItsSection()
    {
        var result = SettingsToml.Read(Example.Replace("type = \"claude-code\"", "", StringComparison.Ordinal));

        Assert.Equal(new SettingsError("assistant.type", 16, "is missing"), Assert.Single(result.Errors));
    }

    [Theory]
    [InlineData("tracker.repository = { owner = \"bitpatch\", name = \"toobusy\" }", "tracker.repository")]
    [InlineData("tracker.repository = 2026-10-09", "tracker.repository")]
    [InlineData("tracker.repository = 1.5", "tracker.repository")]
    [InlineData("[[tracker]]", "tracker")]
    public void ValuesOfOtherKindsAreRefused(string line, string key)
    {
        var result = SettingsToml.Read("version = 1\n" + line + "\n");

        Assert.Contains(result.Errors, error => error.Key == key && error.Line == 2);
    }

    [Theory]
    [InlineData("version = 1\n[tracker\n", 2)]
    [InlineData("version = 1\nversion = 1\n", 2)]
    [InlineData("version = 1\n\n[tracker]\ntype = \"github\n", 4)]
    public void TextThatIsNotTomlIsAnErrorWithItsLine(string text, int line)
    {
        var result = SettingsToml.Read(text);

        Assert.Null(result.Settings);
        Assert.All(result.Errors, error => Assert.Null(error.Key));
        Assert.Equal(line, result.Errors[0].Line);
    }

    [Fact]
    public void ANewFileHasACommentOverEachSection()
    {
        var text = SettingsToml.Write(Settings);

        Assert.Equal("""
            version = 1

            # Where the tasks are.
            [tracker]
            type = "github"
            repository = "bitpatch/toobusy"
            board = "https://github.com/orgs/bitpatch/projects/3"

            # Which milestone the tasks are taken from: lowest-version, earliest-due, fixed (with a title) or none.
            [queue.milestone]
            rule = "lowest-version"

            # A task with a blocking label is never taken. A task needs one of the labels to take; an empty list means any task.
            [queue.labels]
            blocking = ["manual", "draft"]
            take = ["feature", "bug", "chore", "docs"]

            # Who does the tasks.
            [assistant]
            type = "claude-code"

            """, text);
    }

    [Fact]
    public void WhatIsWrittenIsReadBack()
    {
        var settings = Settings with
        {
            Tracker = Settings.Tracker with { Board = null },
            Queue = new QueueSettings(
                new MilestoneSettings(MilestoneRule.Fixed, "The \"first\" one \\ v.0.2.0"),
                new LabelSettings([], ["good first issue", "höhe"])),
        };

        var result = SettingsToml.Read(SettingsToml.Write(settings));

        Assert.Empty(result.Errors);
        AssertSame(settings, result.Settings!);
    }

    [Fact]
    public void ARewriteWithTheSameSettingsChangesNothing()
    {
        Assert.Equal(Example, SettingsToml.Write(Settings, Example));
    }

    [Fact]
    public void ARewriteChangesTheGivenValuesAndKeepsTheComments()
    {
        var settings = Settings with
        {
            Tracker = Settings.Tracker with { Repository = "bitpatch/other" },
            Queue = Settings.Queue with { Labels = new LabelSettings(["manual"], Settings.Queue.Labels.Take) },
        };

        var text = SettingsToml.Write(settings, Example);

        Assert.Equal(
            Example
                .Replace("\"bitpatch/toobusy\"", "\"bitpatch/other\"", StringComparison.Ordinal)
                .Replace("[\"manual\", \"draft\"]", "[\"manual\"]", StringComparison.Ordinal),
            text);
    }

    [Fact]
    public void ARewriteAddsAKeyToItsSection()
    {
        var settings = Settings with { Queue = Settings.Queue with { Milestone = new MilestoneSettings(MilestoneRule.Fixed, "v.0.2.0") } };

        var text = SettingsToml.Write(settings, Example);

        Assert.Equal(
            Example
                .Replace("rule = \"lowest-version\"", "rule = \"fixed\"", StringComparison.Ordinal)
                .Replace("none\n", "none\ntitle = \"v.0.2.0\"\n", StringComparison.Ordinal),
            text);
    }

    [Fact]
    public void ARewriteRemovesAKeyThatIsNoLongerSet()
    {
        var settings = Settings with { Tracker = Settings.Tracker with { Board = null } };

        var text = SettingsToml.Write(settings, Example);

        Assert.Equal(Example.Replace("board = \"https://github.com/orgs/bitpatch/projects/3\"   # optional\n", "", StringComparison.Ordinal), text);
    }

    [Fact]
    public void ARewriteAddsWhatTheFileLacks()
    {
        var text = SettingsToml.Write(Settings, "# Ours.\n[tracker]\ntype = \"github\" # the only one");

        Assert.Equal("""
            version = 1

            # Ours.
            [tracker]
            type = "github" # the only one
            repository = "bitpatch/toobusy"
            board = "https://github.com/orgs/bitpatch/projects/3"

            # Which milestone the tasks are taken from: lowest-version, earliest-due, fixed (with a title) or none.
            [queue.milestone]
            rule = "lowest-version"

            # A task with a blocking label is never taken. A task needs one of the labels to take; an empty list means any task.
            [queue.labels]
            blocking = ["manual", "draft"]
            take = ["feature", "bug", "chore", "docs"]

            # Who does the tasks.
            [assistant]
            type = "claude-code"

            """, text);
    }

    [Fact]
    public void ARewriteKeepsKeysItDoesNotKnow()
    {
        var existing = Example + "\n# Ours.\n[prompts]\ntask = \"Do it.\"  # short\n";
        var settings = Settings with { Assistant = new AssistantSettings("codex") };

        var text = SettingsToml.Write(settings, existing);

        Assert.Equal(existing.Replace("\"claude-code\"", "\"codex\"", StringComparison.Ordinal), text);
    }

    [Fact]
    public void ARewriteKeepsWindowsLineEndings()
    {
        var existing = Example.ReplaceLineEndings("\r\n");
        var settings = Settings with { Tracker = Settings.Tracker with { Repository = "bitpatch/other", Board = null } };

        var text = SettingsToml.Write(settings, existing);

        Assert.Equal(
            existing
                .Replace("\"bitpatch/toobusy\"", "\"bitpatch/other\"", StringComparison.Ordinal)
                .Replace("board = \"https://github.com/orgs/bitpatch/projects/3\"   # optional\r\n", "", StringComparison.Ordinal),
            text);
    }

    [Fact]
    public void ARewriteLeavesAListAloneWhenItsLabelsAreTheSame()
    {
        var existing = Example.Replace("[\"manual\", \"draft\"] ", "[\n  \"manual\",  # by hand\n  'draft',\n]", StringComparison.Ordinal);

        Assert.Equal(existing, SettingsToml.Write(Settings, existing));
    }

    [Theory]
    [InlineData("version = 1\n[tracker\n")]
    [InlineData("tracker = { type = \"github\", repository = \"bitpatch/toobusy\", board = \"https://github.com/orgs/bitpatch/projects/3\" }\n")]
    public void ARewriteRefusesTextItCannotEdit(string existing)
    {
        Assert.Throws<FormatException>(() => SettingsToml.Write(Settings with { Tracker = Settings.Tracker with { Board = null } }, existing));
    }

    static void AssertSame(ProjectSettings expected, ProjectSettings actual)
    {
        Assert.Equal(expected.Tracker, actual.Tracker);
        Assert.Equal(expected.Queue.Milestone, actual.Queue.Milestone);
        Assert.Equal(expected.Queue.Labels.Blocking, actual.Queue.Labels.Blocking);
        Assert.Equal(expected.Queue.Labels.Take, actual.Queue.Labels.Take);
        Assert.Equal(expected.Assistant, actual.Assistant);
    }
}
