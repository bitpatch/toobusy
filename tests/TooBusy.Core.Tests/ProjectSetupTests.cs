using TooBusy.Core.Queue;
using TooBusy.Core.Settings;
using TooBusy.Core.Setup;

namespace TooBusy.Core.Tests;

public class ProjectSetupTests
{
    static readonly ProjectSettings Existing = new(
        new TrackerSettings("github", "bitpatch/toobusy", "https://github.com/orgs/bitpatch/projects/3"),
        new QueueSettings(new MilestoneSettings(MilestoneRule.Fixed, "Polish"), new LabelSettings(["manual"], ["bug", "retired"])),
        new AssistantSettings("claude-code"));

    readonly ScriptedDialog dialog = new();
    readonly FakeMachine machine = new();
    readonly FakeTracker tracker = new();
    readonly FakeStore store = new();

    [Fact]
    public async Task AFirstSetupProposesWhatItFindsAndWritesIt()
    {
        var outcome = await RunAsync();

        Assert.Equal(SetupOutcome.Written, outcome);
        Assert.Equal(new TrackerSettings("github", "acme/rocket", null), store.Saved!.Tracker);
        Assert.Equal(new MilestoneSettings(MilestoneRule.LowestVersion, null), store.Saved.Queue.Milestone);
        Assert.Empty(store.Saved.Queue.Labels.Blocking);
        Assert.Empty(store.Saved.Queue.Labels.Take);
        Assert.Equal(new AssistantSettings("claude-code"), store.Saved.Assistant);
        Assert.Equal(
            ["Tracker", "Repository", "Board", "Milestone rule", "Blocking labels", "Labels to take", "Assistant", "Write the settings?"],
            dialog.Steps);
    }

    [Fact]
    public async Task AFirstSetupShowsTheWholeFileAsAdditions()
    {
        await RunAsync();

        Assert.Contains((SetupTone.Plain, "New file .toobusy/settings.toml:"), dialog.Said);
        Assert.Contains((SetupTone.Added, "+ repository = acme/rocket"), dialog.Said);
        Assert.DoesNotContain(dialog.Said, line => line.Tone == SetupTone.Removed);
    }

    [Fact]
    public async Task TheAnswersBecomeTheSettings()
    {
        dialog.Answer("Repository", "https://github.com/bitpatch/toobusy.git");
        dialog.Answer("Board", " https://github.com/users/denis/projects/7 ");
        dialog.Answer("Milestone rule", "earliest-due");
        dialog.Answer("Blocking labels", "manual", "draft");
        dialog.Answer("Labels to take", "bug");

        await RunAsync();

        Assert.Equal(new TrackerSettings("github", "bitpatch/toobusy", "https://github.com/users/denis/projects/7"), store.Saved!.Tracker);
        Assert.Equal(new MilestoneSettings(MilestoneRule.EarliestDue, null), store.Saved.Queue.Milestone);
        Assert.Equal(["draft", "manual"], store.Saved.Queue.Labels.Blocking);
        Assert.Equal(["bug"], store.Saved.Queue.Labels.Take);
    }

    [Fact]
    public async Task EachRuleIsShownWithTheMilestoneItWouldChooseNow()
    {
        await RunAsync();

        var details = dialog.Offered["Milestone rule"];
        Assert.EndsWith(" → v.0.2.0", details[0], StringComparison.Ordinal);
        Assert.EndsWith(" → Polish", details[1], StringComparison.Ordinal);
    }

    [Fact]
    public async Task TheProposedRuleFollowsTheOpenMilestones()
    {
        tracker.Milestones = [new Milestone("Polish", new DateOnly(2030, 1, 15)), new Milestone("Backlog", null)];
        await RunAsync();
        Assert.Equal(MilestoneRule.EarliestDue, store.Saved!.Queue.Milestone.Rule);

        tracker.Milestones = [new Milestone("Backlog", null)];
        await RunAsync();
        Assert.Equal(MilestoneRule.None, store.Saved!.Queue.Milestone.Rule);
    }

    [Fact]
    public async Task TheFixedRuleGoesOnToPickAnOpenMilestone()
    {
        dialog.Answer("Milestone rule", "fixed");
        dialog.Answer("Milestone", "Polish");

        await RunAsync();

        Assert.Equal(new MilestoneSettings(MilestoneRule.Fixed, "Polish"), store.Saved!.Queue.Milestone);
    }

    [Fact]
    public async Task ABlockingLabelIsNotOfferedToTake()
    {
        dialog.Answer("Blocking labels", "manual");

        await RunAsync();

        Assert.Equal(["bug", "draft", "feature"], dialog.Offered["Labels to take"]);
    }

    [Fact]
    public async Task AnswersThatCannotBeRightAreRefusedByTheQuestion()
    {
        dialog.Answer("Repository", "rocket");
        dialog.Answer("Repository", "acme/rocket");
        dialog.Answer("Board", "https://example.com/board");
        dialog.Answer("Board", "");

        await RunAsync();

        Assert.Equal(["rocket", "https://example.com/board"], dialog.Refused);
        Assert.Equal(new TrackerSettings("github", "acme/rocket", null), store.Saved!.Tracker);
    }

    [Fact]
    public async Task ARepositoryThatCannotBeReadIsAskedAgain()
    {
        tracker.Unreadable.Add("acme/secret");
        dialog.Answer("Repository", "acme/secret");
        dialog.Answer("Repository", "acme/rocket");

        await RunAsync();

        Assert.Contains((SetupTone.Failure, "acme/secret cannot be read"), dialog.Said);
        Assert.Equal("acme/rocket", store.Saved!.Tracker.Repository);
    }

    [Fact]
    public async Task ABoardThatCannotBeReadIsAskedAgain()
    {
        tracker.Unreadable.Add("https://github.com/orgs/acme/projects/1");
        dialog.Answer("Board", "https://github.com/orgs/acme/projects/1");
        dialog.Answer("Board", "https://github.com/orgs/acme/projects/2");

        await RunAsync();

        Assert.Equal("https://github.com/orgs/acme/projects/2", store.Saved!.Tracker.Board);
    }

    [Fact]
    public async Task AnExistingSetupProposesItsValuesSoThatAcceptingThemChangesNothing()
    {
        store.Current = Existing;

        var outcome = await RunAsync();

        Assert.Equal(SetupOutcome.NothingToChange, outcome);
        Assert.Null(store.Saved);
        Assert.Contains((SetupTone.Plain, "Nothing to change"), dialog.Said);
        Assert.DoesNotContain("Write the settings?", dialog.Steps);
    }

    [Fact]
    public async Task AnExistingSetupShowsOnlyWhatChanges()
    {
        store.Current = Existing;
        dialog.Answer("Repository", "acme/rocket");

        await RunAsync();

        Assert.Equal(
            [(SetupTone.Removed, "- repository = bitpatch/toobusy"), (SetupTone.Added, "+ repository = acme/rocket")],
            dialog.Said.Where(line => line.Tone is SetupTone.Added or SetupTone.Removed));
        Assert.Equal(Existing with { Tracker = Existing.Tracker with { Repository = "acme/rocket" } }, store.Saved, SameSettings);
    }

    [Fact]
    public async Task SettingsThatDoNotValidateAreReported()
    {
        store.Errors = [new SettingsError("tracker.repository", 5, "is missing")];

        var outcome = await RunAsync();

        Assert.Contains((SetupTone.Failure, ".toobusy/settings.toml:5: tracker.repository: is missing"), dialog.Said);
        Assert.Equal(SetupOutcome.Written, outcome);
    }

    [Fact]
    public async Task ProblemsOfTheMachineAreShownWithTheirFixesAndDoNotStopTheSetup()
    {
        machine.Problems = [new SetupProblem("Claude Code is not installed", "curl -fsSL https://claude.ai/install.sh | bash")];

        var outcome = await RunAsync();

        Assert.Equal(
            [(SetupTone.Failure, "Claude Code is not installed"), (SetupTone.Muted, "fix: curl -fsSL https://claude.ai/install.sh | bash")],
            dialog.Said.Take(2));
        Assert.Equal(SetupOutcome.Written, outcome);
    }

    [Fact]
    public async Task WithoutTheTrackerTheAnswersAreTypedAndNothingIsVerified()
    {
        machine.TrackerReachable = false;
        tracker.Unreadable.Add("acme/secret");
        dialog.Answer("Repository", "acme/secret");
        dialog.Answer("Milestone rule", "fixed");
        dialog.Answer("Milestone", "v.0.3.0");
        dialog.Answer("Blocking labels", "manual, draft,");
        dialog.Answer("Labels to take", "draft");
        dialog.Answer("Labels to take", "bug, feature");

        await RunAsync();

        Assert.Contains(dialog.Said, line => line.Tone == SetupTone.Warning && line.Text.Contains("nothing is verified", StringComparison.Ordinal));
        Assert.Equal(0, tracker.Calls);
        Assert.Equal("acme/secret", store.Saved!.Tracker.Repository);
        Assert.Equal(new MilestoneSettings(MilestoneRule.Fixed, "v.0.3.0"), store.Saved.Queue.Milestone);
        Assert.Equal(["manual", "draft"], store.Saved.Queue.Labels.Blocking);
        Assert.Equal(["bug", "feature"], store.Saved.Queue.Labels.Take);
        Assert.Equal(["draft"], dialog.Refused);
    }

    [Fact]
    public async Task DecliningWritesNothing()
    {
        dialog.Confirmed = false;

        var outcome = await RunAsync();

        Assert.Equal(SetupOutcome.Declined, outcome);
        Assert.Null(store.Saved);
    }

    Task<SetupOutcome> RunAsync() => new ProjectSetup(dialog, machine, tracker, store).RunAsync(TestContext.Current.CancellationToken);

    static bool SameSettings(ProjectSettings? left, ProjectSettings? right) => FakeStore.Render(left!) == FakeStore.Render(right!);

    // Answers each question from its script and accepts what is proposed when the script has nothing for it.
    sealed class ScriptedDialog : ISetupDialog
    {
        readonly Dictionary<string, Queue<string[]>> answers = [];

        public List<string> Steps { get; } = [];

        public List<(SetupTone Tone, string Text)> Said { get; } = [];

        public List<string> Refused { get; } = [];

        // The details of a selection, or the options of a multiple choice, under the label of the question.
        public Dictionary<string, string[]> Offered { get; } = [];

        public bool Confirmed { get; set; } = true;

        public void Answer(string label, params string[] answer)
        {
            if (!answers.TryGetValue(label, out var queue))
                answers.Add(label, queue = new Queue<string[]>());
            queue.Enqueue(answer);
        }

        public int Choose(string label, IReadOnlyList<SetupOption> options, int proposed)
        {
            Steps.Add(label);
            Offered[label] = [.. options.Select(option => option.Detail)];
            return Next(label) is { } answer ? options.Select(option => option.Name).ToList().IndexOf(answer[0]) : proposed;
        }

        public IReadOnlyList<int> ChooseMany(string label, IReadOnlyList<string> options, IReadOnlyList<int> proposed, string whenNone)
        {
            Steps.Add(label);
            Offered[label] = [.. options];
            return Next(label) is { } answer ? [.. answer.Select(name => options.ToList().IndexOf(name)).Order()] : proposed;
        }

        public string Ask(string label, string hint, string proposed, string whenEmpty, Func<string, string?> refuse)
        {
            Steps.Add(label);
            while (true)
            {
                var answer = Next(label) is { } scripted ? scripted[0] : proposed;
                if (refuse(answer) is null)
                    return answer;
                Refused.Add(answer);
            }
        }

        public bool Confirm(string question)
        {
            Steps.Add(question);
            return Confirmed;
        }

        public void Answered(string label, string value) => Steps.Add(label);

        public void Say(SetupTone tone, string text) => Said.Add((tone, text));

        string[]? Next(string label) => answers.TryGetValue(label, out var queue) && queue.Count > 0 ? queue.Dequeue() : null;
    }

    sealed class FakeMachine : ISetupEnvironment
    {
        public IReadOnlyList<SetupProblem> Problems { get; set; } = [];

        public bool TrackerReachable { get; set; } = true;

        public Task<SetupEnvironment> InspectAsync(CancellationToken cancellationToken) =>
            Task.FromResult(new SetupEnvironment(Problems, TrackerReachable, "acme/rocket"));
    }

    sealed class FakeTracker : ISetupTracker
    {
        public IReadOnlyList<Milestone> Milestones { get; set; } =
            [new("v.0.3.0", null), new("v.0.2.0", new DateOnly(2030, 3, 1)), new("Polish", new DateOnly(2030, 1, 15))];

        public HashSet<string> Unreadable { get; } = [];

        public int Calls { get; private set; }

        public Task<string?> RefuseRepositoryAsync(string repository, CancellationToken cancellationToken) => Task.FromResult(Refuse(repository));

        public Task<string?> RefuseBoardAsync(string board, CancellationToken cancellationToken) => Task.FromResult(Refuse(board));

        public Task<IReadOnlyList<string>> ReadLabelsAsync(string repository, CancellationToken cancellationToken)
        {
            Calls++;
            return Task.FromResult<IReadOnlyList<string>>(["bug", "draft", "feature", "manual"]);
        }

        public Task<IReadOnlyList<Milestone>> ReadOpenMilestonesAsync(string repository, CancellationToken cancellationToken)
        {
            Calls++;
            return Task.FromResult(Milestones);
        }

        string? Refuse(string what)
        {
            Calls++;
            return Unreadable.Contains(what) ? $"{what} cannot be read" : null;
        }
    }

    // Keeps the settings in memory and shows them as one `key = value` line each.
    sealed class FakeStore : ISettingsStore
    {
        public ProjectSettings? Current { get; set; }

        public IReadOnlyList<SettingsError>? Errors { get; set; }

        public ProjectSettings? Saved { get; private set; }

        public string DisplayPath => ".toobusy/settings.toml";

        public SettingsValidation? Load() =>
            Errors is not null ? new SettingsValidation(null, Errors) : Current is null ? null : new SettingsValidation(Current, []);

        public SettingsPreview Preview(ProjectSettings settings) => new(Current is null ? "" : Render(Current), Render(settings));

        public void Save(ProjectSettings settings) => Saved = settings;

        public static string Render(ProjectSettings settings) => string.Join('\n',
            $"repository = {settings.Tracker.Repository}",
            $"board = {settings.Tracker.Board}",
            $"rule = {settings.Queue.Milestone.Rule} {settings.Queue.Milestone.Title}",
            $"blocking = {string.Join(", ", settings.Queue.Labels.Blocking)}",
            $"take = {string.Join(", ", settings.Queue.Labels.Take)}") + "\n";
    }
}
