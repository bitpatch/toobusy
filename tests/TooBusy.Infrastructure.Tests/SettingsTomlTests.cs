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
        board = "https://github.com/orgs/bitpatch/projects/3"   # optional

        [queue.labels]
        blocking = ["manual", "draft"]              # a task with any of these is never taken
        take = ["feature", "bug", "chore", "docs"]  # a task needs one of these; empty means any task
        owner = "manual"                            # put on a task that waits for the owner
        interrupted = "interrupted"                 # put on a task that a run had to stop

        [assistant]
        type = "claude-code"

        """;

    static readonly ProjectSettings Settings = new(
        new TrackerSettings("github", "https://github.com/orgs/bitpatch/projects/3"),
        new QueueSettings(new LabelSettings(["manual", "draft"], ["feature", "bug", "chore", "docs"], "manual", "interrupted")),
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
            "tracker".board = "https://github.com/orgs/bitpatch/projects/3"
            assistant.type = "claude-code"

            [queue]
            labels.blocking = [
              "manual",  # by hand
            ]
            labels.take = []
            labels.owner = "manual"
            labels."interrupted" = 'paused'
            """);

        Assert.Empty(result.Errors);
        Assert.Equal("https://github.com/orgs/bitpatch/projects/3", result.Settings!.Tracker.Board);
        Assert.Equal(["manual"], result.Settings.Queue.Labels.Blocking);
        Assert.Equal("paused", result.Settings.Queue.Labels.Interrupted);
    }

    [Fact]
    public void AnErrorNamesTheKeyAndTheLine()
    {
        var result = SettingsToml.Read(Example.Replace("\"https://github.com/orgs/bitpatch/projects/3\"", "42", StringComparison.Ordinal));

        Assert.Null(result.Settings);
        Assert.Equal(new SettingsError("tracker.board", 5, "must be a string"), Assert.Single(result.Errors));
    }

    [Fact]
    public void AnUnknownKeyNamesItsLine()
    {
        var result = SettingsToml.Read(Example.Replace("[assistant]", "[assistant]\nmodel = \"opus\"", StringComparison.Ordinal));

        Assert.Equal(new SettingsError("assistant.model", 14, "unknown key"), Assert.Single(result.Errors));
    }

    [Fact]
    public void TheRepositoryIsNotAKeyOfTheSettings()
    {
        var result = SettingsToml.Read(Example.Replace("type = \"github\"", "type = \"github\"\nrepository = \"bitpatch/toobusy\"", StringComparison.Ordinal));

        Assert.Equal(new SettingsError("tracker.repository", 5, "unknown key"), Assert.Single(result.Errors));
    }

    [Fact]
    public void AMissingKeyNamesTheLineOfItsSection()
    {
        var result = SettingsToml.Read(Example.Replace("type = \"claude-code\"", "", StringComparison.Ordinal));

        Assert.Equal(new SettingsError("assistant.type", 13, "is missing"), Assert.Single(result.Errors));
    }

    [Theory]
    [InlineData("tracker.board = { owner = \"bitpatch\", number = 3 }", "tracker.board")]
    [InlineData("tracker.board = 2026-10-09", "tracker.board")]
    [InlineData("tracker.board = 1.5", "tracker.board")]
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
            board = "https://github.com/orgs/bitpatch/projects/3"

            # A task with a blocking label is never taken. A task needs one of the labels to take; an empty list means any task.
            # A run puts `owner` on a task that waits for the owner, which is not taken while it has it,
            # and `interrupted` on a task it had to stop, which is taken first.
            [queue.labels]
            blocking = ["manual", "draft"]
            take = ["feature", "bug", "chore", "docs"]
            owner = "manual"
            interrupted = "interrupted"

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
            Queue = new QueueSettings(new LabelSettings(["The \"first\" one \\ v.0.2.0"], ["good first issue", "höhe"], "needs the owner", "interrupted")),
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
            Tracker = Settings.Tracker with { Board = "https://github.com/orgs/bitpatch/projects/9" },
            Queue = Settings.Queue with { Labels = Settings.Queue.Labels with { Blocking = ["manual"] } },
        };

        var text = SettingsToml.Write(settings, Example);

        Assert.Equal(
            Example
                .Replace("projects/3", "projects/9", StringComparison.Ordinal)
                .Replace("[\"manual\", \"draft\"]", "[\"manual\"]", StringComparison.Ordinal),
            text);
    }

    [Fact]
    public void ARewriteAddsAKeyToItsSection()
    {
        var existing = Example.Replace("board = \"https://github.com/orgs/bitpatch/projects/3\"   # optional\n", "", StringComparison.Ordinal);

        var text = SettingsToml.Write(Settings, existing);

        Assert.Equal(
            existing.Replace("type = \"github\"\n", "type = \"github\"\nboard = \"https://github.com/orgs/bitpatch/projects/3\"\n", StringComparison.Ordinal),
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
            board = "https://github.com/orgs/bitpatch/projects/3"

            # A task with a blocking label is never taken. A task needs one of the labels to take; an empty list means any task.
            # A run puts `owner` on a task that waits for the owner, which is not taken while it has it,
            # and `interrupted` on a task it had to stop, which is taken first.
            [queue.labels]
            blocking = ["manual", "draft"]
            take = ["feature", "bug", "chore", "docs"]
            owner = "manual"
            interrupted = "interrupted"

            # Who does the tasks.
            [assistant]
            type = "claude-code"

            """, text);
    }

    [Fact]
    public void ARewriteAddsTheLabelsOfARunToAFileMadeBeforeThem()
    {
        var existing = Example
            .Replace("owner = \"manual\"                            # put on a task that waits for the owner\n", "", StringComparison.Ordinal)
            .Replace("interrupted = \"interrupted\"                 # put on a task that a run had to stop\n", "", StringComparison.Ordinal);

        var text = SettingsToml.Write(Settings, existing);

        Assert.Equal(
            existing.Replace("empty means any task\n", "empty means any task\nowner = \"manual\"\ninterrupted = \"interrupted\"\n", StringComparison.Ordinal),
            text);
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
        var settings = Settings with
        {
            Tracker = Settings.Tracker with { Board = null },
            Queue = new QueueSettings(Settings.Queue.Labels with { Blocking = ["manual"] }),
        };

        var text = SettingsToml.Write(settings, existing);

        Assert.Equal(
            existing
                .Replace("[\"manual\", \"draft\"]", "[\"manual\"]", StringComparison.Ordinal)
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
    [InlineData("tracker = { type = \"github\", board = \"https://github.com/orgs/bitpatch/projects/3\" }\n")]
    public void ARewriteRefusesTextItCannotEdit(string existing)
    {
        Assert.Throws<FormatException>(() => SettingsToml.Write(Settings with { Tracker = Settings.Tracker with { Board = null } }, existing));
    }

    static void AssertSame(ProjectSettings expected, ProjectSettings actual)
    {
        Assert.Equal(expected.Tracker, actual.Tracker);
        Assert.Equal(expected.Queue.Labels.Blocking, actual.Queue.Labels.Blocking);
        Assert.Equal(expected.Queue.Labels.Take, actual.Queue.Labels.Take);
        Assert.Equal(expected.Queue.Labels.Owner, actual.Queue.Labels.Owner);
        Assert.Equal(expected.Queue.Labels.Interrupted, actual.Queue.Labels.Interrupted);
        Assert.Equal(expected.Assistant, actual.Assistant);
    }
}
