using TooBusy.Cli.Terminal;
using TooBusy.Core.Processes;
using TooBusy.Core.Run;

namespace TooBusy.Cli.Tests;

// What the tests of the commands share, each command in a class of its own: a project in a folder of the test,
// a home folder and a folder for the choices of the user, settings to write there, machines whose `gh` answers
// as a test needs, and the tool run over them with or without a terminal.
public abstract class CliAppTests : IDisposable
{
    protected const string Settings = """
        version = 1

        [tracker]
        type = "github"

        [queue.labels]
        blocking = ["manual"]
        take = []
        manual = "manual"
        interrupted = "interrupted"

        [assistant]
        type = "claude-code"
        """;

    // What the settings of a project that names a skill end with.
    protected const string SkillOfTheProject = "\n\n[session]\nskill = \"task-auto\"\n";

    // The settings of a project with a board, labels that let a task in, and a manual label of its own.
    protected const string SettingsWithABoard = """
        version = 1

        [tracker]
        type = "github"
        board = "https://github.com/orgs/acme/projects/1"

        [queue.labels]
        blocking = ["draft"]
        take = ["feature", "bug"]
        manual = "needs-owner"
        interrupted = "interrupted"

        [assistant]
        type = "claude-code"
        """;

    // The labels the settings name, as `gh` lists those of a repository.
    protected const string Labels = "manual\ninterrupted\n";

    private protected readonly DirectoryInfo folder = Directory.CreateTempSubdirectory("toobusy-cli-");

    // Where the choice of the user is kept, in place of the home folder.
    private protected readonly DirectoryInfo personal = Directory.CreateTempSubdirectory("toobusy-personal-");

    // What the command that runs now has written so far.
    StringWriter? shown;

    public void Dispose()
    {
        folder.Delete(recursive: true);
        personal.Delete(recursive: true);
        GC.SuppressFinalize(this);
    }

    // A task that a run works on, as its sign says it.
    protected static readonly RunSign Working = new(RunPhase.Working)
    {
        Task = 12,
        Title = "Export the orders as a file",
        Url = "https://github.com/acme/rocket/issues/12",
        Open = "claude attach 1a2b3c4d",
    };

    // The sign of a run that goes on in the project: the process of the test is the one that lives.
    protected void GoingRun(RunSign sign)
    {
        var file = new TooBusy.Infrastructure.Run.RunSignFile(TooBusy.Infrastructure.Run.LocalFolder.Ensure(folder.FullName));
        Assert.Null(file.Raise(sign));
    }

    protected void GitRepository() => folder.CreateSubdirectory(".git");

    // A skill of the project, where Claude Code reads it.
    protected void WriteSkill(string name) =>
        File.WriteAllText(Path.Combine(folder.CreateSubdirectory(Path.Combine(".claude", "skills", name)).FullName, "SKILL.md"), $"---\nname: {name}\n---\n");

    protected void WriteSettings(string text) =>
        File.WriteAllText(Path.Combine(folder.CreateSubdirectory(".toobusy").FullName, "settings.toml"), text);

    protected Task<(int Exit, string Output, string Error)> RunAsync(params string[] args) => RunFromAsync(folder.FullName, args);

    protected Task<(int Exit, string Output, string Error)> RunFromAsync(string from, params string[] args) => RunAsync(Context(null) with { Folder = from }, args);

    protected Task<(int Exit, string Output, string Error)> RunWithKeysAsync(Keys keys, params string[] args) => RunWithKeysAsync(keys, null, args);

    // Runs in a terminal of a hundred and twenty columns and thirty lines.
    protected Task<(int Exit, string Output, string Error)> RunWithKeysAsync(Keys keys, ScriptedProcesses? processes, params string[] args) =>
        RunAsync(Context(processes) with { Terminal = new TerminalDevice(Watched(keys).Read, () => (120, 30)) }, args);

    // Runs in a terminal that says whether a key is pressed, as the page of a run asks it: the run goes on by
    // itself there, and the keys are held back until it has got where the test waits for it.
    protected Task<(int Exit, string Output, string Error)> RunLiveAsync(Keys keys, ScriptedProcesses? processes, params string[] args) =>
        RunAsync(Context(processes) with { Terminal = new TerminalDevice(Watched(keys).Read, () => (120, 30)) { KeyWaiting = () => keys.Waiting } }, args);

    // The keys, told what the page of the command that runs shows now.
    protected Keys Watched(Keys keys)
    {
        keys.Shown = () => TestTerminal.TextOf(shown?.ToString() ?? "");
        return keys;
    }

    // The project lies right under the home folder, and the choices of the user are kept in a folder of the test.
    // A run does not make the test wait: its clock goes on by itself.
    protected CliContext Context(ScriptedProcesses? processes) => new(folder.FullName, TextWriter.Null, TextWriter.Null)
    {
        Home = folder.Parent!.FullName,
        Processes = processes,
        PersonalFolder = personal.FullName,
        Clock = new InstantClock(),
    };

    // Chooses everything a run needs: no milestone, the assistant's own model and the proposed effort.
    protected async Task ChooseAllAsync()
    {
        await RunAsync("milestone", "--none");
        await RunAsync("model", "--default");
        await RunAsync("effort", "high");
    }

    // What `toobusy milestone` prints without a terminal, and its exit code.
    protected async Task<(string Output, int Exit)> ShownAsync(ScriptedProcesses? processes)
    {
        var (exit, output, _) = await RunAsync(Context(processes), ["milestone"]);
        return (output, exit);
    }

    // What `gh` answers of the statuses of a board that has those GitHub gives a new one.
    protected const string NewBoard = "board\nstatus\tTodo\nstatus\tIn Progress\nstatus\tDone\n";

    // What `gh` answers of the boards of a user who can reach two, one of them linked to the repository.
    protected const string TwoBoards = "owner\tuser\tann\nlinked\thttps://github.com/orgs/acme/projects/1\tRocket\nboard\thttps://github.com/users/ann/projects/2\tGarden\n";

    // A machine with git and a logged-in `gh`: the project is cloned from acme/rocket, the user can make boards for
    // themselves, the repository has the given milestones and labels, and every board the given statuses.
    protected static ScriptedProcesses GitHub(string milestones, string labels = "", string statuses = NewBoard) => new((command, arguments) => new ProcessResult(ProcessStatus.Exited, 0, command switch
    {
        "git" => "https://github.com/acme/rocket.git\n",
        "gh" when arguments[0] == "label" => labels,
        "gh" when arguments[0] == "api" && arguments[1].StartsWith("repos/", StringComparison.Ordinal) => milestones,
        "gh" when arguments.Any(argument => argument.Contains("options { name }", StringComparison.Ordinal)) => statuses,
        "gh" when arguments[0] == "api" => "owner\tuser\tann\n",
        _ => "",
    }, ""));

    // A machine whose `gh` answers the request for the open tasks with these, in a project cloned from acme/rocket.
    protected static ScriptedProcesses Tracker(string tasks) => new((command, arguments) => new ProcessResult(ProcessStatus.Exited, 0, command switch
    {
        "git" => "https://github.com/acme/rocket.git\n",
        "gh" when arguments is ["api", "graphql", ..] => tasks,
        _ => "",
    }, ""));

    // Does something while a command is answered, and answers with nothing.
    protected static string Do(Action action)
    {
        action();
        return "";
    }

    // What is left in the terminal when the screen is closed.
    protected static string Report(string output) => output.Split(Screen.Leave)[^1];

    // `stopped` stops the tool from outside, as a signal does; the end of the test does where none is given.
    protected async Task<(int Exit, string Output, string Error)> RunAsync(CliContext context, string[] args, CancellationToken? stopped = null)
    {
        using var output = new StringWriter();
        using var error = new StringWriter();
        shown = output;
        var exit = await CliApp.RunAsync(args, context with { Output = output, Error = error }, stopped ?? TestContext.Current.CancellationToken);
        return (exit, output.ToString(), error.ToString());
    }

    // Whether the page has shown the text by now, so many times: what keys that are held back wait for.
    protected bool Shown(string text, int times = 1) => shown is not null && shown.ToString().Split(text).Length > times;
}
