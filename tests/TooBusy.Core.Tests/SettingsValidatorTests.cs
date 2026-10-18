using TooBusy.Core.Queue;
using TooBusy.Core.Settings;

namespace TooBusy.Core.Tests;

public class SettingsValidatorTests
{
    const string Board = "https://github.com/orgs/bitpatch/projects/3";

    [Fact]
    public void ACompleteDocumentGivesTheSettings()
    {
        var result = SettingsValidator.Validate(Document(
            (SettingsKeys.TrackerBoard, "https://github.com/orgs/bitpatch/projects/3"),
            (SettingsKeys.BlockingLabels, Labels("manual", "draft")),
            (SettingsKeys.TakeLabels, Labels("feature", "bug")),
            (SettingsKeys.ManualLabel, "manual"),
            (SettingsKeys.InterruptedLabel, "paused")));

        Assert.Empty(result.Errors);
        Assert.Equal(new TrackerSettings("github", "https://github.com/orgs/bitpatch/projects/3"), result.Settings!.Tracker);
        Assert.Equal(["manual", "draft"], result.Settings.Queue.Labels.Blocking);
        Assert.Equal(["feature", "bug"], result.Settings.Queue.Labels.Take);
        Assert.Equal("manual", result.Settings.Queue.Labels.Manual);
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
    [InlineData(SettingsKeys.ManualLabel)]
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
    [InlineData(SettingsKeys.ManualLabel, 1L)]
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
    public void TheCommandsAroundATaskAreOptionalEachApart()
    {
        Assert.Equal(TaskCommands.None, SettingsValidator.Validate(Document()).Settings!.Commands);

        var both = SettingsValidator.Validate(Document(("task", ""), (SettingsKeys.TaskBefore, "scripts/dev.sh ports free"), (SettingsKeys.TaskAfter, "scripts/dev.sh server stop")));
        var one = SettingsValidator.Validate(Document(("task", ""), (SettingsKeys.TaskAfter, "make stop && echo \"stopped\"")));

        Assert.Empty(both.Errors);
        Assert.Equal(new TaskCommands("scripts/dev.sh ports free", "scripts/dev.sh server stop"), both.Settings!.Commands);
        Assert.Empty(one.Errors);
        Assert.Equal(new TaskCommands(null, "make stop && echo \"stopped\""), one.Settings!.Commands);
    }

    [Fact]
    public void ThePermissionModeIsOptional()
    {
        var unnamed = SettingsValidator.Validate(Document()).Settings!.Assistant;
        var named = SettingsValidator.Validate(Document((SettingsKeys.PermissionMode, "dontAsk")));

        Assert.Null(unnamed.PermissionMode);
        Assert.Equal("auto", unnamed.SessionMode);
        Assert.Empty(named.Errors);
        Assert.Equal(new AssistantSettings("claude-code") { PermissionMode = "dontAsk" }, named.Settings!.Assistant);
        Assert.Equal("dontAsk", named.Settings.Assistant.SessionMode);
    }

    [Theory]
    [InlineData("auto")]
    [InlineData("dontAsk")]
    public void AModeOfClaudeCodeInWhichASessionGoesOnWithNobodyIsTaken(string mode)
    {
        Assert.Equal(mode, SettingsValidator.Validate(Document((SettingsKeys.PermissionMode, mode))).Settings!.Assistant.PermissionMode);
    }

    // A session in such a mode would wait for an answer that never comes.
    [Theory]
    [InlineData("manual")]
    [InlineData("default")]
    [InlineData("acceptEdits")]
    [InlineData("plan")]
    public void AModeThatWaitsForAnAnswerIsRefused(string mode)
    {
        var error = OnlyError(Document((SettingsKeys.PermissionMode, mode)));

        Assert.Equal(
            new SettingsError(SettingsKeys.PermissionMode, LineOf(SettingsKeys.PermissionMode), $"must be `auto` or `dontAsk`: in `{mode}` a session waits for an answer, and nobody answers a session of a run"),
            error);
    }

    [Fact]
    public void AModeThatChecksNothingIsRefused()
    {
        var error = OnlyError(Document((SettingsKeys.PermissionMode, "bypassPermissions")));

        Assert.Equal("must be `auto` or `dontAsk`: `bypassPermissions` checks nothing a session does, and toobusy starts no session in it", error.Message);
    }

    // The file names a mode as Claude Code writes it.
    [Theory]
    [InlineData("")]
    [InlineData("Auto")]
    [InlineData("dont-ask")]
    [InlineData("yolo")]
    public void AnythingElseIsNotAPermissionMode(string mode)
    {
        var error = OnlyError(Document((SettingsKeys.PermissionMode, mode)));

        Assert.Equal(
            new SettingsError(SettingsKeys.PermissionMode, LineOf(SettingsKeys.PermissionMode), "must be `auto` or `dontAsk`, a permission mode of Claude Code in which a session goes on with nobody to answer it"),
            error);
        Assert.Equal("must be a string", OnlyError(Document((SettingsKeys.PermissionMode, 1L))).Message);
    }

    // The modes are known by the assistant: of one that toobusy does not know, nothing more is said than that.
    [Fact]
    public void ThePermissionModeIsOneOfTheAssistantTheSettingsName()
    {
        var error = OnlyError(Document((SettingsKeys.AssistantType, "codex"), (SettingsKeys.PermissionMode, "plan")));

        Assert.Equal(SettingsKeys.AssistantType, error.Key);
    }

    [Fact]
    public void TheBranchOfTheTasksIsOptional()
    {
        Assert.Null(SettingsValidator.Validate(Document()).Settings!.Branch);

        var named = SettingsValidator.Validate(Document(("workspace", ""), (SettingsKeys.WorkspaceBranch, "release/2.0")));

        Assert.Empty(named.Errors);
        Assert.Equal("release/2.0", named.Settings!.Branch);
    }

    // Each refused by `git check-ref-format --branch` as well.
    [Theory]
    [InlineData("")]
    [InlineData("my branch")]
    [InlineData("-develop")]
    [InlineData("/develop")]
    [InlineData("develop/")]
    [InlineData("develop.")]
    [InlineData(".develop")]
    [InlineData("feature/.hidden")]
    [InlineData("a..b")]
    [InlineData("a//b")]
    [InlineData("develop.lock")]
    [InlineData("a.lock/b")]
    [InlineData("a@{1}")]
    [InlineData("@")]
    [InlineData("HEAD")]
    [InlineData("a~1")]
    [InlineData("a^")]
    [InlineData("a:b")]
    [InlineData("a?")]
    [InlineData("a*")]
    [InlineData("a[b")]
    [InlineData("a\\b")]
    [InlineData("a\tb")]
    public void ANameThatGitDoesNotTakeForABranchIsRefused(string branch)
    {
        var error = OnlyError(Document(("workspace", ""), (SettingsKeys.WorkspaceBranch, branch)));

        Assert.Equal(
            new SettingsError(SettingsKeys.WorkspaceBranch, LineOf(SettingsKeys.WorkspaceBranch), "must be the name of a branch as git takes it, as `develop`: no space, none of `~^:?*[\\`, no `..` and no `@{`, and neither `/` nor `.` at either end"),
            error);
    }

    [Theory]
    [InlineData("develop")]
    [InlineData("main")]
    [InlineData("feature/export-2.0")]
    [InlineData("release_1.x")]
    [InlineData("@home")]
    [InlineData("ветка")]
    public void ANameThatGitTakesIsABranch(string branch) => Assert.Null(SettingsValidator.RefuseBranch(branch));

    [Theory]
    [InlineData(SettingsKeys.TaskBefore, "")]
    [InlineData(SettingsKeys.TaskAfter, "  ")]
    public void ACommandThatIsEmptyIsRefused(string key, string command)
    {
        var error = OnlyError(Document(("task", ""), (key, command)));

        Assert.Equal(new SettingsError(key, LineOf(key), "a command must not be empty: leave the key out to run none"), error);
    }

    [Fact]
    public void ACommandThatIsNotATextIsRefused()
    {
        var error = OnlyError(Document(("task", ""), (SettingsKeys.TaskBefore, Labels("make", "stop"))));

        Assert.Equal(new SettingsError(SettingsKeys.TaskBefore, LineOf(SettingsKeys.TaskBefore), "must be a string"), error);
    }

    [Fact]
    public void TheSkillIsOptional()
    {
        Assert.Null(SettingsValidator.Validate(Document()).Settings!.Skill);

        var named = SettingsValidator.Validate(Document(("session", ""), (SettingsKeys.SessionSkill, "task-auto")));

        Assert.Empty(named.Errors);
        Assert.Equal("task-auto", named.Settings!.Skill);
    }

    [Theory]
    [InlineData("task-auto")]
    [InlineData("Task_auto2")]
    [InlineData("toolbox:task-auto")]
    public void ASkillIsNamedAsItsCommandIsWithoutTheSlash(string skill)
    {
        Assert.Empty(SettingsValidator.Validate(Document(("session", ""), (SettingsKeys.SessionSkill, skill))).Errors);
    }

    [Theory]
    [InlineData("")]
    [InlineData("/task-auto")]
    [InlineData("task auto")]
    [InlineData("-task")]
    [InlineData("task\nauto")]
    public void AnythingElseIsNotTheNameOfASkill(string skill)
    {
        var error = OnlyError(Document(("session", ""), (SettingsKeys.SessionSkill, skill)));

        Assert.Equal(
            new SettingsError(SettingsKeys.SessionSkill, LineOf(SettingsKeys.SessionSkill), "must be the name of a skill, as `task-auto`: letters, digits, `-`, `_`, `.` and `:`, without the `/` before it"),
            error);
    }

    [Fact]
    public void ASkillThatIsNotATextIsRefused()
    {
        var error = OnlyError(Document(("session", ""), (SettingsKeys.SessionSkill, true)));

        Assert.Equal(new SettingsError(SettingsKeys.SessionSkill, LineOf(SettingsKeys.SessionSkill), "must be a string"), error);
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
    [InlineData(SettingsKeys.ManualLabel)]
    [InlineData(SettingsKeys.InterruptedLabel)]
    public void ALabelOfARunNeedsAName(string key)
    {
        var error = OnlyError(Document((key, " ")));

        Assert.Equal(new SettingsError(key, LineOf(key), "a label name must not be empty"), error);
    }

    [Theory]
    [InlineData(SettingsKeys.ManualLabel)]
    [InlineData(SettingsKeys.InterruptedLabel)]
    public void ALabelOfARunIsNotALabelToTake(string key)
    {
        var error = OnlyError(Document((SettingsKeys.TakeLabels, Labels("bug", "feature")), (key, "Bug")));

        Assert.Equal(new SettingsError(key, LineOf(key), "the label `Bug` is in `queue.labels.take` too"), error);
    }

    [Fact]
    public void TheManualLabelMayBeABlockingOne()
    {
        var result = SettingsValidator.Validate(Document((SettingsKeys.BlockingLabels, Labels("manual")), (SettingsKeys.ManualLabel, "manual")));

        Assert.Empty(result.Errors);
    }

    [Fact]
    public void TheManualLabelIsReadUnderTheNameItHadBefore()
    {
        var result = SettingsValidator.Validate(Document((SettingsKeys.ManualLabel, null), (SettingsKeys.FormerManualLabel, "ask")));

        Assert.Empty(result.Errors);
        Assert.Equal("ask", result.Settings!.Queue.Labels.Manual);
    }

    [Fact]
    public void AnErrorOfTheManualLabelStandsAtTheNameTheFileGivesIt()
    {
        var error = OnlyError(Document((SettingsKeys.ManualLabel, null), (SettingsKeys.FormerManualLabel, "Bug"), (SettingsKeys.TakeLabels, Labels("bug"))));

        Assert.Equal(new SettingsError(SettingsKeys.FormerManualLabel, LineOf(SettingsKeys.FormerManualLabel), "the label `Bug` is in `queue.labels.take` too"), error);
    }

    [Fact]
    public void TheManualLabelIsNotNamedTwice()
    {
        var error = OnlyError(Document((SettingsKeys.FormerManualLabel, "ask")));

        Assert.Equal(new SettingsError(SettingsKeys.FormerManualLabel, LineOf(SettingsKeys.FormerManualLabel), "is the name `queue.labels.manual` had before, and the file sets that too: leave one"), error);
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
        var error = OnlyError(Document((SettingsKeys.ManualLabel, "manual"), (SettingsKeys.InterruptedLabel, "Manual")));

        Assert.Equal(new SettingsError(SettingsKeys.InterruptedLabel, LineOf(SettingsKeys.InterruptedLabel), "must not be the label of `queue.labels.manual`"), error);
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
    public void TheStatusesOfABoardAreThoseOfGitHubUnlessTheyAreNamed()
    {
        var usual = SettingsValidator.Validate(Document((SettingsKeys.TrackerBoard, Board)));
        Assert.Equal(BoardStatuses.Default, usual.Settings!.Tracker.Statuses);

        var one = SettingsValidator.Validate(Document((SettingsKeys.TrackerBoard, Board), (SettingsKeys.DoneStatus, "Shipped")));
        Assert.Empty(one.Errors);
        Assert.Equal(new BoardStatuses("Todo", "In Progress", "Shipped"), one.Settings!.Tracker.Statuses);

        var all = SettingsValidator.Validate(Document(
            (SettingsKeys.TrackerBoard, Board), (SettingsKeys.TodoStatus, "Ready"), (SettingsKeys.InProgressStatus, "Doing"), (SettingsKeys.DoneStatus, "Shipped")));
        Assert.Empty(all.Errors);
        Assert.Equal(new BoardStatuses("Ready", "Doing", "Shipped"), all.Settings!.Tracker.Statuses);
    }

    [Theory]
    [InlineData(SettingsKeys.TodoStatus)]
    [InlineData(SettingsKeys.InProgressStatus)]
    [InlineData(SettingsKeys.DoneStatus)]
    public void AStatusIsAStringThatIsNotEmpty(string key)
    {
        var empty = OnlyError(Document((SettingsKeys.TrackerBoard, Board), (key, "  ")));
        Assert.Equal((key, "the name of a status must not be empty: leave the key out for the name GitHub gives"), (empty.Key, empty.Message));

        var number = OnlyError(Document((SettingsKeys.TrackerBoard, Board), (key, 3L)));
        Assert.Equal((key, "must be a string"), (number.Key, number.Message));
    }

    [Fact]
    public void AStatusMeansSomethingOnlyWithABoard()
    {
        var error = OnlyError(Document((SettingsKeys.TodoStatus, "Ready")));

        Assert.Equal((SettingsKeys.TodoStatus, "names a status of a board, and `tracker.board` names no board"), (error.Key, error.Message));
    }

    [Fact]
    public void TwoOfTheThreeMustNotNameOneStatus()
    {
        var named = OnlyError(Document((SettingsKeys.TrackerBoard, Board), (SettingsKeys.TodoStatus, "Ready"), (SettingsKeys.DoneStatus, "ready")));
        Assert.Equal((SettingsKeys.DoneStatus, "`tracker.statuses.todo` and `tracker.statuses.done` must not name one status: both are `ready`"), (named.Key, named.Message));

        // One of the two is the name GitHub gives: the error stands at the key the file has.
        var given = OnlyError(Document((SettingsKeys.TrackerBoard, Board), (SettingsKeys.TodoStatus, "in progress")));
        Assert.Equal((SettingsKeys.TodoStatus, LineOf(SettingsKeys.TodoStatus)), (given.Key, given.Line));
        Assert.Equal("`tracker.statuses.todo` and `tracker.statuses.in-progress` must not name one status: both are `In Progress`", given.Message);
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
        (SettingsKeys.ManualLabel, "needs-owner"),
        (SettingsKeys.InterruptedLabel, "interrupted"),
        ("assistant", ""),
        (SettingsKeys.AssistantType, "claude-code"),
    ];

    static readonly string[] Order = [.. Valid.Select(pair => pair.Key), SettingsKeys.TrackerBoard, "tracker.statuses", SettingsKeys.TodoStatus, SettingsKeys.InProgressStatus, SettingsKeys.DoneStatus, "session", SettingsKeys.SessionSkill, "task", SettingsKeys.TaskBefore, SettingsKeys.TaskAfter, "workspace", SettingsKeys.WorkspaceBranch, SettingsKeys.PermissionMode, SettingsKeys.FormerManualLabel];

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
