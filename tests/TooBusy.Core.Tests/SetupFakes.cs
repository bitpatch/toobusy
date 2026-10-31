using TooBusy.Core.Assistant;
using TooBusy.Core.Queue;
using TooBusy.Core.Settings;
using TooBusy.Core.Setup;

namespace TooBusy.Core.Tests;

// Answers each question from its script and accepts what is proposed when the script has nothing for it.
// A board that the project has is kept; without one the answer is the one that is proposed, or no board.
sealed class ScriptedDialog : ISetupDialog
{
    readonly Dictionary<string, Queue<string[]?>> answers = [];
    readonly Queue<BoardAnswer?> boards = new();
    readonly Queue<bool?> confirmations = new();

    // The names of the questions in the order they were asked.
    public List<string> Asked { get; } = [];

    public List<SetupProgress> Seen { get; } = [];

    public SetupProgress Shown => Seen[^1];

    // The step that was shown, for each time something was shown.
    public IEnumerable<int> Places => Seen.Select(progress => progress.Step);

    public List<string> Waited { get; } = [];

    public List<string> Refused { get; } = [];

    // The options of a selection or of a multiple choice, under the label of the question.
    public Dictionary<string, string[]> Offered { get; } = [];

    // The option of a selection that is marked as the one the settings have now, under the label of the question.
    public Dictionary<string, string?> Current { get; } = [];

    // The options of a multiple choice that the settings have now, under the label of the question.
    public Dictionary<string, string[]> Saved { get; } = [];

    // The options that cannot be chosen, each with what holds it, under the label of the question.
    public Dictionary<string, string[]> Held { get; } = [];

    public BoardQuestion? Question { get; private set; }

    public Action? BeforeConfirm { get; set; }

    // Forgets what was asked and shown, and keeps what is left of the script: what the next setup asks is seen alone.
    public void Reset()
    {
        Asked.Clear();
        Seen.Clear();
        Waited.Clear();
        Refused.Clear();
        Offered.Clear();
        Current.Clear();
        Saved.Clear();
        Held.Clear();
        Question = null;
    }

    public void Answer(string label, params string[] answer) => Script(label).Enqueue(answer);

    public void Back(string label) => Script(label).Enqueue(null);

    public void Board(BoardAnswer? answer) => boards.Enqueue(answer);

    public void Confirm(params bool?[] answers)
    {
        foreach (var answer in answers)
            confirmations.Enqueue(answer);
    }

    public void Show(SetupProgress progress) => Seen.Add(progress);

    // What the user does not wait for, by the text of the wait.
    public HashSet<string> Unwaited { get; } = [];

    public async Task<T?> WaitAsync<T>(string label, string hint, string text, Func<CancellationToken, Task<T>> work, CancellationToken cancellationToken)
        where T : class
    {
        Waited.Add(text);
        return Unwaited.Remove(text) ? null : await work(cancellationToken);
    }

    public int? Choose(string label, string hint, IReadOnlyList<SetupOption> options, int proposed)
    {
        Asked.Add(label);
        Offer(label, options);
        Current[label] = options.SingleOrDefault(option => option.Current)?.Name;
        if (!Next(label, out var answer))
            return proposed;
        return answer is null ? null : options.Select(option => option.Name).ToList().IndexOf(answer[0]);
    }

    public IReadOnlyList<int>? ChooseMany(string label, string hint, IReadOnlyList<SetupOption> options, IReadOnlyList<int> proposed)
    {
        Asked.Add(label);
        Offer(label, options);
        Saved[label] = [.. options.Where(option => option.Current).Select(option => option.Name)];
        if (!Next(label, out var answer))
            return proposed;
        var names = options.Select(option => option.Name).ToList();
        return answer is null ? null : [.. answer.Select(name => names.IndexOf(name)).Order()];
    }

    // What is offered, and what is off with what holds it.
    void Offer(string label, IReadOnlyList<SetupOption> options)
    {
        Offered[label] = [.. options.Where(option => !option.Off).Select(option => option.Name)];
        Held[label] = [.. options.Where(option => option.Off).Select(option => $"{option.Name}: {option.Detail}")];
    }

    public string? Ask(string label, string hint, string proposed, Func<string, string?> refuse)
    {
        Asked.Add(label);
        while (true)
        {
            var scripted = Next(label, out var answer);
            if (scripted && answer is null)
                return null;

            var text = scripted ? answer![0] : proposed;
            if (refuse(text) is null)
                return text;
            Refused.Add(text);
        }
    }

    public BoardAnswer? AskBoard(BoardQuestion question)
    {
        Asked.Add(question.Label);
        Question = question;
        if (boards.Count > 0)
            return boards.Dequeue();
        return question.Current is { } current ? new BoardAnswer.Existing(current.Address) : question.Proposed ?? new BoardAnswer.None();
    }

    public bool? Confirm(string question)
    {
        Asked.Add(question);
        BeforeConfirm?.Invoke();
        return confirmations.Count > 0 ? confirmations.Dequeue() : true;
    }

    Queue<string[]?> Script(string label)
    {
        if (!answers.TryGetValue(label, out var queue))
            answers.Add(label, queue = new Queue<string[]?>());
        return queue;
    }

    // False when the script has nothing for the question; a null answer is going back.
    bool Next(string label, out string[]? answer)
    {
        answer = null;
        return answers.TryGetValue(label, out var queue) && queue.TryDequeue(out answer);
    }
}

// A machine where nothing is missing, in a project cloned from acme/rocket that has the skill task-auto, until a
// test says otherwise.
sealed class FakeEnvironment : ISetupEnvironment
{
    public IReadOnlyList<SetupProblem> Problems { get; set; } = [];

    public bool TrackerReachable { get; set; } = true;

    public string? Origin { get; set; } = "acme/rocket";

    // The skills that are written; null is a machine where skills cannot be looked for.
    public HashSet<string>? Skills { get; set; } = ["task-auto"];

    public Task<SetupEnvironment> InspectAsync(CancellationToken cancellationToken) =>
        Task.FromResult(new SetupEnvironment(Problems, TrackerReachable, Origin));

    public SkillLook FindSkill(string name) => new(Skills?.Contains(name), $".claude/skills/{name}/SKILL.md");
}

// The boards the user reaches, as a test sets them.
sealed class FakeBoards : ISetupBoards
{
    public SetupBoards Read { get; set; } = SetupBoards.None;

    // The repository the boards were read for; null when they were not read.
    public string? Repository { get; private set; }

    public Task<SetupBoards> ReadAsync(string? repository, CancellationToken cancellationToken)
    {
        Repository = repository;
        return Task.FromResult(Read);
    }
}

// A tracker that reads and makes boards and labels until a test says otherwise, and remembers what it made.
sealed class FakeSetupTracker : ISetupTracker
{
    public HashSet<string> Unreadable { get; } = [];

    public int Calls { get; private set; }

    public List<(SetupOwner Owner, string Title)> Made { get; } = [];

    public List<(string Board, string Repository)> Linked { get; } = [];

    public string? RefuseToMake { get; set; }

    public string? RefuseToLink { get; set; }

    public List<(string Repository, string Name)> Labelled { get; } = [];

    public string? RefuseToLabel { get; set; }

    // Forgets what was asked for and made, and keeps what it answers.
    public void Reset()
    {
        Calls = 0;
        Made.Clear();
        Linked.Clear();
        Labelled.Clear();
        StatusesOf.Clear();
    }

    public Task<string?> RefuseBoardAsync(string board, CancellationToken cancellationToken)
    {
        Calls++;
        return Task.FromResult(Unreadable.Contains(board) ? $"{board} cannot be read" : null);
    }

    // What every board calls its statuses; null is a board whose statuses cannot be read.
    public IReadOnlyList<string>? Statuses { get; set; } = ["Todo", "In Progress", "Done"];

    // The boards whose statuses were asked for.
    public List<string> StatusesOf { get; } = [];

    public Task<IReadOnlyList<string>?> ReadStatusesAsync(string board, CancellationToken cancellationToken)
    {
        StatusesOf.Add(board);
        return Task.FromResult(Statuses);
    }

    // The labels of the repository.
    public IReadOnlyList<string> Labels { get; set; } = ["bug", "draft", "feature", "interrupted", "manual"];

    public Task<IReadOnlyList<string>> ReadLabelsAsync(string repository, CancellationToken cancellationToken)
    {
        Calls++;
        return Task.FromResult(Labels);
    }

    public Task<string> CreateBoardAsync(SetupOwner owner, string title, CancellationToken cancellationToken)
    {
        if (RefuseToMake is not null)
            throw new TrackerException(RefuseToMake);
        Made.Add((owner, title));
        return Task.FromResult($"https://github.com/orgs/{owner.Login}/projects/42");
    }

    public Task LinkBoardAsync(string board, string repository, CancellationToken cancellationToken)
    {
        if (RefuseToLink is not null)
            throw new TrackerException(RefuseToLink);
        Linked.Add((board, repository));
        return Task.CompletedTask;
    }

    public Task CreateLabelAsync(string repository, string name, CancellationToken cancellationToken)
    {
        Calls++;
        if (RefuseToLabel is not null)
            throw new TrackerException(RefuseToLabel);
        Labelled.Add((repository, name));
        return Task.CompletedTask;
    }
}

// Keeps the settings in memory and shows them as one `key = value` line each: the settings of the setup and of the
// checks of the doctor.
sealed class FakeSettingsStore : ISettingsStore
{
    public ProjectSettings? Current { get; set; }

    public IReadOnlyList<SettingsError>? Errors { get; set; }

    public ProjectSettings? Saved { get; private set; }

    public string DisplayPath => ".toobusy/settings.toml";

    public SettingsValidation? Load() =>
        Errors is not null ? new SettingsValidation(null, Errors) : Current is null ? null : new SettingsValidation(Current, []);

    public SettingsPreview Preview(ProjectSettings settings) => new(Current is null ? "" : Render(Current), Render(settings));

    public void Save(ProjectSettings settings) => Saved = settings;

    // Forgets what was saved, and keeps the settings there are.
    public void Reset() => Saved = null;

    public static string Render(ProjectSettings settings) => string.Join('\n',
        $"board = {settings.Tracker.Board}",
        $"statuses = {string.Join(", ", settings.Tracker.Statuses.Names)}",
        $"blocking = {string.Join(", ", settings.Queue.Labels.Blocking)}",
        $"take = {string.Join(", ", settings.Queue.Labels.Take)}",
        $"manual = {settings.Queue.Labels.Manual}",
        $"interrupted = {settings.Queue.Labels.Interrupted}",
        $"skill = {settings.Session.Skill}",
        $"before = {settings.Commands.Before}",
        $"after = {settings.Commands.After}",
        $"branch = {settings.Workspace.Branch}",
        $"permission-mode = {settings.Assistant.PermissionMode}") + "\n";
}
