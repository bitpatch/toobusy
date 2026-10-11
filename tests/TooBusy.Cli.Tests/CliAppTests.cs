using System.Text.RegularExpressions;
using TooBusy.Cli.Terminal;
using TooBusy.Core.Processes;
using TooBusy.Core.Queue;
using TooBusy.Core.Run;

namespace TooBusy.Cli.Tests;

public sealed class CliAppTests : IDisposable
{
    const string Settings = """
        version = 1

        [tracker]
        type = "github"

        [queue.labels]
        blocking = ["manual"]
        take = []
        owner = "manual"
        interrupted = "interrupted"

        [assistant]
        type = "claude-code"
        """;

    // What the settings of a project that names a skill end with.
    const string SkillOfTheProject = "\n\n[session]\nskill = \"task-auto\"\n";

    // The settings of a project with a board, labels that let a task in, and a label of the owner of its own.
    const string SettingsWithABoard = """
        version = 1

        [tracker]
        type = "github"
        board = "https://github.com/orgs/acme/projects/1"

        [queue.labels]
        blocking = ["draft"]
        take = ["feature", "bug"]
        owner = "needs-owner"
        interrupted = "interrupted"

        [assistant]
        type = "claude-code"
        """;

    // The open tasks of that project as `gh` gives them, among them one for every reason a task is held for.
    const string OpenTasks = "total\t11\n"
        + "7\tChoose the format of the export\thttps://github.com/acme/rocket/issues/7\tTodo\t\t\t\tfeature\tneeds-owner\n"
        + "9\tDraft of the new page\thttps://github.com/acme/rocket/issues/9\tTodo\t\t\t\tfeature\tdraft\n"
        + "10\tTidy the styles\thttps://github.com/acme/rocket/issues/10\tTodo\t\t\t\tchore\n"
        + "12\tExport the orders as a file\thttps://github.com/acme/rocket/issues/12\tTodo\t\t\t\tfeature\n"
        + "14\tRename the settings page\thttps://github.com/acme/rocket/issues/14\tIn Progress\t\t\t\tfeature\n"
        + "15\tFix the rounding of a discount\thttps://github.com/acme/rocket/issues/15\tTodo\t\t\t\tbug\n"
        + "16\tCount the visits\thttps://github.com/acme/rocket/issues/16\t\t\t\t\tfeature\n"
        + "20\tDeploy\thttps://github.com/acme/rocket/issues/20\tTodo\t12\t\t\tfeature\n"
        + "21\tWrite the export down\thttps://github.com/acme/rocket/issues/21\tTodo\t7\t\t\tfeature\n"
        + "22\tTranslate the manual\thttps://github.com/acme/rocket/issues/22\tTodo\t\t99\t\tfeature\n"
        + "71\tPaint the windows yellow\thttps://github.com/acme/rocket/issues/71\tTodo\t\t\t\tfeature\tinterrupted\n";

    // The labels the settings name, as `gh` lists those of a repository.
    const string Labels = "manual\ninterrupted\n";

    readonly DirectoryInfo folder = Directory.CreateTempSubdirectory("toobusy-cli-");

    // Where the choice of the user is kept, in place of the home folder.
    readonly DirectoryInfo personal = Directory.CreateTempSubdirectory("toobusy-personal-");

    // What the command that runs now has written so far.
    StringWriter? shown;

    public void Dispose()
    {
        folder.Delete(recursive: true);
        personal.Delete(recursive: true);
    }

    [Fact]
    public async Task VersionOptionPrintsTheVersion()
    {
        var (exit, output, _) = await RunAsync("--version");

        Assert.Equal(0, exit);
        Assert.Matches(@"^\d+\.\d+\.\d+(\.dev)?$", output.Trim());
    }

    [Fact]
    public async Task HelpOptionWorksOutsideAGitRepository()
    {
        var (exit, output, _) = await RunAsync("--help");

        Assert.Equal(0, exit);
        Assert.Contains("Usage:", output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task HelpOfACommandWorksOutsideAGitRepository()
    {
        var (exit, output, _) = await RunAsync("run", "--help");

        Assert.Equal(0, exit);
        Assert.Contains("--demo", output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AboutTellsHowARunWorksOutsideAGitRepository()
    {
        var (exit, output, error) = await RunAsync("about");

        Assert.Equal(0, exit);
        Assert.Equal("", error);
        Assert.StartsWith("# How toobusy works\n", output, StringComparison.Ordinal);
        Assert.Contains("No settings are read here, so its labels are not named below", output, StringComparison.Ordinal);
        Assert.Contains(Briefing.Task(new(12, "<the title of the task>", "<the address of the task>", [], BoardStatus.Todo, "Todo", [], []), new NewTasks("<the label of the owner>", [], "<the milestone the user works on>", "<the address of the board>"), interrupted: false), output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AboutNamesTheLabelsOfAProjectThatIsSetUp()
    {
        GitRepository();
        WriteSettings(Settings);

        var (exit, output, error) = await RunAsync("about");

        Assert.Equal(0, exit);
        Assert.Equal("", error);
        Assert.Contains("- A task with the label `manual` is not taken.\n- The project has no board", output, StringComparison.Ordinal);
        Assert.Contains("takes the `interrupted` label off a task that had it", output, StringComparison.Ordinal);
        Assert.DoesNotContain("No settings are read here", output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AboutNamesTheSkillOfAProjectThatHasOne()
    {
        GitRepository();
        WriteSettings(Settings + SkillOfTheProject);

        var (exit, output, _) = await RunAsync("about");

        Assert.Equal(0, exit);
        Assert.Contains("This project names the skill `task-auto`, so its first message begins with `/task-auto #12 <the title of the task>`, as shown below.", output, StringComparison.Ordinal);
        Assert.Contains("```text\n/task-auto #12 <the title of the task>\n\nYou are doing task #12 of this project without its owner.", output, StringComparison.Ordinal);

        WriteSettings(Settings);
        Assert.Contains("This project names none: its first message begins with the words of toobusy", (await RunAsync("about")).Output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AboutNamesTheCommandsOfAProjectAroundATask()
    {
        GitRepository();
        WriteSettings(Settings + "\n[task]\nbefore = \"scripts/dev.sh ports free\"\nafter = \"scripts/dev.sh server stop\"\n");

        var (exit, output, _) = await RunAsync("about");

        Assert.Equal(0, exit);
        Assert.Contains("This project runs `scripts/dev.sh ports free` before a task and `scripts/dev.sh server stop` after it.", output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ADemoOfARunRunsNoCommandOfTheProject()
    {
        GitRepository();
        var mark = Path.Combine(Path.GetTempPath(), $"toobusy-demo-{Guid.NewGuid():N}");
        WriteSettings(Settings + $"\n[task]\nbefore = \"touch '{mark}'; exit 1\"\nafter = \"touch '{mark}'\"\n");

        var (exit, output, _) = await RunAsync("run", "--demo", "--max", "1");

        Assert.Equal(0, exit);
        Assert.DoesNotContain("command", output, StringComparison.Ordinal);
        Assert.False(File.Exists(mark));
    }

    [Fact]
    public async Task AboutSpeaksOfNoLabelsWhereTheSettingsAreNotValid()
    {
        GitRepository();
        WriteSettings("version = 1\n");

        var (exit, output, _) = await RunAsync("about");

        Assert.Equal(0, exit);
        Assert.Contains("No settings are read here", output, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData]
    [InlineData("run")]
    [InlineData("run", "--demo")]
    [InlineData("queue")]
    [InlineData("queue", "--demo")]
    [InlineData("doctor")]
    public async Task OutsideAGitRepositoryACommandFails(params string[] args)
    {
        var (exit, output, error) = await RunAsync(args);

        Assert.Equal(2, exit);
        Assert.Equal("", output);
        Assert.Equal("toobusy: not inside a git repository" + Environment.NewLine, error);
    }

    [Theory]
    [InlineData]
    [InlineData("run")]
    [InlineData("queue")]
    [InlineData("queue", "--json")]
    public async Task WithoutSettingsTheProjectIsNotSetUp(params string[] args)
    {
        GitRepository();

        var (exit, output, error) = await RunAsync(args);

        Assert.Equal(2, exit);
        Assert.Equal("", output);
        Assert.Equal(
            "toobusy: this project is not set up yet." + Environment.NewLine
            + "Run `toobusy init` to set it up." + Environment.NewLine
            + "An assistant that sets a project up for its owner starts with `toobusy init --assist`." + Environment.NewLine,
            error);
    }

    [Fact]
    public async Task TheProjectIsFoundFromAFolderInsideIt()
    {
        GitRepository();
        WriteSettings(Settings);
        var inside = folder.CreateSubdirectory("src").CreateSubdirectory("deep");

        var (exit, output, _) = await RunFromAsync(inside.FullName, "run", "--demo");

        Assert.Equal(0, exit);
        Assert.Contains("Demo", output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task NoArgumentsPrintTheHelpInAProjectThatIsSetUp()
    {
        GitRepository();
        WriteSettings(Settings);

        var (exit, output, error) = await RunAsync();

        Assert.Equal(0, exit);
        Assert.Contains("Usage:", output, StringComparison.Ordinal);
        Assert.Equal("", error);
    }

    [Fact]
    public async Task UnknownArgumentIsAWrongCommandLine()
    {
        var (exit, _, error) = await RunAsync("--no-such-option");

        Assert.Equal(2, exit);
        Assert.Contains("--no-such-option", error, StringComparison.Ordinal);
    }

    [Fact]
    public async Task UnknownOptionOfACommandIsAWrongCommandLine()
    {
        GitRepository();
        WriteSettings(Settings);

        var (exit, _, error) = await RunAsync("run", "--no-such-option");

        Assert.Equal(2, exit);
        Assert.Contains("--no-such-option", error, StringComparison.Ordinal);
    }

    [Fact]
    public async Task DemoSaysThatNothingIsChanged()
    {
        GitRepository();
        WriteSettings(Settings);

        var (exit, output, _) = await RunAsync("run", "--demo");

        Assert.Equal(0, exit);
        Assert.StartsWith("Demo: nothing is changed." + Environment.NewLine + "✻ v0.1.0 (made up) · 6 tasks in the queue", output, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("toobusy: give `--task` or `--max`, not both", "--task", "3", "--max", "2")]
    [InlineData("toobusy: `--task` takes the number of a task, which is 1 or more", "--task", "0")]
    [InlineData("toobusy: `--max` takes a number of tasks, which is 1 or more", "--max", "0")]
    [InlineData("toobusy: `--max` takes a number of tasks, which is 1 or more", "--max=-2")]
    [InlineData("--task", "--task", "twelve")]
    [InlineData("--max", "--max")]
    public async Task ARunOfATaskAndOfANumberOfTasksTogetherOrWithANumberThatIsNotPositiveIsAWrongCommandLine(string said, params string[] options)
    {
        GitRepository();
        WriteSettings(Settings);

        var (exit, output, error) = await RunAsync(["run", "--demo", .. options]);

        Assert.Equal(2, exit);
        Assert.Contains(said, error, StringComparison.Ordinal);
        Assert.DoesNotContain("✻", output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ADemoOfARunOfOneTaskDoesThatTaskAndNoOther()
    {
        GitRepository();

        var (exit, output, _) = await RunAsync("run", "--demo", "--task", "101");

        // The task a run was killed over is not among the tasks of such a demo: its record would hold the run.
        Assert.Equal(0, exit);
        Assert.Equal(
            """
            Demo: nothing is changed.
            ✻ v0.1.0 (made up) · #101 and no other task · the assistant's own model · high effort
            → #101 Show the total of an order in its header — started · a new session with the skill task-auto · claude attach 4f2a101 (made up)
              #101 is In Progress on the board and assigned to the current user; the session is told that toobusy keeps its task
            ✔ #101 Show the total of an order in its header — done in 9 s
              #101 is closed, with the report of the session as its comment
            ‖ Stopped after #101, as you asked · 1 done
              Worked 9 s

            """.ReplaceLineEndings(),
            output);
    }

    [Fact]
    public async Task ADemoOfARunOfTheTaskARunWasKilledOverGoesOnWithItsSession()
    {
        GitRepository();

        var (exit, output, _) = await RunAsync("run", "--demo", "--task", "100");

        Assert.Equal(0, exit);
        Assert.Contains("→ #100 Rename the settings page — taken again · its session was stopped, and goes on", output, StringComparison.Ordinal);
        Assert.Contains("‖ Stopped after #100, as you asked · 1 done", output, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("105", "it waits for #102")]
    [InlineData("106", "it has the manual label")]
    [InlineData("107", "its status is In Progress")]
    [InlineData("999", "it is not among the open tasks of the milestone v0.1.0 (made up)")]
    public async Task ARunOfATaskThatARunWouldNotTakeStartsNothingAndFails(string number, string why)
    {
        GitRepository();

        var (exit, output, _) = await RunAsync("run", "--demo", "--task", number);

        Assert.Equal(1, exit);
        Assert.Equal(
            $"Demo: nothing is changed.{Environment.NewLine}✖ Stopped: #{number} is not taken: {why} · no task was started{Environment.NewLine}  Worked 0 s{Environment.NewLine}",
            output);
    }

    [Fact]
    public async Task ADemoOfARunOfANumberOfTasksStopsAfterThem()
    {
        GitRepository();

        var (exit, output, _) = await RunAsync("run", "--demo", "--max", "2");

        Assert.Equal(0, exit);
        Assert.Contains("✻ v0.1.0 (made up) · 6 tasks in the queue, 2 at most · the assistant's own model · high effort", output, StringComparison.Ordinal);
        Assert.Contains("✔ #100 Rename the settings page — done", output, StringComparison.Ordinal);
        Assert.Contains("✔ #101 Show the total of an order in its header — done", output, StringComparison.Ordinal);
        Assert.DoesNotContain("#102 Export the orders as a file —", output, StringComparison.Ordinal);
        Assert.EndsWith($"‖ Stopped after 2 tasks, as you asked · 2 done{Environment.NewLine}  Worked 15 s{Environment.NewLine}", output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task InATerminalTheTapeOfARunOfANumberOfTasksSaysWhatEndedIt()
    {
        GitRepository();

        var (exit, output, _) = await RunLiveAsync(new Keys(), null, "run", "--demo", "--max", "1");

        Assert.Equal(0, exit);
        Assert.Equal(
            [
                "",
                " ✔ #100 Rename the settings page  0:06",
                "",
                " 1 task in 6 s · 1 done",
                " ‖ Stopped after 1 task, as you asked",
                "Demo: the tasks and the sessions were made up, and nothing was changed.",
            ],
            Scroll.Read(output).Written.Skip(3));
    }

    [Fact]
    public async Task ARunOfOneTaskOfAProjectSaysThatTheNumberIsNoOpenTaskAndStartsNoSession()
    {
        GitRepository();
        WriteSettings(Settings);
        await ChooseAllAsync();
        var processes = new FakeProcesses((command, arguments) => new ProcessResult(ProcessStatus.Exited, 0, (command, arguments[0]) switch
        {
            ("git", _) when arguments.Contains("remote") => "https://github.com/acme/rocket.git\n",
            ("git", _) => "# branch.oid 1a2b\n# branch.ab +0 -0\n",
            ("gh", "api") => "total\t1\n12\tExport the data\thttps://github.com/acme/rocket/issues/12\t\t\t\t\tfeature\n",
            ("gh", "label") => Labels,
            _ => "",
        }, ""));

        var (exit, output, _) = await RunAsync(Context(processes) with { Home = personal.FullName }, ["run", "--task", "40"]);

        Assert.Equal(1, exit);
        Assert.Equal($"✖ Stopped: #40 is not taken: it is not among the open tasks · no task was started{Environment.NewLine}  Worked 0 s{Environment.NewLine}", output);
        Assert.DoesNotContain(processes.Asked, asked => asked.Command == "claude" && asked.Arguments[0] == "--bg");
        Assert.DoesNotContain(processes.Asked, asked => asked.Command == "gh" && asked.Arguments[0] == "issue");
    }

    [Fact]
    public async Task InATerminalARunIsATapeAndTheToolIsLeftWhenTheRunIsOver()
    {
        GitRepository();
        WriteSettings(Settings);

        // No key is pressed: the run goes on by itself, and so does the tool when the run is over.
        var (exit, output, _) = await RunLiveAsync(new Keys(), null, "run", "--demo");

        Assert.Equal(0, exit);

        // A run enters no screen of its own: its bar, its tasks and how they went stay in the history of the
        // terminal, with nothing of the task that was worked on and nothing of the menu.
        Assert.DoesNotContain("\u001b[?1049", output, StringComparison.Ordinal);
        var rows = Scroll.Read(output).Written;
        Assert.StartsWith($"  toobusy · ~{Path.DirectorySeparatorChar}{folder.Name} ", rows[1], StringComparison.Ordinal);
        Assert.EndsWith(" Running the queue · demo", rows[1], StringComparison.Ordinal);
        Assert.Equal(
            [
                "",
                " ✔ #100 Rename the settings page  0:06",
                " ✔ #101 Show the total of an order in its header  0:09",
                " ✔ #102 Export the orders as a file  0:21",
                " ◐ #103 Fix the rounding of a discount  0:10",
                " ✔ #104 Update the dependencies  0:16",
                " ✔ #105 Describe the export in the manual  0:07",
                "",
                " 6 tasks in 69 s · 5 done · 1 done in part",
                "Demo: the tasks and the sessions were made up, and nothing was changed.",
            ],
            rows.Skip(3));
        Assert.True(Scroll.Read(output).CursorShown);
        Assert.True(Scroll.Read(output).Wraps);
    }

    [Fact]
    public async Task CtrlCTwiceKillsARunAndTheTapeSaysWhatIsLeft()
    {
        GitRepository();
        WriteSettings(Settings);
        await ChooseAllAsync();

        // One open task, and a session of Claude Code that works on it for as long as it is let.
        var processes = new FakeProcesses((command, arguments) => new ProcessResult(ProcessStatus.Exited, 0, (command, arguments[0]) switch
        {
            ("git", _) when arguments.Contains("remote") => "https://github.com/acme/rocket.git\n",
            ("git", _) => "# branch.oid 1a2b\n# branch.ab +0 -0\n",
            ("gh", "api") => "total\t1\n12\tExport the data\thttps://github.com/acme/rocket/issues/12\t\t\t\t\tfeature\n",
            ("gh", "label") => Labels,
            ("gh", "issue") => "Write it as CSV.\n",
            ("claude", "--bg") => "backgrounded · abc12345 · #12 Export the data\n",
            ("claude", "agents") => """[{"id":"abc12345","sessionId":"abc12345-0000","kind":"background","state":"working","status":"busy","pid":1}]""",
            _ => "",
        }, ""));
        // The clock is the real one here: with a clock that makes nobody wait, a session that never ends would be
        // looked at without end while the keys are waited for.
        var context = Context(processes) with { Home = personal.FullName, Clock = new TooBusy.Infrastructure.Machine.SystemClock() };
        var transcript = TooBusy.Assistants.ClaudeCode.ClaudeFolders.Of(folder.FullName, "", personal.FullName, Environment.GetEnvironmentVariable).Transcript("abc12345-0000");
        Directory.CreateDirectory(Path.GetDirectoryName(transcript)!);
        File.WriteAllText(transcript, """{"type":"assistant","timestamp":"2099-01-01T09:00:00Z","message":{"model":"claude","content":[{"type":"tool_use","name":"Bash","input":{"command":"dotnet test"}}]}}""" + "\n");
        var keys = new Keys()
            .Hold(() => Shown("Bash: dotnet test")).Press(Keys.ControlC)
            .Hold(() => Shown("press ctrl+c again to stop the session and exit")).Press(Keys.ControlC);

        var (exit, output, error) = await RunAsync(context with { Terminal = new TerminalDevice(keys.Read, () => (120, 30)) { KeyWaiting = () => keys.Waiting } }, ["run"]);

        Assert.Equal("", error);
        Assert.Equal(130, exit);
        var rows = Scroll.Read(output).Rows.Where(row => row.Length > 0).ToList();
        Assert.Matches(@"^ ■ #12 Export the data  [\d:]+$", rows[1]);
        Assert.Matches(@"^ 1 task in .+ · 1 interrupted$", rows[2]);
        Assert.Equal(" ■ Killed: #12 stays In Progress, and the next run goes on with its session · claude attach abc12345", rows[3]);
        Assert.Equal(4, rows.Count);
        Assert.Contains(processes.Asked, asked => asked.Command == "claude" && asked.Arguments.Contains("stop"));

        // The task and its session are written down on the machine, and nowhere git sees: the next run goes on by them.
        using var record = System.Text.Json.JsonDocument.Parse(File.ReadAllText(Path.Combine(folder.FullName, ".toobusy", "local", "session.json")));
        Assert.Equal(
            (12, "abc12345", "abc12345-0000", "working"),
            (record.RootElement.GetProperty("task").GetInt32(), record.RootElement.GetProperty("session").GetString(), record.RootElement.GetProperty("conversation").GetString(), record.RootElement.GetProperty("state").GetString()));
    }

    [Fact]
    public async Task ARunDoesNotStartBeforeTheMilestoneIsChosen()
    {
        GitRepository();
        WriteSettings(Settings);

        var (exit, output, error) = await RunAsync(Context(GitHub(milestones: "", Labels)), ["run"]);

        Assert.Equal(1, exit);
        Assert.Equal("", output);
        Assert.Equal(
            "toobusy: the milestone to work on is not chosen." + Environment.NewLine + "Run `toobusy milestone` to choose it." + Environment.NewLine,
            error);
    }

    [Fact]
    public async Task ARunDoesNotStartWithAMilestoneThatIsNotOpenAnyMore()
    {
        GitRepository();
        WriteSettings(Settings);
        var processes = GitHub(milestones: "v2\t\t3\nv3\t\t0\n");
        await RunAsync(Context(processes), ["milestone", "v2"]);
        var later = GitHub(milestones: "v3\t\t0\n", Labels);

        var (exit, _, error) = await RunAsync(Context(later), ["run"]);

        Assert.Equal(1, exit);
        Assert.StartsWith("toobusy: the milestone “v2” is not open any more." + Environment.NewLine, error, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AProjectWithoutAGitHubOriginHasNoTasksToTake()
    {
        GitRepository();
        WriteSettings(Settings);
        await ChooseAllAsync();
        var processes = new FakeProcesses(command => new ProcessResult(ProcessStatus.Exited, 0, command == "git" ? "git@example.com:acme/rocket.git\n" : "", ""));

        var (exit, output, error) = await RunAsync(Context(processes), ["run"]);

        Assert.Equal(1, exit);
        Assert.Equal("", output);
        Assert.Equal(
            """
            ✘ repository         The `origin` remote is not a GitHub repository.
            toobusy: the run did not start. `toobusy doctor` checks all of it again.

            """.ReplaceLineEndings(),
            error);
    }

    [Fact]
    public async Task ARunDoesNotStartWhileACheckFailsAndTellsTheFailuresWithTheirFixes()
    {
        GitRepository();
        WriteSettings(Settings);
        await ChooseAllAsync();

        // Claude Code is there and says that it is not logged in; the repository lacks a label of the settings.
        var processes = new FakeProcesses((command, arguments) => (command, arguments[0]) switch
        {
            ("claude", "auth") => new ProcessResult(ProcessStatus.Exited, 1, """{"loggedIn": false}""", ""),
            ("git", _) => new ProcessResult(ProcessStatus.Exited, 0, "https://github.com/acme/rocket.git\n", ""),
            ("gh", "label") => new ProcessResult(ProcessStatus.Exited, 0, "manual\n", ""),
            _ => new ProcessResult(ProcessStatus.Exited, 0, "", ""),
        });

        var (exit, output, error) = await RunAsync(Context(processes), ["run"]);

        Assert.Equal(1, exit);
        Assert.Equal("", output);
        Assert.Equal(
            """
            ✘ Claude Code login  Claude Code is not logged in.
              fix: claude, then /login
            ✘ labels             acme/rocket has no label “interrupted”.
              fix: gh label create "interrupted" --repo acme/rocket
            toobusy: the run did not start. `toobusy doctor` checks all of it again.

            """.ReplaceLineEndings(),
            error);
        Assert.DoesNotContain(processes.Asked, asked => asked.Command == "claude" && asked.Arguments[0] == "--bg");
    }

    [Fact]
    public async Task InATerminalTheChecksBeforeARunLeaveNothingOnItWhenTheyPass()
    {
        GitRepository();
        WriteSettings(Settings);
        var keys = new Keys();

        var (exit, output, error) = await RunWithKeysAsync(keys, GitHub(milestones: "", Labels), "run");

        Assert.Equal(1, exit);
        Assert.StartsWith("toobusy: the milestone to work on is not chosen.", error, StringComparison.Ordinal);
        Assert.Contains("⣾ labels", output, StringComparison.Ordinal);
        Assert.All(Scroll.Read(output).Rows, row => Assert.Equal("", row));
    }

    [Fact]
    public async Task QueueTakesATaskOnlyFromTheStatusTheSettingsNameAndAboutNamesTheStatusesSo()
    {
        GitRepository();
        WriteSettings(SettingsWithABoard.Replace("[queue.labels]", "[tracker.statuses]\ntodo = \"Ready\"\ndone = \"Shipped\"\n\n[queue.labels]", StringComparison.Ordinal));
        await RunAsync("milestone", "--none");
        var processes = Tracker("total\t3\n"
            + "12\tExport the orders as a file\thttps://github.com/acme/rocket/issues/12\tready\t\t\t\tfeature\n"
            + "14\tRename the settings page\thttps://github.com/acme/rocket/issues/14\tTodo\t\t\t\tfeature\n"
            + "15\tFix the rounding of a discount\thttps://github.com/acme/rocket/issues/15\tShipped\t\t\t\tbug\n");

        var (exit, output, error) = await RunAsync(Context(processes), ["queue"]);

        Assert.Equal("", error);
        Assert.Equal(0, exit);
        Assert.Equal(
            """
            Ready
              #12 Export the orders as a file

            Later
              no tasks

            Held
              #14 Rename the settings page        its status is Todo
              #15 Fix the rounding of a discount  its status is Shipped

            """.ReplaceLineEndings(),
            output);

        (_, var about, _) = await RunAsync(Context(processes), ["about"]);
        Assert.Contains("a task is taken only from the status `Ready`. The statuses a run knows are named below as the settings of the project call them, `Ready`, `In Progress` and `Shipped`.", about, StringComparison.Ordinal);
        Assert.Contains("with the status `Ready`.", about, StringComparison.Ordinal);
    }

    [Fact]
    public async Task QueueShowsWhatARunTakesNowWhatOpensLaterAndWhatIsHeldWithEveryReason()
    {
        GitRepository();
        WriteSettings(SettingsWithABoard);
        await RunAsync("milestone", "--none");
        var processes = Tracker(OpenTasks);

        var (exit, output, error) = await RunAsync(Context(processes), ["queue"]);

        Assert.Equal("", error);
        Assert.Equal(0, exit);
        Assert.Equal(
            """
            Ready
              #71 Paint the windows yellow         interrupted
              #12 Export the orders as a file
              #15 Fix the rounding of a discount

            Later
              #20 Deploy                           waits for #12

            Held
              #7  Choose the format of the export  it waits for the owner: it has the needs-owner label
              #9  Draft of the new page            it has the draft label
              #10 Tidy the styles                  it has none of the labels to take: feature, bug
              #14 Rename the settings page         its status is In Progress
              #16 Count the visits                 it is not on the board
              #21 Write the export down            it waits for #7 (it waits for the owner: it has the needs-owner label)
              #22 Translate the manual             it waits for #99 (not among these tasks)

            """.ReplaceLineEndings(),
            output);

        // It reads the tasks and nothing else: no check of `doctor` is made, nothing is changed in the tracker,
        // nothing is asked of the assistant, and nothing is left on the machine. The model and the effort are not
        // chosen here, and it does not ask for them.
        Assert.Equal(["git", "gh"], processes.Asked.Select(asked => asked.Command));
        Assert.Equal(["api", "graphql"], processes.Asked[1].Arguments.Take(2));
        Assert.False(Directory.Exists(Path.Combine(folder.FullName, ".toobusy", "local")));
    }

    [Fact]
    public async Task QueueStartsWithTheTaskAnEarlierRunLeftWithASession()
    {
        GitRepository();
        WriteSettings(SettingsWithABoard);
        await RunAsync("milestone", "--none");

        // The record of a task that stands as one that is worked on: a run goes on with it whatever its status.
        var local = Directory.CreateDirectory(Path.Combine(folder.FullName, ".toobusy", "local"));
        var record = """{"task":14,"session":"abc12345","since":"2030-01-01T09:00:00.0000000Z","state":"working"}""";
        File.WriteAllText(Path.Combine(local.FullName, "session.json"), record);

        var (exit, output, _) = await RunAsync(Context(Tracker(OpenTasks)), ["queue"]);

        Assert.Equal(0, exit);
        Assert.StartsWith(
            """
            Ready
              #14 Rename the settings page         left with a session
              #71 Paint the windows yellow         interrupted
              #12 Export the orders as a file

            """.ReplaceLineEndings(),
            output,
            StringComparison.Ordinal);
        Assert.DoesNotContain("its status is In Progress", output, StringComparison.Ordinal);
        Assert.Equal(record, File.ReadAllText(Path.Combine(local.FullName, "session.json")));
        Assert.Equal(["session.json"], local.GetFiles().Select(file => file.Name));
    }

    [Fact]
    public async Task QueueDoesNotPutFirstATaskOfARecordThatARunWouldNotGoOnWith()
    {
        GitRepository();
        WriteSettings(SettingsWithABoard);
        await RunAsync("milestone", "--none");
        var local = Directory.CreateDirectory(Path.Combine(folder.FullName, ".toobusy", "local"));
        File.WriteAllText(Path.Combine(local.FullName, "session.json"), """{"task":9,"session":"abc12345","since":"2030-01-01T09:00:00.0000000Z","state":"working"}""");

        var (_, output, _) = await RunAsync(Context(Tracker(OpenTasks)), ["queue"]);

        Assert.DoesNotContain(QueueLook.Left, output, StringComparison.Ordinal);
        Assert.Contains("  #9  Draft of the new page            it has the draft label" + Environment.NewLine, output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task QueueAsJsonHasTheThreeListsWithWhatATaskWaitsForAndWhyItIsHeld()
    {
        GitRepository();
        WriteSettings(SettingsWithABoard);
        await RunAsync("milestone", "--none");
        var local = Directory.CreateDirectory(Path.Combine(folder.FullName, ".toobusy", "local"));
        File.WriteAllText(Path.Combine(local.FullName, "session.json"), """{"task":14,"session":"abc12345","since":"2030-01-01T09:00:00.0000000Z","state":"working"}""");

        var (exit, output, error) = await RunAsync(Context(Tracker(OpenTasks)), ["queue", "--json"]);

        Assert.Equal("", error);
        Assert.Equal(0, exit);
        using var queue = System.Text.Json.JsonDocument.Parse(output);
        var ready = queue.RootElement.GetProperty("ready").EnumerateArray().ToList();
        Assert.Equal([14, 71, 12, 15], ready.Select(task => task.GetProperty("number").GetInt32()));
        Assert.Equal(["session", "interrupted", null, null], ready.Select(task => task.TryGetProperty("first", out var first) ? first.GetString() : null));
        Assert.Equal("Paint the windows yellow", ready[1].GetProperty("title").GetString());
        Assert.Equal("https://github.com/acme/rocket/issues/71", ready[1].GetProperty("url").GetString());
        Assert.Equal(["feature", "interrupted"], ready[1].GetProperty("labels").EnumerateArray().Select(label => label.GetString()));

        var later = Assert.Single(queue.RootElement.GetProperty("later").EnumerateArray());
        Assert.Equal(20, later.GetProperty("number").GetInt32());
        Assert.Equal("Deploy", later.GetProperty("title").GetString());
        Assert.Equal("https://github.com/acme/rocket/issues/20", later.GetProperty("url").GetString());
        Assert.Equal(["feature"], later.GetProperty("labels").EnumerateArray().Select(label => label.GetString()));
        Assert.Equal([12], later.GetProperty("waitsFor").EnumerateArray().Select(number => number.GetInt32()));

        var held = queue.RootElement.GetProperty("held").EnumerateArray().ToList();
        Assert.Equal([7, 9, 10, 16, 21, 22], held.Select(task => task.GetProperty("number").GetInt32()));
        Assert.Equal("it waits for the owner: it has the needs-owner label", held[0].GetProperty("reason").GetString());
        Assert.Equal(["feature", "needs-owner"], held[0].GetProperty("labels").EnumerateArray().Select(label => label.GetString()));
        Assert.Equal("https://github.com/acme/rocket/issues/7", held[0].GetProperty("url").GetString());
    }

    [Fact]
    public async Task QueueSaysOfEveryListThatIsEmptyThatItHasNoTasks()
    {
        GitRepository();
        WriteSettings(Settings);
        await RunAsync("milestone", "--none");

        var (exit, output, _) = await RunAsync(Context(Tracker("total\t0\n")), ["queue"]);

        Assert.Equal(0, exit);
        Assert.Equal(
            """
            Ready
              no tasks

            Later
              no tasks

            Held
              no tasks

            """.ReplaceLineEndings(),
            output);

        var (_, json, _) = await RunAsync(Context(Tracker("total\t0\n")), ["queue", "--json"]);

        using var queue = System.Text.Json.JsonDocument.Parse(json);
        Assert.Equal([0, 0, 0], queue.RootElement.EnumerateObject().Select(list => list.Value.GetArrayLength()));
        Assert.Equal(["ready", "later", "held"], queue.RootElement.EnumerateObject().Select(list => list.Name));
    }

    [Fact]
    public async Task QueuePaintsTheNamesOfTheListsTheNumbersAndWhatIsSaidOfATask()
    {
        GitRepository();
        WriteSettings(SettingsWithABoard);
        await RunAsync("milestone", "--none");
        var palette = Palette.Dark;

        var (_, output, _) = await RunAsync(Context(Tracker(OpenTasks)) with { OutputPalette = palette }, ["queue"]);

        Assert.StartsWith(palette.Paint(Tone.Strong, "Ready") + Environment.NewLine, output, StringComparison.Ordinal);
        Assert.Contains($"  {palette.Muted("#71")} Paint the windows yellow         {palette.Warning("interrupted")}" + Environment.NewLine, output, StringComparison.Ordinal);
        Assert.Contains($"  {palette.Muted("#12")} Export the orders as a file" + Environment.NewLine, output, StringComparison.Ordinal);
        Assert.Contains($"  {palette.Muted("#20")} Deploy                           {palette.Muted("waits for #12")}" + Environment.NewLine, output, StringComparison.Ordinal);
        Assert.Contains($"  {palette.Muted("#9 ")} Draft of the new page            {palette.Muted("it has the draft label")}" + Environment.NewLine, output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task QueueCutsATitleThatIsTooLongAndKeepsItWholeInJson()
    {
        GitRepository();
        WriteSettings(Settings);
        await RunAsync("milestone", "--none");
        var title = new string('a', 70);
        var tasks = $"total\t1\n12\t{title}\thttps://github.com/acme/rocket/issues/12\t\t\t\t\tmanual\n";

        var (_, output, _) = await RunAsync(Context(Tracker(tasks)), ["queue"]);
        var (_, json, _) = await RunAsync(Context(Tracker(tasks)), ["queue", "--json"]);

        Assert.Contains($"  #12 {new string('a', 59)}…  it has the manual label" + Environment.NewLine, output, StringComparison.Ordinal);
        Assert.Contains($"\"title\": \"{title}\"", json, StringComparison.Ordinal);
    }

    [Fact]
    public async Task QueueOfMoreThanAHundredOpenTasksIsTheErrorItIsForARun()
    {
        GitRepository();
        WriteSettings(Settings);
        await RunAsync("milestone", "--none");

        var (exit, output, error) = await RunAsync(Context(Tracker("total\t101\n")), ["queue"]);

        Assert.Equal(1, exit);
        Assert.Equal("", output);
        Assert.Equal("toobusy: acme/rocket has 101 open tasks, and the queue reads only the first 100." + Environment.NewLine, error);
    }

    [Fact]
    public async Task QueueSaysWhatTheTrackerSaysWhenTheTasksCannotBeRead()
    {
        GitRepository();
        WriteSettings(Settings);
        await RunAsync("milestone", "--none");
        var processes = new FakeProcesses(command => command == "git"
            ? new ProcessResult(ProcessStatus.Exited, 0, "https://github.com/acme/rocket.git\n", "")
            : new ProcessResult(ProcessStatus.Exited, 1, "", "HTTP 401: Bad credentials\n"));

        var (exit, output, error) = await RunAsync(Context(processes), ["queue", "--json"]);

        Assert.Equal(1, exit);
        Assert.Equal("", output);
        Assert.Equal("toobusy: The tasks of acme/rocket cannot be read. HTTP 401: Bad credentials" + Environment.NewLine, error);
    }

    [Fact]
    public async Task QueueIsNotShownBeforeTheMilestoneIsChosen()
    {
        GitRepository();
        WriteSettings(Settings);

        var (exit, output, error) = await RunAsync(Context(Tracker(OpenTasks)), ["queue"]);

        Assert.Equal(1, exit);
        Assert.Equal("", output);
        Assert.Equal(
            "toobusy: the milestone to work on is not chosen." + Environment.NewLine + "Run `toobusy milestone` to choose it." + Environment.NewLine,
            error);
    }

    [Fact]
    public async Task QueueIsNotShownForAMilestoneThatIsNotOpenAnyMore()
    {
        GitRepository();
        WriteSettings(Settings);
        await RunAsync(Context(GitHub(milestones: "v2\t\t3\nv3\t\t0\n")), ["milestone", "v2"]);

        var (exit, output, error) = await RunAsync(Context(GitHub(milestones: "v3\t\t0\n", Labels)), ["queue"]);

        Assert.Equal(1, exit);
        Assert.Equal("", output);
        Assert.Equal(
            "toobusy: the milestone “v2” is not open any more." + Environment.NewLine + "Run `toobusy milestone` to choose it." + Environment.NewLine,
            error);
    }

    [Fact]
    public async Task QueueRefusesSettingsThatAreNotValid()
    {
        GitRepository();
        WriteSettings(Settings.Replace("blocking = [\"manual\"]", "blocking = \"manual\"", StringComparison.Ordinal));
        await RunAsync("milestone", "--none");

        var (exit, output, error) = await RunAsync(Context(Tracker(OpenTasks)), ["queue"]);

        Assert.Equal(1, exit);
        Assert.Equal("", output);
        Assert.StartsWith("toobusy: the settings of this project are not valid." + Environment.NewLine + ".toobusy/settings.toml:7: queue.labels.blocking: ", error, StringComparison.Ordinal);
    }

    [Fact]
    public async Task QueueOfAProjectWithoutAGitHubOriginHasNoTasks()
    {
        GitRepository();
        WriteSettings(Settings);
        await RunAsync("milestone", "--none");
        var processes = new FakeProcesses(command => new ProcessResult(ProcessStatus.Exited, 0, command == "git" ? "git@example.com:acme/rocket.git\n" : "", ""));

        var (exit, output, error) = await RunAsync(Context(processes), ["queue"]);

        Assert.Equal(1, exit);
        Assert.Equal("", output);
        Assert.Equal("toobusy: the `origin` remote is not a GitHub repository, so there are no tasks to take" + Environment.NewLine, error);
    }

    [Fact]
    public async Task QueueOfADemoShowsTheMadeUpTasksOfADemoOfARun()
    {
        GitRepository();

        var (exit, output, error) = await RunAsync("queue", "--demo");

        Assert.Equal("", error);
        Assert.Equal(0, exit);
        Assert.StartsWith(
            "Ready" + Environment.NewLine + "  #100 Rename the settings page                  left with a session" + Environment.NewLine + "  #101 Show the total of an order in its header" + Environment.NewLine,
            output,
            StringComparison.Ordinal);
        Assert.Contains("Later" + Environment.NewLine + "  #105 Describe the export in the manual         waits for #102" + Environment.NewLine, output, StringComparison.Ordinal);
        Assert.Contains("Held" + Environment.NewLine + "  #106 Move the site to the new hosting", output, StringComparison.Ordinal);
        Assert.EndsWith(Environment.NewLine + "Demo: the tasks are made up." + Environment.NewLine, output, StringComparison.Ordinal);
        Assert.False(Directory.Exists(Path.Combine(folder.FullName, ".toobusy")));

        // What an assistant reads has nothing but the lists.
        var (_, json, _) = await RunAsync("queue", "--demo", "--json");

        using var queue = System.Text.Json.JsonDocument.Parse(json);
        Assert.Equal(100, queue.RootElement.GetProperty("ready")[0].GetProperty("number").GetInt32());
        Assert.Equal("session", queue.RootElement.GetProperty("ready")[0].GetProperty("first").GetString());
    }

    [Fact]
    public async Task DoctorTellsEveryCheckOfAProjectThatIsReady()
    {
        GitRepository();
        WriteSettings(Settings.Replace("type = \"github\"", "type = \"github\"\nboard = \"https://github.com/orgs/acme/projects/1\"", StringComparison.Ordinal));
        var processes = GitHub(milestones: "", Labels);

        var (exit, output, error) = await RunAsync(Context(processes), ["doctor"]);

        Assert.Equal(0, exit);
        Assert.Equal("", error);
        Assert.Equal(
            """
            ✔ git
            ✔ GitHub CLI
            ✔ GitHub login
            ✔ Claude Code
            ✔ Claude Code login
            ✔ settings           .toobusy/settings.toml
            ✔ repository         acme/rocket
            ✔ board              https://github.com/orgs/acme/projects/1
            ✔ statuses           Todo, In Progress, Done
            ✔ labels
            ○ skill              the project names no skill

            """.ReplaceLineEndings(),
            output);
        Assert.Contains(processes.Asked, asked => asked.Command == "claude" && asked.Arguments.SequenceEqual(["auth", "status"]));
        Assert.Contains(processes.Asked, asked => asked.Command == "gh" && asked.Arguments.SequenceEqual(["api", "repos/acme/rocket", "--jq", ".full_name"]));
    }

    [Fact]
    public async Task DoctorLooksForTheSkillOfTheSettingsAmongTheSkillsOfTheProject()
    {
        GitRepository();
        WriteSettings(Settings + SkillOfTheProject);
        var processes = GitHub(milestones: "", Labels);
        var place = Path.Combine(".claude", "skills", "task-auto", "SKILL.md");

        var (exit, output, _) = await RunAsync(Context(processes), ["doctor"]);

        Assert.Equal(1, exit);
        Assert.EndsWith(
            "✘ skill              There is no skill “task-auto”: a session that starts with it would do nothing." + Environment.NewLine
            + $"  fix: write {place}, as `toobusy about` tells, or name no skill: toobusy init --no-skill" + Environment.NewLine,
            output,
            StringComparison.Ordinal);

        WriteSkill("task-auto");
        (exit, output, _) = await RunAsync(Context(processes), ["doctor"]);

        Assert.Equal(0, exit);
        Assert.EndsWith($"✔ skill              {place}" + Environment.NewLine, output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task DoctorFailsOverABoardThatLacksAStatusOfARunAndARunDoesNotStart()
    {
        GitRepository();
        WriteSettings(Settings.Replace("type = \"github\"", "type = \"github\"\nboard = \"https://github.com/orgs/acme/projects/1\"", StringComparison.Ordinal));
        await ChooseAllAsync();
        var processes = GitHubWithProjects(Labels, statuses: "board\nstatus\tBacklog\nstatus\tReady\nstatus\tDone\n");
        var failure =
            "✘ statuses           The board has no statuses “Todo”, “In Progress”: it has Backlog, Ready, Done." + Environment.NewLine
            + "  fix: say which status of the board is which: toobusy init, or work without a board: toobusy init --no-board" + Environment.NewLine;

        var (exit, output, _) = await RunAsync(Context(processes), ["doctor"]);

        Assert.Equal(1, exit);
        Assert.Contains("✔ board              https://github.com/orgs/acme/projects/1" + Environment.NewLine + failure + "✔ labels" + Environment.NewLine, output, StringComparison.Ordinal);

        (exit, output, var error) = await RunAsync(Context(processes), ["run"]);

        Assert.Equal(1, exit);
        Assert.Equal("", output);
        Assert.Equal(failure + "toobusy: the run did not start. `toobusy doctor` checks all of it again." + Environment.NewLine, error);
        Assert.DoesNotContain(processes.Asked, asked => asked.Command == "claude" && asked.Arguments[0] == "--bg");
    }

    [Fact]
    public async Task DoctorFailsOverABoardWithoutTheStatusField()
    {
        GitRepository();
        WriteSettings(Settings.Replace("type = \"github\"", "type = \"github\"\nboard = \"https://github.com/orgs/acme/projects/1\"", StringComparison.Ordinal));

        var (exit, output, _) = await RunAsync(Context(GitHubWithProjects(Labels, statuses: "board\n")), ["doctor"]);

        Assert.Equal(1, exit);
        Assert.Contains("✘ statuses           The board has no statuses “Todo”, “In Progress”, “Done”: it has no statuses." + Environment.NewLine, output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ARunDoesNotStartWhileTheSkillOfTheSettingsIsNotWritten()
    {
        GitRepository();
        WriteSettings(Settings + SkillOfTheProject);
        await ChooseAllAsync();
        var processes = GitHub(milestones: "", Labels);

        var (exit, output, error) = await RunAsync(Context(processes), ["run"]);

        Assert.Equal(1, exit);
        Assert.Equal("", output);
        Assert.StartsWith("✘ skill              There is no skill “task-auto”: a session that starts with it would do nothing.", error, StringComparison.Ordinal);
        Assert.EndsWith("toobusy: the run did not start. `toobusy doctor` checks all of it again." + Environment.NewLine, error, StringComparison.Ordinal);
        Assert.DoesNotContain(processes.Asked, asked => asked.Command == "claude" && asked.Arguments[0] == "--bg");
    }

    [Fact]
    public async Task InAProjectWithASkillClaudeCodeIsStartedWithItsCommandAtTheHeadOfTheMessage()
    {
        GitRepository();
        WriteSettings(Settings + SkillOfTheProject);
        WriteSkill("task-auto");
        await ChooseAllAsync();

        // The files of the skill are not committed in this test; `git` says that the working tree is clean.
        var closed = false;
        var processes = new FakeProcesses((command, arguments) => new ProcessResult(ProcessStatus.Exited, 0, (command, arguments[0]) switch
        {
            ("git", _) when arguments.Contains("remote") => "https://github.com/acme/rocket.git\n",
            ("git", _) => "# branch.oid 1a2b\n# branch.ab +0 -0\n",
            ("gh", "api") => closed ? "total\t0\n" : "total\t1\n12\tExport the data\thttps://github.com/acme/rocket/issues/12\t\t\t\t\tfeature\n",
            ("gh", "label") => Labels,
            ("gh", "issue") => Do(() => closed |= arguments[1] == "close"),
            ("claude", "--bg") => "backgrounded · abc12345 · #12 Export the data\n",
            ("claude", "agents") => """[{"id":"abc12345","sessionId":"abc12345-0000","kind":"background","state":"done","status":"idle","pid":1}]""",
            _ => "",
        }, ""));
        var context = Context(processes) with { Home = personal.FullName };
        var transcript = TooBusy.Assistants.ClaudeCode.ClaudeFolders.Of(folder.FullName, "", personal.FullName, Environment.GetEnvironmentVariable).Transcript("abc12345-0000");
        Directory.CreateDirectory(Path.GetDirectoryName(transcript)!);
        File.WriteAllText(transcript, """{"type":"assistant","timestamp":"2030-01-01T09:00:00Z","message":{"model":"claude","content":[{"type":"text","text":"The export is written.\nTOOBUSY: done"}]}}""" + "\n");

        var (exit, output, error) = await RunAsync(context, ["run"]);

        Assert.Equal("", error);
        Assert.Equal(0, exit);
        Assert.Contains("→ #12 Export the data — started · a new session with the skill task-auto · claude attach abc12345", output, StringComparison.Ordinal);
        var started = processes.Asked.Single(asked => asked.Command == "claude" && asked.Arguments[0] == "--bg").Arguments;
        Assert.Equal(
            "/task-auto #12 Export the data\n\n"
            + Briefing.Task(new(12, "Export the data", "https://github.com/acme/rocket/issues/12", ["feature"], BoardStatus.Todo, "Todo", [], []), new NewTasks("manual", [], null, null), interrupted: false),
            started[^1]);
    }

    [Fact]
    public async Task DoctorOnAMachineWithNothingFailsEveryToolAndSkipsWhatWaitsForThem()
    {
        GitRepository();

        var (exit, output, error) = await RunAsync("doctor");

        Assert.Equal(1, exit);
        Assert.Equal("", error);
        var lines = output.ReplaceLineEndings("\n").Split('\n');
        Assert.Equal("✘ git                `git` is not installed.", lines[0]);
        Assert.StartsWith("  fix: ", lines[1], StringComparison.Ordinal);
        Assert.Equal("✘ GitHub CLI         The GitHub command-line tool `gh` is not installed.", lines[2]);
        Assert.Equal("○ GitHub login       waits for the GitHub CLI", lines[4]);
        Assert.Equal("✘ Claude Code        Claude Code is not installed: `claude` is not on the path.", lines[5]);
        Assert.Equal("○ Claude Code login  waits for Claude Code", lines[7]);
        Assert.Equal(
            [
                "✘ settings           This project is not set up yet.",
                "  fix: toobusy init",
                "○ repository         waits for git",
                "○ board              waits for the settings",
                "○ statuses           waits for the settings",
                "○ labels             waits for the settings",
                "○ skill              waits for the settings",
                "",
            ],
            lines[8..]);
    }

    [Fact]
    public async Task DoctorTellsWhatIsWrongWithSettingsThatDoNotValidate()
    {
        GitRepository();
        WriteSettings("version = 2\n");

        var (exit, output, _) = await RunAsync(Context(GitHub(milestones: "")), ["doctor"]);

        Assert.Equal(1, exit);
        Assert.Contains("✘ settings           .toobusy/settings.toml does not validate:" + Environment.NewLine + "  .toobusy/settings.toml:1: version: ", output, StringComparison.Ordinal);
        Assert.Contains("✔ repository         acme/rocket", output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task DoctorPaintsTheMarksAndWhatIsWrong()
    {
        GitRepository();
        var palette = Palette.Dark;

        var (_, output, _) = await RunAsync(Context(GitHub(milestones: "")) with { OutputPalette = palette }, ["doctor"]);

        Assert.Contains($"{palette.Success("✔")} git" + Environment.NewLine, output, StringComparison.Ordinal);
        Assert.Contains($"{palette.Error("✘")} settings           {palette.Error("This project is not set up yet.")}" + Environment.NewLine, output, StringComparison.Ordinal);
        Assert.Contains($"  {palette.Muted("fix: toobusy init")}" + Environment.NewLine, output, StringComparison.Ordinal);
        Assert.Contains($"{palette.Muted("○")} board              {palette.Muted("waits for the settings")}" + Environment.NewLine, output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task InATerminalDoctorShowsTheCheckThatRunsAndPutsItsResultInItsPlace()
    {
        GitRepository();
        WriteSettings(Settings);

        var (exit, output, _) = await RunWithKeysAsync(new Keys(), GitHub(milestones: "", Labels), "doctor");

        Assert.Equal(0, exit);
        Assert.Contains("\r⣾ GitHub login\r\u001b[K✔ GitHub login" + Environment.NewLine, output, StringComparison.Ordinal);
        Assert.Equal(
            ["✔ git", "✔ GitHub CLI", "✔ GitHub login", "✔ Claude Code", "✔ Claude Code login", "✔ settings           .toobusy/settings.toml", "✔ repository         acme/rocket", "○ board              the project has no board", "○ statuses           the project has no board", "✔ labels", "○ skill              the project names no skill"],
            Scroll.Read(output).Rows.Where(row => row.Length > 0));
    }

    [Fact]
    public async Task DoctorOfADemoChecksAMadeUpMachineThatHasItAll()
    {
        GitRepository();

        var (exit, output, _) = await RunAsync("doctor", "--demo");

        Assert.Equal(0, exit);
        Assert.DoesNotContain("✘", output, StringComparison.Ordinal);
        Assert.Contains("✔ labels" + Environment.NewLine + "✔ skill              .claude/skills/task-auto/SKILL.md" + Environment.NewLine + "Demo: the checks were made up." + Environment.NewLine, output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task WithoutATerminalARunDoesTheTasksAndPrintsItsLog()
    {
        GitRepository();
        WriteSettings(Settings);
        await ChooseAllAsync();

        // One open task; Claude Code starts a session for it, which has ended its turn when it is looked at, and
        // its conversation says that the task is done.
        var closed = false;
        var processes = new FakeProcesses((command, arguments) => new ProcessResult(ProcessStatus.Exited, 0, (command, arguments[0]) switch
        {
            ("git", _) when arguments.Contains("remote") => "https://github.com/acme/rocket.git\n",
            ("git", _) => "# branch.oid 1a2b\n# branch.ab +0 -0\n",
            ("gh", "api") => closed ? "total\t0\n" : "total\t1\n12\tExport the data\thttps://github.com/acme/rocket/issues/12\t\t\t\t\tfeature\n",
            ("gh", "label") => Labels,
            ("gh", "issue") => Do(() => closed |= arguments[1] == "close"),
            ("claude", "--bg") => "backgrounded · abc12345 · #12 Export the data\n",
            ("claude", "agents") => """[{"id":"abc12345","sessionId":"abc12345-0000","kind":"background","state":"done","status":"idle","pid":1}]""",
            _ => "",
        }, ""));
        var context = Context(processes) with { Home = personal.FullName };
        var transcript = TooBusy.Assistants.ClaudeCode.ClaudeFolders.Of(folder.FullName, "", personal.FullName, Environment.GetEnvironmentVariable).Transcript("abc12345-0000");
        Directory.CreateDirectory(Path.GetDirectoryName(transcript)!);
        File.WriteAllText(transcript, """{"type":"assistant","timestamp":"2030-01-01T09:00:00Z","message":{"model":"claude","content":[{"type":"text","text":"The export is written.\nTOOBUSY: done"}]}}""" + "\n");

        var (exit, output, error) = await RunAsync(context, ["run"]);

        Assert.Equal("", error);
        Assert.Equal(0, exit);
        Assert.Equal(
            """
            ✻ No milestone · 1 task in the queue · the assistant's own model · high effort
            → #12 Export the data — started · a new session · claude attach abc12345
              #12 is assigned to the current user; the session is told that toobusy keeps its task
            ✔ #12 Export the data — done in 0 s
              #12 is closed, with the report of the session as its comment
            ✔ No task is left · 1 done
              Worked 0 s

            """.ReplaceLineEndings(),
            output);

        var started = processes.Asked.Single(asked => asked.Command == "claude" && asked.Arguments[0] == "--bg").Arguments;
        Assert.Equal(["--effort", "high", "-n", "#12 Export the data"], started.Skip(7).Take(4));
        Assert.Equal(Briefing.Task(new(12, "Export the data", "https://github.com/acme/rocket/issues/12", ["feature"], BoardStatus.Todo, "Todo", [], []), new NewTasks("manual", [], null, null), interrupted: false), started[^1]);

        // The session reads its task on its own: the run asks the tracker for nothing of it.
        Assert.DoesNotContain(processes.Asked, asked => asked.Command == "gh" && asked.Arguments is ["issue", "view", ..]);
        Assert.Contains(processes.Asked, asked => asked.Command == "gh" && asked.Arguments.SequenceEqual(
            ["issue", "comment", "12", "--repo", "acme/rocket", "--body", "**Done.**\n\nThe export is written.\n\n_Session: `claude attach abc12345`_"]));
        Assert.Contains(processes.Asked, asked => asked.Command == "gh" && asked.Arguments.SequenceEqual(["issue", "close", "12", "--repo", "acme/rocket"]));

        // What a run leaves on the machine lies in a folder that keeps itself out of git.
        Assert.Equal("*\n", File.ReadAllText(Path.Combine(folder.FullName, ".toobusy", "local", ".gitignore")));
    }

    [Fact]
    public async Task InATerminalARunThatFindsNoTaskLeavesTheToolAtOnceAndOpensNoMenu()
    {
        GitRepository();
        WriteSettings(Settings);
        await ChooseAllAsync();
        var processes = new FakeProcesses((command, arguments) => new ProcessResult(ProcessStatus.Exited, 0, command switch
        {
            "git" when arguments.Contains("remote") => "https://github.com/acme/rocket.git\n",
            "gh" when arguments[0] == "label" => Labels,
            "gh" => "total\t0\n",
            _ => "",
        }, ""));

        var (exit, output, _) = await RunLiveAsync(new Keys(), processes, "run");

        Assert.Equal(0, exit);
        Assert.DoesNotContain("What to do", output, StringComparison.Ordinal);
        Assert.DoesNotContain("demo", output, StringComparison.Ordinal);
        Assert.DoesNotContain("\u001b[?1049", output, StringComparison.Ordinal);
        var rows = Scroll.Read(output).Written;
        Assert.EndsWith(" Running the queue", rows[1], StringComparison.Ordinal);
        Assert.Equal(["", " 0 tasks in 0 s"], rows.Skip(3));
    }

    [Fact]
    public async Task ARunDoesNotStartBeforeTheModelIsChosen()
    {
        GitRepository();
        WriteSettings(Settings);
        await RunAsync("milestone", "--none");
        await RunAsync("effort", "high");

        var (exit, output, error) = await RunAsync(Context(GitHub(milestones: "", Labels)), ["run"]);

        Assert.Equal(1, exit);
        Assert.Equal("", output);
        Assert.Equal(
            "toobusy: the model is not chosen." + Environment.NewLine + "Run `toobusy model` to choose it." + Environment.NewLine,
            error);
    }

    [Fact]
    public async Task ARunDoesNotStartBeforeTheEffortIsChosen()
    {
        GitRepository();
        WriteSettings(Settings);
        await RunAsync("milestone", "--none");
        await RunAsync("model", "--default");

        var (exit, output, error) = await RunAsync(Context(GitHub(milestones: "", Labels)), ["run"]);

        Assert.Equal(1, exit);
        Assert.Equal("", output);
        Assert.Equal(
            "toobusy: the effort is not chosen." + Environment.NewLine + "Run `toobusy effort` to choose it." + Environment.NewLine,
            error);
    }

    [Fact]
    public async Task AModelIsChosenByItsNameFromAScript()
    {
        GitRepository();

        var (exit, output, error) = await RunAsync("model", " claude-opus-4 ");

        Assert.Equal(0, exit);
        Assert.Equal("", error);
        Assert.Equal("Model: claude-opus-4" + Environment.NewLine, output);
        Assert.Equal("Model: claude-opus-4" + Environment.NewLine, (await RunAsync("model")).Output);
        Assert.Contains("model = \"claude-opus-4\"", File.ReadAllText(Path.Combine(personal.FullName, "projects.toml")), StringComparison.Ordinal);
    }

    [Fact]
    public async Task TheAssistantsOwnModelIsChosenFromAScriptToo()
    {
        GitRepository();

        var (exit, output, _) = await RunAsync("model", "--default");

        Assert.Equal(0, exit);
        Assert.Equal("Model: the assistant's own." + Environment.NewLine, output);
        Assert.Equal("Model: the assistant's own" + Environment.NewLine, (await RunAsync("model")).Output);
        Assert.Contains("model = \"\"", File.ReadAllText(Path.Combine(personal.FullName, "projects.toml")), StringComparison.Ordinal);
    }

    [Fact]
    public async Task ANameAndDefaultTogetherAreAWrongCommandLine()
    {
        GitRepository();

        var (exit, _, error) = await RunAsync("model", "opus", "--default");

        Assert.Equal(2, exit);
        Assert.Contains("not both", error, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AnEmptyModelNameIsRefused()
    {
        GitRepository();

        var (exit, output, error) = await RunAsync("model", " ");

        Assert.Equal(1, exit);
        Assert.Equal("", output);
        Assert.Equal("toobusy: the name of the model is empty" + Environment.NewLine, error);
        Assert.False(File.Exists(Path.Combine(personal.FullName, "projects.toml")));
    }

    [Fact]
    public async Task WithoutATerminalAModelThatIsNotChosenSaysHowToChooseIt()
    {
        GitRepository();

        var (exit, output, _) = await RunAsync("model");

        Assert.Equal(0, exit);
        Assert.Equal(
            "Model: not chosen" + Environment.NewLine + "Choose it with `toobusy model <name>` or `toobusy model --default`." + Environment.NewLine,
            output);
    }

    [Fact]
    public async Task InATerminalTheModelIsChosenFromAList()
    {
        GitRepository();

        var (exit, output, _) = await RunWithKeysAsync(new Keys().Press(Keys.Up, Keys.Enter).Type("claude-opus-4").Press(Keys.Enter), "model");

        Assert.Equal(0, exit);
        Assert.Contains("Choosing the model", output, StringComparison.Ordinal);
        Assert.Equal(
            $"toobusy · ~{Path.DirectorySeparatorChar}{folder.Name}{Environment.NewLine}✔ Model            claude-opus-4{Environment.NewLine}",
            Report(output));
        Assert.Equal("Model: claude-opus-4", (await RunAsync("model")).Output.Trim());
    }

    [Fact]
    public async Task LeavingTheQuestionAboutTheModelLeavesItAsItWas()
    {
        GitRepository();
        await RunAsync("model", "opus");

        var (exit, output, _) = await RunWithKeysAsync(new Keys().Press(Keys.Escape, Keys.Escape), "model");

        Assert.Equal(0, exit);
        Assert.EndsWith("The model is left as it was." + Environment.NewLine, Report(output), StringComparison.Ordinal);
        Assert.Equal("Model: opus", (await RunAsync("model")).Output.Trim());
    }

    [Fact]
    public async Task ADemoRemembersNoModelAndNoEffort()
    {
        GitRepository();

        var (exit, output, _) = await RunAsync("model", "opus", "--demo");
        var (_, effort, _) = await RunAsync("effort", "max", "--demo");
        var (_, page, _) = await RunWithKeysAsync(new Keys().Press(Keys.Enter), "effort", "--demo");

        Assert.Equal(0, exit);
        Assert.Equal("Model: opus" + Environment.NewLine, output);
        Assert.Equal("Effort: max" + Environment.NewLine, effort);
        Assert.EndsWith($"✔ Effort           high{Environment.NewLine}Demo: what was chosen is not remembered.{Environment.NewLine}", Report(page), StringComparison.Ordinal);
        Assert.False(File.Exists(Path.Combine(personal.FullName, "projects.toml")));
    }

    [Fact]
    public async Task AnEffortIsChosenByItsLevelFromAScript()
    {
        GitRepository();

        var (exit, output, error) = await RunAsync("effort", "XHigh");

        Assert.Equal(0, exit);
        Assert.Equal("", error);
        Assert.Equal("Effort: xhigh" + Environment.NewLine, output);
        Assert.Equal("Effort: xhigh" + Environment.NewLine, (await RunAsync("effort")).Output);
    }

    [Fact]
    public async Task AnEffortThatIsNotALevelIsRefusedWithTheLevels()
    {
        GitRepository();

        var (exit, output, error) = await RunAsync("effort", "ultra");

        Assert.Equal(1, exit);
        Assert.Equal("", output);
        Assert.Equal("toobusy: there is no effort “ultra”." + Environment.NewLine + "The levels: low, medium, high, xhigh, max" + Environment.NewLine, error);
    }

    [Fact]
    public async Task WithoutATerminalAnEffortThatIsNotChosenSaysHowToChooseIt()
    {
        GitRepository();

        var (exit, output, _) = await RunAsync("effort");

        Assert.Equal(0, exit);
        Assert.Equal(
            "Effort: not chosen" + Environment.NewLine + "Choose it with `toobusy effort <level>`: low, medium, high, xhigh, max." + Environment.NewLine,
            output);
    }

    [Fact]
    public async Task InATerminalTheEffortIsChosenFromAList()
    {
        GitRepository();

        var (exit, output, _) = await RunWithKeysAsync(new Keys().Press(Keys.Down, Keys.Enter), "effort");

        Assert.Equal(0, exit);
        Assert.Contains("❯ high", output, StringComparison.Ordinal);
        Assert.Equal(
            $"toobusy · ~{Path.DirectorySeparatorChar}{folder.Name}{Environment.NewLine}✔ Effort           xhigh{Environment.NewLine}",
            Report(output));
        Assert.Equal("Effort: xhigh", (await RunAsync("effort")).Output.Trim());
    }

    [Fact]
    public async Task LeavingTheQuestionAboutTheModelOnTheFirstRunLeavesThePage()
    {
        GitRepository();
        WriteSettings(Settings);
        await RunAsync("milestone", "--none");

        var (exit, output, _) = await RunWithKeysAsync(new Keys().Press(Keys.Escape, Keys.Escape), []);

        Assert.Equal(0, exit);
        Assert.Contains("The model the tasks are done with by default.", output, StringComparison.Ordinal);
        Assert.DoesNotContain("What to do", output, StringComparison.Ordinal);
        Assert.Equal("Model: not chosen", (await RunAsync("model")).Output.Split(Environment.NewLine)[0]);
    }

    [Fact]
    public async Task TheAssistantOfTheMenuChangesTheModelAndTheEffort()
    {
        GitRepository();
        WriteSettings(Settings);
        await ChooseAllAsync();

        // The assistant; the model: opus; the effort: one above; a look at the model that is left with Escape; `Back`
        // of the list, and out. On the first run there was nowhere to go back to, and here there is.
        var keys = new Keys().Press(
            Keys.Down, Keys.Enter,
            Keys.Enter, Keys.Down, Keys.Down, Keys.Enter,
            Keys.Down, Keys.Enter, Keys.Down, Keys.Enter,
            Keys.Up, Keys.Enter, Keys.Escape,
            Keys.Up, Keys.Enter, Keys.Up, Keys.Enter);

        var (exit, output, _) = await RunWithKeysAsync(keys, []);

        Assert.Equal(0, exit);
        Assert.Contains("Model   the assistant's own", output, StringComparison.Ordinal);
        Assert.Contains("Model   opus", output, StringComparison.Ordinal);
        Assert.Contains("Effort  xhigh", output, StringComparison.Ordinal);
        Assert.Contains("Assistant  opus · xhigh", output, StringComparison.Ordinal);
        Assert.Contains("  haiku\u001b[K\r\n   Other…", output, StringComparison.Ordinal);
        Assert.Contains("Back", output, StringComparison.Ordinal);
        Assert.Contains("The model is left as it was.", output, StringComparison.Ordinal);
        Assert.Equal(
            [
                $"toobusy · ~{Path.DirectorySeparatorChar}{folder.Name}",
                "✔ Model            opus",
                "✔ Effort           xhigh",
                "",
            ],
            Report(output).Split(Environment.NewLine));
        Assert.Equal("Model: opus", (await RunAsync("model")).Output.Trim());
        Assert.Equal("Effort: xhigh", (await RunAsync("effort")).Output.Trim());
    }

    [Fact]
    public async Task AMilestoneIsChosenByItsTitleFromAScript()
    {
        GitRepository();
        var processes = GitHub(milestones: "v0.2.0\t2030-01-15T08:00:00Z\t12\nBacklog\t\t1\n");

        var (exit, output, error) = await RunAsync(Context(processes), ["milestone", "V0.2.0"]);

        Assert.Equal(0, exit);
        Assert.Equal("", error);
        Assert.Equal("Milestone: v0.2.0 · due 2030-01-15 · 12 open tasks" + Environment.NewLine, output);
        Assert.Contains(processes.Asked, asked => asked.Command == "gh" && asked.Arguments[1] == "repos/acme/rocket/milestones?state=open&per_page=100");
        Assert.Equal(("Milestone: v0.2.0" + Environment.NewLine, 0), await ShownAsync(processes));
    }

    [Fact]
    public async Task AMilestoneThatIsNotOpenIsRefusedWithTheOpenOnes()
    {
        GitRepository();
        var processes = GitHub(milestones: "v0.3.0\t\t1\nv0.2.0\t\t2\n");

        var (exit, output, error) = await RunAsync(Context(processes), ["milestone", "v0.1.0"]);

        Assert.Equal(1, exit);
        Assert.Equal("", output);
        Assert.Equal("toobusy: there is no open milestone “v0.1.0”." + Environment.NewLine + "The open ones: v0.2.0, v0.3.0" + Environment.NewLine, error);
        Assert.Equal("Milestone: not chosen", (await ShownAsync(processes)).Output.Split(Environment.NewLine)[0]);
    }

    [Fact]
    public async Task WithoutMilestonesToReadATitleCannotBeChosen()
    {
        GitRepository();

        var (exit, _, error) = await RunAsync("milestone", "v1");

        Assert.Equal(1, exit);
        Assert.Equal("toobusy: the `origin` remote is not a GitHub repository, so there are no milestones to choose from" + Environment.NewLine, error);
    }

    [Fact]
    public async Task WorkingWithoutAMilestoneIsChosenFromAScriptToo()
    {
        GitRepository();

        var (exit, output, _) = await RunAsync("milestone", "--none");

        Assert.Equal(0, exit);
        Assert.Equal("Milestone: none. Tasks are taken whatever their milestone." + Environment.NewLine, output);
        Assert.Equal(("Milestone: no milestone" + Environment.NewLine, 0), await ShownAsync(null));
        Assert.Contains("milestone = \"\"", File.ReadAllText(Path.Combine(personal.FullName, "projects.toml")), StringComparison.Ordinal);
    }

    [Fact]
    public async Task ATitleAndNoneTogetherAreAWrongCommandLine()
    {
        GitRepository();

        var (exit, _, error) = await RunAsync("milestone", "v1", "--none");

        Assert.Equal(2, exit);
        Assert.Contains("not both", error, StringComparison.Ordinal);
    }

    [Fact]
    public async Task InATerminalTheMilestoneIsChosenFromAList()
    {
        GitRepository();
        var processes = GitHub(milestones: "v0.3.0\t\t1\nv0.2.0\t\t2\n");

        var (exit, output, _) = await RunWithKeysAsync(new Keys().Press(Keys.Down, Keys.Enter), processes, "milestone");

        Assert.Equal(0, exit);
        Assert.Contains("❯ v0.2.0", output, StringComparison.Ordinal);
        Assert.Equal(
            $"toobusy · ~{Path.DirectorySeparatorChar}{folder.Name}{Environment.NewLine}✔ Milestone        v0.3.0{Environment.NewLine}",
            Report(output));
        Assert.Equal("Milestone: v0.3.0", (await ShownAsync(processes)).Output.Trim());
    }

    [Fact]
    public async Task ADemoRemembersNoMilestone()
    {
        GitRepository();

        var (exit, output, _) = await RunAsync("milestone", "--none", "--demo");

        Assert.Equal(0, exit);
        Assert.StartsWith("Milestone: none.", output, StringComparison.Ordinal);
        Assert.False(File.Exists(Path.Combine(personal.FullName, "projects.toml")));
    }

    [Fact]
    public async Task WithoutACommandAProjectIsSetUpTheChoicesAreMadeAndTheMenuOpens()
    {
        GitRepository();
        var processes = GitHub(milestones: "v0.2.0\t\t2\n", labels: "bug\nmanual\n");

        // No project, no blocking labels, any label; a new label for the owner and one for interrupted tasks, each
        // by the name that is proposed; the skill that is proposed; yes; the first milestone; the model after the
        // assistant's own; the proposed effort; the last of the menu.
        var keys = new Keys().Press(
            Keys.Tab, Keys.Tab, Keys.Enter, Keys.Enter, Keys.Enter,
            Keys.Enter, Keys.Enter, Keys.Enter, Keys.Enter, Keys.Enter,
            Keys.Enter, Keys.Enter, Keys.Down, Keys.Enter, Keys.Enter, Keys.Up, Keys.Enter);

        var (exit, output, error) = await RunWithKeysAsync(keys, processes, []);

        Assert.Equal(0, exit);
        Assert.Equal("", error);
        Assert.Contains("What to do", output, StringComparison.Ordinal);
        Assert.Contains("Assistant  fable · high", output, StringComparison.Ordinal);
        Assert.Contains("Milestone  v0.2.0", output, StringComparison.Ordinal);
        Assert.Equal(
            [
                $"toobusy · ~{Path.DirectorySeparatorChar}{folder.Name}",
                "✔ Tracker          GitHub",
                "✔ Project          none",
                "✔ Blocking labels  none",
                "✔ Labels to take   any task",
                "✔ Owner's label    needs-owner",
                "✔ Interrupt label  interrupted",
                "✔ Skill            task-auto",
                "✔ Assistant        Claude Code",
                "✔ The settings are written to .toobusy/settings.toml. Commit the file.",
                "✔ Milestone        v0.2.0",
                "✔ Model            fable",
                "✔ Effort           high",
                "`toobusy about` tells how a run works, for you and for your assistant.",
                "",
            ],
            Report(output).Split(Environment.NewLine));
        Assert.Contains("type = \"github\"", File.ReadAllText(Path.Combine(folder.FullName, ".toobusy", "settings.toml")), StringComparison.Ordinal);
        Assert.Contains("The label “needs-owner” will be made in acme/rocket.", output, StringComparison.Ordinal);
        Assert.Contains("There is no skill “task-auto” yet: write .claude/skills/task-auto/SKILL.md before a run.", output, StringComparison.Ordinal);
        Assert.Contains("✔ Skill            task-auto", output, StringComparison.Ordinal);
        Assert.Contains("[session]\nskill = \"task-auto\"", File.ReadAllText(Path.Combine(folder.FullName, ".toobusy", "settings.toml")), StringComparison.Ordinal);
        Assert.Contains(processes.Asked, asked => asked.Command == "gh" && asked.Arguments.SequenceEqual(["label", "create", "needs-owner", "--repo", "acme/rocket"]));
        Assert.Equal("Milestone: v0.2.0", (await ShownAsync(processes)).Output.Trim());
        Assert.Equal("Model: fable", (await RunAsync("model")).Output.Trim());
        Assert.Equal("Effort: high", (await RunAsync("effort")).Output.Trim());
    }

    [Fact]
    public async Task WithoutACommandAProjectThatIsReadyOpensWithTheMenu()
    {
        GitRepository();
        WriteSettings(Settings);
        await ChooseAllAsync();

        // Settings, back out of them, then leave with Escape twice.
        var keys = new Keys().Press(Keys.Down, Keys.Down, Keys.Down, Keys.Enter, Keys.Escape, Keys.Escape, Keys.Escape);

        var (exit, output, _) = await RunWithKeysAsync(keys, []);

        Assert.Equal(0, exit);
        Assert.Contains("✔ Milestone        none: tasks are taken whatever their milestone", output, StringComparison.Ordinal);
        Assert.Contains("✔ Model            the assistant's own", output, StringComparison.Ordinal);
        Assert.Contains("✔ Effort           high", output, StringComparison.Ordinal);
        Assert.Contains("Assistant  own model · high", output, StringComparison.Ordinal);
        Assert.Contains("Setting up this project", output, StringComparison.Ordinal);
        Assert.Contains("The setup was left: nothing was changed.", output, StringComparison.Ordinal);
        Assert.Contains("press esc again to exit", output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RunOfTheMenuSaysHowManyTasksARunWouldTake()
    {
        GitRepository();
        WriteSettings(Settings);
        await ChooseAllAsync();

        // Three open tasks: one to take, one that opens after it, and one that a label holds.
        var processes = new FakeProcesses((command, arguments) => new ProcessResult(ProcessStatus.Exited, 0, command switch
        {
            "git" => "https://github.com/acme/rocket.git\n",
            "gh" when arguments[1] == "graphql" => "total\t3\n12\tExport the data\thttps://github.com/acme/rocket/issues/12\t\t\t\t\tfeature\n"
                + "13\tDescribe the export\thttps://github.com/acme/rocket/issues/13\t\t12\t\t\n14\tMove the site\thttps://github.com/acme/rocket/issues/14\t\t\t\t\tmanual\n",
            _ => "",
        }, ""));

        var (exit, output, _) = await RunWithKeysAsync(new Keys().Press(Keys.Up, Keys.Enter), processes, []);

        Assert.Equal(0, exit);
        Assert.Contains("❯ Run        2 tasks", output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RunOfTheMenuNamesTheTaskThatAnEarlierRunLeftWithASession()
    {
        GitRepository();
        WriteSettings(Settings);
        await ChooseAllAsync();

        // A record of the task that waits for another: a run goes on with it before any other, and it counts.
        var local = Directory.CreateDirectory(Path.Combine(folder.FullName, ".toobusy", "local"));
        File.WriteAllText(Path.Combine(local.FullName, "session.json"), """{"task":13,"session":"abc12345","since":"2030-01-01T09:00:00.0000000Z","state":"working"}""");
        var processes = new FakeProcesses((command, arguments) => new ProcessResult(ProcessStatus.Exited, 0, command switch
        {
            "git" => "https://github.com/acme/rocket.git\n",
            "gh" when arguments[1] == "graphql" => "total\t2\n12\tExport the data\thttps://github.com/acme/rocket/issues/12\t\t\t\t\tfeature\n"
                + "13\tDescribe the export\thttps://github.com/acme/rocket/issues/13\t\t12\t\t\n",
            _ => "",
        }, ""));

        var (exit, output, _) = await RunWithKeysAsync(new Keys().Press(Keys.Up, Keys.Enter), processes, []);

        Assert.Equal(0, exit);
        Assert.Contains("❯ Run        interrupted: #13 Describe the export", output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RunOfTheMenuOpensNoRunThatWouldTakeNoTask()
    {
        GitRepository();
        WriteSettings(Settings);
        await ChooseAllAsync();
        var processes = new FakeProcesses((command, _) => new ProcessResult(ProcessStatus.Exited, 0, command == "git" ? "https://github.com/acme/rocket.git\n" : "total\t0\n", ""));

        // Enter on `Run`, which counts again and opens nothing; then the last of the menu.
        var (exit, output, _) = await RunWithKeysAsync(new Keys().Press(Keys.Enter, Keys.Up, Keys.Enter), processes, []);

        Assert.Equal(0, exit);
        Assert.Contains("❯ Run        no tasks", output, StringComparison.Ordinal);
        Assert.DoesNotContain("Running the queue", output, StringComparison.Ordinal);
        Assert.Equal(2, processes.Asked.Count(asked => asked.Command == "gh" && asked.Arguments[1] == "graphql"));
    }

    [Fact]
    public async Task RunOfTheMenuOpensTheRunAfterTheChecksOfDoctor()
    {
        GitRepository();
        WriteSettings(Settings);
        await ChooseAllAsync();

        // One task for the menu to count; when the run reads the tasks there is none left, so it is over at once.
        var counted = false;
        var processes = new FakeProcesses((command, arguments) => new ProcessResult(ProcessStatus.Exited, 0, command switch
        {
            "git" when arguments.Contains("remote") => "https://github.com/acme/rocket.git\n",
            "gh" when arguments[0] == "label" => Labels,
            "gh" when arguments is ["api", "graphql", ..] && !counted => Do(() => counted = true) + "total\t1\n12\tExport the data\thttps://github.com/acme/rocket/issues/12\t\t\t\t\tfeature\n",
            "gh" => "total\t0\n",
            _ => "",
        }, ""));

        // `Run`; the menu is back when the run is over, and the last of it leaves.
        var keys = new Keys().Press(Keys.Enter).Hold(() => Shown("0 tasks in 0 s")).Press(Keys.Up, Keys.Enter);
        var (exit, output, error) = await RunLiveAsync(keys, processes, []);

        Assert.Equal(0, exit);
        Assert.Equal("", error);
        Assert.Contains("Running the queue", output, StringComparison.Ordinal);
        Assert.DoesNotContain("The run did not start.", output, StringComparison.Ordinal);

        // Every check was made, and before the run asked for its tasks.
        var asked = processes.Asked.Select(asked => $"{asked.Command} {asked.Arguments[0]}").ToList();
        Assert.Contains("claude auth", asked);
        Assert.Contains("gh label", asked);
        Assert.True(asked.IndexOf("gh label") < processes.Asked.FindLastIndex(asked => asked.Command == "gh" && asked.Arguments is ["api", "graphql", ..]));
    }

    [Fact]
    public async Task RunOfTheMenuOpensNoRunWhileACheckFailsAndSaysTheFailuresWithTheirFixesAboveTheMenu()
    {
        GitRepository();
        WriteSettings(Settings);
        await ChooseAllAsync();

        // A task to take; Claude Code says that it is not logged in, and the repository lacks a label of the settings.
        var processes = new FakeProcesses((command, arguments) => (command, arguments[0]) switch
        {
            ("claude", "auth") => new ProcessResult(ProcessStatus.Exited, 1, """{"loggedIn": false}""", ""),
            ("git", _) => new ProcessResult(ProcessStatus.Exited, 0, "https://github.com/acme/rocket.git\n", ""),
            ("gh", "label") => new ProcessResult(ProcessStatus.Exited, 0, "manual\n", ""),
            ("gh", "api") when arguments[1] == "graphql" => new ProcessResult(ProcessStatus.Exited, 0, "total\t1\n12\tExport the data\thttps://github.com/acme/rocket/issues/12\t\t\t\t\tfeature\n", ""),
            _ => new ProcessResult(ProcessStatus.Exited, 0, "", ""),
        });
        using var terminal = new TestTerminal(width: 120, height: 30);

        // `Run`, which opens nothing; then the last of the menu.
        terminal.Keys.Press(Keys.Enter, Keys.Up, Keys.Enter);
        var exit = await CliApp.RunAsync([], Context(processes) with { Output = terminal.Output, Terminal = terminal.Device }, TestContext.Current.CancellationToken);

        Assert.Equal(0, exit);
        terminal.AssertSaw("""
             ✘ Claude Code login  Claude Code is not logged in.
               fix: claude, then /login
             ✘ labels             acme/rocket has no label “interrupted”.
               fix: gh label create "interrupted" --repo acme/rocket
             The run did not start. `toobusy doctor` checks all of it again.
             What to do
             ❯ Run        1 task
            """.ReplaceLineEndings("\n").TrimEnd(' '));
        Assert.False(terminal.Saw("Running the queue"));
        Assert.DoesNotContain(processes.Asked, asked => asked.Command == "claude" && asked.Arguments[0] == "--bg");
    }

    [Fact]
    public async Task RunOfTheMenuOpensNothingOverABoardThatLacksAStatusOfARun()
    {
        GitRepository();
        WriteSettings(Settings.Replace("type = \"github\"", "type = \"github\"\nboard = \"https://github.com/orgs/acme/projects/1\"", StringComparison.Ordinal));
        await ChooseAllAsync();

        // A task in Todo to take, on a board that calls no status Done.
        var processes = new FakeProcesses((command, arguments) => new ProcessResult(ProcessStatus.Exited, 0, command switch
        {
            "git" => "https://github.com/acme/rocket.git\n",
            "gh" when arguments[0] == "label" => Labels,
            "gh" when arguments.Any(argument => argument.Contains("options { name }", StringComparison.Ordinal)) => "board\nstatus\tTodo\nstatus\tIn Progress\nstatus\tShipped\n",
            "gh" when arguments.Any(argument => argument.Contains("projectV2(number:", StringComparison.Ordinal)) => "P_1\n",
            "gh" when arguments is ["api", "graphql", ..] => "total\t1\n12\tExport the data\thttps://github.com/acme/rocket/issues/12\tTodo\t\t\t\tfeature\n",
            _ => "",
        }, ""));
        using var terminal = new TestTerminal(width: 120, height: 30);

        // `Run`, which opens nothing; then the last of the menu.
        terminal.Keys.Press(Keys.Enter, Keys.Up, Keys.Enter);
        var exit = await CliApp.RunAsync([], Context(processes) with { Output = terminal.Output, Terminal = terminal.Device }, TestContext.Current.CancellationToken);

        Assert.Equal(0, exit);
        terminal.AssertSaw("""
             ✘ statuses           The board has no status “Done”: it has Todo, In Progress, Shipped.
               fix: say which status of the board is which: toobusy init, or work without a board: toobusy init --no-board
             The run did not start. `toobusy doctor` checks all of it again.
             What to do
             ❯ Run        1 task
            """.ReplaceLineEndings("\n").TrimEnd(' '));
        Assert.False(terminal.Saw("Running the queue"));
        Assert.DoesNotContain(processes.Asked, asked => asked.Command == "claude" && asked.Arguments[0] == "--bg");
    }

    [Fact]
    public async Task BackOfTheSettingsGoesToTheMenuAsEscapeDoes()
    {
        GitRepository();
        WriteSettings(Settings);
        await ChooseAllAsync();

        // Settings, which open on the tab without a project; `Back` under it; the last of the menu.
        var keys = new Keys().Press(Keys.Down, Keys.Down, Keys.Down, Keys.Enter, Keys.Down, Keys.Enter, Keys.Up, Keys.Enter);

        var (exit, output, _) = await RunWithKeysAsync(keys, []);

        Assert.Equal(0, exit);
        Assert.Contains("❯ Back", output, StringComparison.Ordinal);
        Assert.Contains("The setup was left: nothing was changed.", output, StringComparison.Ordinal);
        Assert.DoesNotContain("press esc again to exit", output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ADemoShowsAProjectThatIsReadyWhereThereAreNoSettings()
    {
        GitRepository();

        // Straight to the menu, which names the task a run was killed over: the run, which goes on with it and
        // comes back to the menu by itself when it is over, and out.
        var keys = new Keys()
            .Hold(() => Shown("interrupted: #100 Rename the settings page")).Press(Keys.Enter)
            .Hold(() => Shown("What to do", times: 2)).Press(Keys.Up, Keys.Enter);

        var (exit, output, error) = await RunLiveAsync(keys, null, ["--demo"]);

        Assert.Equal(0, exit);
        Assert.Equal("", error);
        Assert.DoesNotContain("Write the settings?", output, StringComparison.Ordinal);
        Assert.Contains("✔ Project          https://github.com/users/example/projects/1", output, StringComparison.Ordinal);
        Assert.Contains("✔ Blocking labels  manual, draft", output, StringComparison.Ordinal);
        Assert.Contains("✔ Milestone        v0.1.0 (made up) · due 2030-01-01 · 3 open tasks", output, StringComparison.Ordinal);
        Assert.Contains("✔ Model            the assistant's own", output, StringComparison.Ordinal);
        Assert.Contains("✔ Effort           high", output, StringComparison.Ordinal);
        Assert.Contains("❯ Run        interrupted: #100 Rename the settings page", output, StringComparison.Ordinal);

        // The menu that the run comes back to says how the run went, and counts the tasks that are made up anew.
        Assert.Contains("❯ Run        5 tasks", output, StringComparison.Ordinal);
        Assert.Contains("6 tasks in 69 s · 5 done · 1 done in part", output.Split("What to do")[^2], StringComparison.Ordinal);

        // The screen is left for the run and entered again for the menu; the tasks of the run stay in the history
        // of the terminal, and only what else there is to say is printed under them.
        Assert.Equal(2, output.Split("\u001b[?1049h").Length - 1);
        Assert.Equal(
            [
                " ✔ #100 Rename the settings page  0:06",
                " ✔ #101 Show the total of an order in its header  0:09",
                " ✔ #102 Export the orders as a file  0:21",
                " ◐ #103 Fix the rounding of a discount  0:10",
                " ✔ #104 Update the dependencies  0:16",
                " ✔ #105 Describe the export in the manual  0:07",
                " 6 tasks in 69 s · 5 done · 1 done in part",
                "Demo: the tasks and the sessions were made up, and nothing was changed.",
            ],
            Scroll.Read(output).Rows.Where(row => row.Length > 0).Skip(1));
        Assert.False(Directory.Exists(Path.Combine(folder.FullName, ".toobusy")));
        Assert.False(File.Exists(Path.Combine(personal.FullName, "projects.toml")));
    }

    [Fact]
    public async Task ADemoRemembersAnAbortAndTheMenuAndTheNextRunStartWithTheInterruptedTask()
    {
        GitRepository();

        // The run is aborted while the first new task is worked on. The menu it comes back to says which task was
        // interrupted instead of the number of tasks; Run takes that task first, and when the run is over the tasks
        // are counted.
        var keys = new Keys()
            .Hold(() => Shown("interrupted: #100 Rename the settings page")).Press(Keys.Enter)
            .Hold(() => Shown("Read src/Orders/Order.cs")).Type("/")
            .Hold(() => Shown("❯ stop")).Press(Keys.Down, Keys.Enter)
            .Hold(() => Shown("interrupted: #101 Show the total of an order in its header")).Press(Keys.Enter)
            .Hold(() => Shown("5 tasks")).Press(Keys.Up, Keys.Enter);
        var context = Context(null) with
        {
            Clock = new PacedClock(),
            Terminal = new TerminalDevice(keys.Read, () => (120, 30)) { KeyWaiting = () => keys.Waiting },
        };

        var (exit, output, error) = await RunAsync(context, ["--demo"]);

        Assert.Equal(0, exit);
        Assert.Equal("", error);
        // The tape has the task twice: interrupted by the first run, and done by the second, which took it first.
        var rows = Scroll.Read(output).Rows;
        Assert.Contains(rows, row => Regex.IsMatch(row, @"^ ■ #101 Show the total of an order in its header  [\d:]+$"));
        Assert.Contains(rows, row => Regex.IsMatch(row, @"^ ✔ #101 Show the total of an order in its header  [\d:]+$"));
        Assert.Contains(rows, row => row.StartsWith(" ✔ #102 ", StringComparison.Ordinal));

        // A demo has it all: nothing is checked before its runs.
        Assert.DoesNotContain("checking:", output, StringComparison.Ordinal);
        Assert.False(Directory.Exists(Path.Combine(folder.FullName, ".toobusy")));
        Assert.False(File.Exists(Path.Combine(personal.FullName, "projects.toml")));
    }

    [Fact]
    public async Task ADemoOfTheRunNeedsNoSettingsEither()
    {
        GitRepository();

        var (exit, output, _) = await RunAsync("run", "--demo");

        // The whole of a demo: a task whose session a kill stopped and which goes on with it, a task that is done, a
        // session that asks and is told to go on alone, a task done in part whose session made a task of what is left,
        // a usage limit that is waited out, a task that opened after another one, and the tasks that are held.
        Assert.Equal(0, exit);
        Assert.Equal(
            """
            Demo: nothing is changed.
            ✻ v0.1.0 (made up) · 6 tasks in the queue · the assistant's own model · high effort
              Opens later: #105 after #102
              Held: #106 it has the manual label · #107 its status is In Progress
            → #100 Rename the settings page — taken again · its session was stopped, and goes on · claude attach 4f2a100 (made up)
              #100 is In Progress on the board and assigned to the current user; the session is told that toobusy keeps its task
            ✔ #100 Rename the settings page — done in 6 s
              #100 is closed, with the report of the session as its comment
            → #101 Show the total of an order in its header — started · a new session with the skill task-auto · claude attach 4f2a101 (made up)
              #101 is In Progress on the board and assigned to the current user; the session is told that toobusy keeps its task
            ✔ #101 Show the total of an order in its header — done in 9 s
              #101 is closed, with the report of the session as its comment
            → #102 Export the orders as a file — started · a new session with the skill task-auto · claude attach 4f2a102 (made up)
              #102 is In Progress on the board and assigned to the current user; the session is told that toobusy keeps its task
            ‖ #102 waits for the owner: input needed · it is told to go on alone in 10 s: /nudge does it now, /hold never · claude attach 4f2a102 (made up)
              │ The task does not say what kind of file the export is. Should it be CSV or JSON?
            ▶ #102 is told to go on without the owner: its turn is cut, and this is the next message of its conversation · claude attach 4f2a102 (made up)
              “The owner is away: no answer to your question and no approval will come. Decide yourself and go on with task #102 from where you stopped. What cannot be decided without the owner, make a new task of it yourself: describe there what is left and your questions for the owner, and put the label `needs-owner` on it, so that no run takes it before the owner answers. End your reply with a `TOOBUSY:` line.”
            ✔ #102 Export the orders as a file — done in 21 s
              #102 is closed, with the report of the session as its comment
            → #103 Fix the rounding of a discount — started · a new session with the skill task-auto · claude attach 4f2a103 (made up)
              #103 is In Progress on the board and assigned to the current user; the session is told that toobusy keeps its task
            ◐ #103 Fix the rounding of a discount — done in part in 10 s
              #103 is closed, with the report of the session as its comment; what is left is a task that the session made
            → #104 Update the dependencies — started · a new session with the skill task-auto · claude attach 4f2a104 (made up)
              #104 is In Progress on the board and assigned to the current user; the session is told that toobusy keeps its task
            ‖ #104 ran into a usage limit: You have hit your usage limit.
            ‖ #104 waits 7 s for the 5-hour limit to reset, then goes on · /stop pauses it for the next run
            ▶ #104 goes on after the reset of the limit · claude attach 4f2a104 (made up)
            ✔ #104 Update the dependencies — done in 16 s
              #104 is closed, with the report of the session as its comment
            → #105 Describe the export in the manual — started · a new session with the skill task-auto · claude attach 4f2a105 (made up)
              #105 is In Progress on the board and assigned to the current user; the session is told that toobusy keeps its task
            ✔ #105 Describe the export in the manual — done in 7 s
              #105 is closed, with the report of the session as its comment
              Held: #106 it has the manual label · #107 its status is In Progress · #108 it waits for the owner: it has the needs-owner label
            ✔ No task is left · 5 done · 1 done in part
              Worked 69 s

            """.ReplaceLineEndings(),
            output);
    }

    [Fact]
    public async Task ADemoShowsTheSettingsThatExist()
    {
        GitRepository();
        WriteSettings(Settings);

        var (_, output, _) = await RunWithKeysAsync(new Keys().Press(Keys.Up, Keys.Enter), ["--demo"]);

        Assert.Contains("✔ Project          none", output, StringComparison.Ordinal);
        Assert.Contains("✔ Blocking labels  manual", output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task LeavingTheFirstSetupLeavesThePage()
    {
        GitRepository();

        var (exit, output, _) = await RunWithKeysAsync(new Keys().Press(Keys.ControlC, Keys.ControlC), []);

        Assert.Equal(0, exit);
        Assert.DoesNotContain("What to do", output, StringComparison.Ordinal);
        Assert.False(Directory.Exists(Path.Combine(folder.FullName, ".toobusy")));
    }

    [Fact]
    public async Task ARealSetupWritesTheSettings()
    {
        GitRepository();
        var processes = GitHub(milestones: "", labels: "bug\nmanual\n");

        // No project; `manual` blocks; any label; `manual` is the label of the owner too, the one above `New label…`;
        // a new label for interrupted tasks, by the name that is proposed; no skill, the proposed name being
        // deleted; yes.
        var keys = new Keys().Press(
            Keys.Tab, Keys.Tab, Keys.Enter, Keys.Down, Keys.Space, Keys.Enter, Keys.Enter,
            Keys.Up, Keys.Enter, Keys.Enter, Keys.Enter, Keys.Control('u'), Keys.Enter, Keys.Enter);

        var (exit, output, _) = await RunWithKeysAsync(keys, processes, "init");

        Assert.Equal(0, exit);
        Assert.EndsWith(
            "✔ The settings are written to .toobusy/settings.toml. Commit the file." + Environment.NewLine
            + "`toobusy about` tells how a run works, for you and for your assistant." + Environment.NewLine,
            Report(output),
            StringComparison.Ordinal);
        var written = File.ReadAllText(Path.Combine(folder.FullName, ".toobusy", "settings.toml"));
        Assert.Contains("blocking = [\"manual\"]", written, StringComparison.Ordinal);
        Assert.Contains("owner = \"manual\"\ninterrupted = \"interrupted\"", written, StringComparison.Ordinal);
        Assert.DoesNotContain("session", written, StringComparison.Ordinal);
        Assert.Contains("✔ Skill            none", Report(output), StringComparison.Ordinal);
        Assert.DoesNotContain("There is no skill", output, StringComparison.Ordinal);
        Assert.Contains(processes.Asked, asked => asked.Command == "gh" && asked.Arguments.Take(4).SequenceEqual(["label", "list", "--repo", "acme/rocket"]));

        // Only the label that the repository does not have is made.
        Assert.Equal(
            [["label", "create", "interrupted", "--repo", "acme/rocket"]],
            processes.Asked.Where(asked => asked.Command == "gh" && asked.Arguments.Take(2).SequenceEqual(["label", "create"])).Select(asked => asked.Arguments));
    }

    [Fact]
    public async Task WithoutTheGitHubToolASetupSaysHowToGetIt()
    {
        GitRepository();

        var (_, output, _) = await RunWithKeysAsync(new Keys().Press(Keys.ControlC, Keys.ControlC), "init");

        Assert.Contains("✘ The GitHub command-line tool `gh` is not installed.", output, StringComparison.Ordinal);
        Assert.Contains("fix: ", output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RunRefusesSettingsThatAreNotValid()
    {
        GitRepository();
        WriteSettings(Settings.Replace("blocking = [\"manual\"]", "blocking = \"manual\"", StringComparison.Ordinal));

        var (exit, output, error) = await RunAsync("run", "--demo");

        Assert.Equal(1, exit);
        Assert.Equal("", output);
        Assert.Contains(".toobusy/settings.toml:7: queue.labels.blocking: ", error, StringComparison.Ordinal);
    }

    [Fact]
    public async Task TheNotSetUpMessageIsColouredInATerminal()
    {
        GitRepository();
        using var error = new StringWriter();
        var context = new CliContext(folder.FullName, TextWriter.Null, error) { ErrorPalette = Palette.Dark };

        var exit = await CliApp.RunAsync(["run"], context, TestContext.Current.CancellationToken);

        Assert.Equal(2, exit);
        Assert.Equal(
            $"{Palette.Dark.Muted("toobusy:")} this project is not set up yet.{Environment.NewLine}Run {Palette.Dark.Accent("`toobusy init`")} to set it up.{Environment.NewLine}"
            + $"An assistant that sets a project up for its owner starts with {Palette.Dark.Accent("`toobusy init --assist`")}.{Environment.NewLine}",
            error.ToString());
    }

    [Fact]
    public async Task InitNeedsAGitRepository()
    {
        var (exit, _, error) = await RunAsync("init", "--demo");

        Assert.Equal(2, exit);
        Assert.Equal("toobusy: not inside a git repository" + Environment.NewLine, error);
    }

    [Fact]
    public async Task WithoutATerminalInitFailsAndNamesTheOptionThatAsksNothing()
    {
        GitRepository();

        var (exit, output, error) = await RunAsync("init", "--no-board");

        Assert.Equal(1, exit);
        Assert.Equal("", output);
        Assert.Equal(
            "toobusy: `init` asks questions and needs a terminal." + Environment.NewLine
            + "Run `toobusy init --yes` to ask nothing: it takes what the options say and what the setup proposes. `toobusy init --help` lists the options." + Environment.NewLine
            + "An assistant that sets a project up for its owner starts with `toobusy init --assist`." + Environment.NewLine,
            error);
        Assert.False(Directory.Exists(Path.Combine(folder.FullName, ".toobusy")));
    }

    [Fact]
    public async Task TheHelpOfInitListsItsOptions()
    {
        var (exit, output, _) = await RunAsync("init", "--help");

        Assert.Equal(0, exit);
        foreach (var option in (string[])["--board <URL>", "--no-board", "--new-board <title>", "--board-owner <login>", "--blocking-label <name>", "--no-blocking-labels", "--take-label <name>", "--no-take-labels", "--owner-label <name>", "--interrupt-label <name>", "--skill <name>", "--no-skill", "--before <command>", "--no-before", "--after <command>", "--no-after", "-y, --yes"])
            Assert.Contains(option, output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task WithYesAndWithoutATerminalAFirstSetupTakesWhatIsProposedAndWritesIt()
    {
        GitRepository();
        var processes = GitHub(milestones: "", labels: "bug\nmanual\n");

        var (exit, output, error) = await RunAsync(Context(processes), ["init", "--yes"]);

        Assert.Equal(0, exit);
        Assert.Equal("", error);
        Assert.Equal(
            [
                $"toobusy · Setting up this project · ~{Path.DirectorySeparatorChar}{folder.Name}",
                "! There is no skill “task-auto” yet: write .claude/skills/task-auto/SKILL.md before a run.",
                "`toobusy about` tells what the skill is to say.",
                "✔ Tracker          GitHub",
                "✔ Project          none",
                "✔ Blocking labels  none",
                "✔ Labels to take   any task",
                "✔ Owner's label    needs-owner",
                "✔ Interrupt label  interrupted",
                "✔ Skill            task-auto",
                "✔ Assistant        Claude Code",
                "The label “needs-owner” was made in acme/rocket.",
                "The label “interrupted” was made in acme/rocket.",
                "✔ The settings are written to .toobusy/settings.toml. Commit the file.",
                "`toobusy about` tells how a run works, for you and for your assistant.",
                "",
            ],
            output.Split(Environment.NewLine));
        var written = File.ReadAllText(Path.Combine(folder.FullName, ".toobusy", "settings.toml"));
        Assert.Contains("blocking = []\ntake = []\nowner = \"needs-owner\"\ninterrupted = \"interrupted\"", written, StringComparison.Ordinal);
        Assert.EndsWith("[session]\nskill = \"task-auto\"\n", written, StringComparison.Ordinal);
        Assert.DoesNotContain("board", written.Replace("# ", "", StringComparison.Ordinal).Split('\n').Where(line => line.Contains('=', StringComparison.Ordinal)).Aggregate("", string.Concat), StringComparison.Ordinal);
    }

    [Fact]
    public async Task WithYesTheOptionsOfTheSkillNameItOrSayThatThereIsNone()
    {
        GitRepository();
        var processes = GitHub(milestones: "", labels: "bug\nmanual\n");

        var (exit, output, _) = await RunAsync(Context(processes), ["init", "--yes", "--no-skill"]);

        Assert.Equal(0, exit);
        Assert.Contains("✔ Skill            none", output, StringComparison.Ordinal);
        Assert.DoesNotContain("There is no skill", output, StringComparison.Ordinal);
        Assert.DoesNotContain("session", File.ReadAllText(Path.Combine(folder.FullName, ".toobusy", "settings.toml")), StringComparison.Ordinal);

        // A skill that is written is not spoken of; the settings that exist get its section.
        WriteSkill("night-shift");
        (exit, output, _) = await RunAsync(Context(processes), ["init", "--yes", "--skill", "/night-shift"]);

        Assert.Equal(0, exit);
        Assert.Contains("✔ Skill            night-shift", output, StringComparison.Ordinal);
        Assert.DoesNotContain("There is no skill", output, StringComparison.Ordinal);
        Assert.EndsWith(
            "\n# What a session starts with: the skill of the project that says how a task is done here without its owner.\n[session]\nskill = \"night-shift\"\n",
            File.ReadAllText(Path.Combine(folder.FullName, ".toobusy", "settings.toml")),
            StringComparison.Ordinal);

        // Settings that name a skill keep it when no option speaks of it.
        var written = File.ReadAllText(Path.Combine(folder.FullName, ".toobusy", "settings.toml"));
        (exit, output, _) = await RunAsync(Context(processes), ["init", "--yes"]);

        Assert.Equal(0, exit);
        Assert.Contains("✔ Skill            night-shift", output, StringComparison.Ordinal);
        Assert.Equal(written, File.ReadAllText(Path.Combine(folder.FullName, ".toobusy", "settings.toml")));
    }

    [Fact]
    public async Task WithYesTheOptionsOfTheCommandsSetChangeAndTakeThemAway()
    {
        GitRepository();
        var processes = GitHub(milestones: "", labels: "bug\nneeds-owner\ninterrupted\n");
        var file = Path.Combine(folder.FullName, ".toobusy", "settings.toml");

        // A first setup has none without the options, and writes what they say.
        var (exit, output, _) = await RunAsync(Context(processes), ["init", "--yes", "--no-skill", "--before", "scripts/dev.sh ports free", "--after", "scripts/dev.sh server stop"]);

        Assert.Equal(0, exit);
        Assert.Contains(
            "✔ Assistant        Claude Code" + Environment.NewLine
            + "✔ Before a task    none → scripts/dev.sh ports free" + Environment.NewLine
            + "✔ After a task     none → scripts/dev.sh server stop" + Environment.NewLine,
            output,
            StringComparison.Ordinal);
        Assert.EndsWith("[task]\nbefore = \"scripts/dev.sh ports free\"\nafter = \"scripts/dev.sh server stop\"\n", File.ReadAllText(file), StringComparison.Ordinal);

        // Without an option they are kept, and nothing is said of them.
        var written = File.ReadAllText(file);
        (exit, output, _) = await RunAsync(Context(processes), ["init", "--yes"]);

        Assert.Equal(0, exit);
        Assert.DoesNotContain("a task", output, StringComparison.Ordinal);
        Assert.EndsWith("Nothing to change: the settings already say this." + Environment.NewLine, output, StringComparison.Ordinal);
        Assert.Equal(written, File.ReadAllText(file));

        // One is changed and the other taken away.
        (exit, output, _) = await RunAsync(Context(processes), ["init", "--yes", "--before", "make free", "--no-after"]);

        Assert.Equal(0, exit);
        Assert.Contains("✔ Before a task    scripts/dev.sh ports free → make free" + Environment.NewLine + "✔ After a task     scripts/dev.sh server stop → none" + Environment.NewLine, output, StringComparison.Ordinal);
        Assert.EndsWith("[task]\nbefore = \"make free\"\n", File.ReadAllText(file), StringComparison.Ordinal);

        (exit, _, _) = await RunAsync(Context(processes), ["init", "--yes", "--no-before"]);

        Assert.Equal(0, exit);
        Assert.DoesNotContain("before", File.ReadAllText(file).Split("[task]")[^1], StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("--before", "")]
    [InlineData("--after", "  ")]
    public async Task WithYesAnEmptyCommandFailsAndNothingIsWritten(string option, string empty)
    {
        GitRepository();
        var processes = GitHub(milestones: "", labels: "bug\nmanual\n");

        var (exit, output, _) = await RunAsync(Context(processes), ["init", "--yes", option, empty]);

        Assert.Equal(1, exit);
        Assert.EndsWith($"✘ {option}: A command must not be empty: give --no-{option[2..]} to run none. The settings were not written." + Environment.NewLine, output, StringComparison.Ordinal);
        Assert.False(File.Exists(Path.Combine(folder.FullName, ".toobusy", "settings.toml")));
        Assert.DoesNotContain(processes.Asked, asked => asked.Command == "gh" && asked.Arguments.Take(2).SequenceEqual(["label", "create"]));
    }

    [Fact]
    public async Task WithoutYesTheCommandsOfTheOptionsStandOnTheLastPageAndAreWrittenOnSave()
    {
        GitRepository();
        WriteSettings(Settings);
        var processes = GitHub(milestones: "", Labels);

        // Enter on every question keeps what the settings say; the last page has the command alone to change.
        var keys = new Keys().Press(Keys.Enter, Keys.Enter, Keys.Enter, Keys.Enter, Keys.Enter, Keys.Enter, Keys.Enter);

        var (exit, output, _) = await RunWithKeysAsync(keys, processes, "init", "--before", "scripts/dev.sh ports free");

        Assert.Equal(0, exit);
        Assert.Contains("This will change in .toobusy/settings.toml:", output, StringComparison.Ordinal);
        Assert.Contains("Before a task: none → scripts/dev.sh ports free", output, StringComparison.Ordinal);
        Assert.DoesNotContain("After a task", output, StringComparison.Ordinal);
        Assert.Contains("✔ Before a task    none → scripts/dev.sh ports free" + Environment.NewLine + "✔ The settings are written to .toobusy/settings.toml. Commit the file.", Report(output), StringComparison.Ordinal);
        Assert.EndsWith("[task]\nbefore = \"scripts/dev.sh ports free\"\n", File.ReadAllText(Path.Combine(folder.FullName, ".toobusy", "settings.toml")), StringComparison.Ordinal);
    }

    [Fact]
    public async Task WithYesANameThatIsNotOneOfASkillFailsAndNothingIsWritten()
    {
        GitRepository();
        var processes = GitHub(milestones: "", labels: "bug\nmanual\n");

        var (exit, output, _) = await RunAsync(Context(processes), ["init", "--yes", "--skill", "task auto"]);

        Assert.Equal(1, exit);
        Assert.EndsWith("✘ Skill: task auto is not the name of a skill: it is letters, digits and dashes, as task-auto. The settings were not written." + Environment.NewLine, output, StringComparison.Ordinal);
        Assert.False(File.Exists(Path.Combine(folder.FullName, ".toobusy", "settings.toml")));
    }

    [Fact]
    public async Task WithYesEveryOptionIsTheAnswer()
    {
        GitRepository();
        var processes = GitHubWithProjects(labels: "bug\nfeature\nmanual\ndraft\n");

        var (exit, output, _) = await RunAsync(
            Context(processes),
            ["init", "-y", "--board", "https://github.com/orgs/acme/projects/3/views/1", "--blocking-label", "manual", "--blocking-label", "draft", "--take-label", "bug", "--take-label", "feature", "--owner-label", "manual", "--interrupt-label", "paused"]);

        Assert.Equal(0, exit);
        var written = File.ReadAllText(Path.Combine(folder.FullName, ".toobusy", "settings.toml"));
        Assert.Contains("board = \"https://github.com/orgs/acme/projects/3\"", written, StringComparison.Ordinal);
        Assert.Contains("blocking = [\"manual\", \"draft\"]\ntake = [\"bug\", \"feature\"]\nowner = \"manual\"\ninterrupted = \"paused\"", written, StringComparison.Ordinal);
        Assert.Contains("✔ Project          https://github.com/orgs/acme/projects/3", output, StringComparison.Ordinal);
        Assert.Contains("The project was linked to acme/rocket.", output, StringComparison.Ordinal);
        Assert.Contains("The label “paused” was made in acme/rocket.", output, StringComparison.Ordinal);
        Assert.Equal(
            [["label", "create", "paused", "--repo", "acme/rocket"]],
            processes.Asked.Where(asked => asked.Command == "gh" && asked.Arguments.Take(2).SequenceEqual(["label", "create"])).Select(asked => asked.Arguments));
    }

    [Fact]
    public async Task WithYesABoardThatLacksAStatusOfARunIsALineOfAWarningAndTheSettingsAreWritten()
    {
        GitRepository();
        var processes = GitHubWithProjects(Labels, statuses: "board\nstatus\tTodo\nstatus\tDoing\nstatus\tDone\n");

        var (exit, output, error) = await RunAsync(Context(processes), ["init", "--yes", "--board", "https://github.com/orgs/acme/projects/3", "--owner-label", "manual", "--no-skill"]);

        Assert.Equal(0, exit);
        Assert.Equal("", error);
        Assert.Equal(
            [
                "! The board has no status “In Progress”: it has Todo, Doing, Done.",
                "Say which of its statuses is which with `--todo-status`, `--in-progress-status` and `--done-status`, or with `toobusy init`: `toobusy doctor` fails until then.",
                "✔ Tracker          GitHub",
                "✔ Project          https://github.com/orgs/acme/projects/3",
            ],
            output.Split(Environment.NewLine)[1..5]);
        Assert.Contains("✔ The settings are written to .toobusy/settings.toml. Commit the file.", output, StringComparison.Ordinal);
        Assert.Contains("board = \"https://github.com/orgs/acme/projects/3\"", File.ReadAllText(Path.Combine(folder.FullName, ".toobusy", "settings.toml")), StringComparison.Ordinal);

        // The board is the one of the settings now, and a setup that keeps it says the same again.
        (exit, output, _) = await RunAsync(Context(processes), ["init", "--yes"]);

        Assert.Equal(0, exit);
        Assert.Contains("! The board has no status “In Progress”: it has Todo, Doing, Done.", output, StringComparison.Ordinal);

        // An option says which status is which, and it is kept as the board writes it.
        (exit, output, _) = await RunAsync(Context(processes), ["init", "--yes", "--in-progress-status", "doing"]);

        Assert.Equal(0, exit);
        Assert.DoesNotContain("The board has no status", output, StringComparison.Ordinal);
        Assert.Contains("✔ Statuses         Todo, Doing, Done", output, StringComparison.Ordinal);
        Assert.Contains("[tracker.statuses]\nin-progress = \"Doing\"\n", File.ReadAllText(Path.Combine(folder.FullName, ".toobusy", "settings.toml")), StringComparison.Ordinal);
    }

    [Fact]
    public async Task WithYesAStatusThatTheBoardDoesNotHaveFailsAndNothingIsWritten()
    {
        GitRepository();
        var processes = GitHubWithProjects(Labels, statuses: "board\nstatus\tTodo\nstatus\tDoing\nstatus\tDone\n");

        var (exit, output, _) = await RunAsync(Context(processes), ["init", "--yes", "--board", "https://github.com/orgs/acme/projects/3", "--in-progress-status", "Working", "--owner-label", "manual", "--no-skill"]);

        Assert.Equal(1, exit);
        Assert.EndsWith("✘ In Progress status: The board has no status “Working”: it has Todo, Doing, Done. The settings were not written." + Environment.NewLine, output, StringComparison.Ordinal);
        Assert.False(File.Exists(Path.Combine(folder.FullName, ".toobusy", "settings.toml")));
    }

    [Fact]
    public async Task TheOptionsOfTheStatusesDoNotGoWithNoBoard()
    {
        GitRepository();

        var (exit, _, error) = await RunAsync(Context(GitHubWithProjects(Labels)), ["init", "--yes", "--no-board", "--done-status", "Shipped"]);

        Assert.Equal(2, exit);
        Assert.Equal("toobusy: `--todo-status`, `--in-progress-status` and `--done-status` name the statuses of a board, and do not go with `--no-board`" + Environment.NewLine, error);
    }

    [Fact]
    public async Task WithYesABoardThatHasTheStatusesOfARunIsNotSpokenOf()
    {
        GitRepository();
        var processes = GitHubWithProjects(Labels);

        var (exit, output, _) = await RunAsync(Context(processes), ["init", "--yes", "--board", "https://github.com/orgs/acme/projects/3", "--owner-label", "manual", "--no-skill"]);

        Assert.Equal(0, exit);
        Assert.DoesNotContain("status", output, StringComparison.Ordinal);
        Assert.Contains(processes.Asked, asked => asked.Command == "gh" && asked.Arguments.Any(argument => argument.Contains("options { name }", StringComparison.Ordinal)));
    }

    [Fact]
    public async Task OnTheScreenABoardThatIsNotNamedAsGitHubNamesOneIsAskedWhichOfItsStatusesIsWhich()
    {
        GitRepository();
        var processes = GitHubWithProjects(labels: "bug\nmanual\ninterrupted\n", statuses: "board\nstatus\tBacklog\nstatus\tReady\nstatus\tIn Progress\nstatus\tDone\n");

        // The board, then the second of its statuses for a task to do and what is proposed for the other two, then
        // Enter on every question, the last one included.
        var keys = new Keys().Press(Keys.Enter, Keys.Down, Keys.Enter, Keys.Enter, Keys.Enter, Keys.Enter, Keys.Enter, Keys.Enter, Keys.Enter, Keys.Enter, Keys.Enter);

        var (exit, output, _) = await RunWithKeysAsync(keys, processes, "init", "--board", "https://github.com/orgs/acme/projects/3", "--blocking-label", "manual", "--take-label", "bug", "--owner-label", "manual", "--interrupt-label", "interrupted", "--no-skill");

        Assert.Equal(0, exit);
        var page = output.Split(Screen.Leave)[0];
        Assert.Contains("Todo status", page, StringComparison.Ordinal);
        Assert.Contains("A task is taken only from this status of the board.", page, StringComparison.Ordinal);
        Assert.Contains("A run moves a task to this status before its session.", page, StringComparison.Ordinal);
        Assert.Contains("A run moves a task to this status when it is done.", page, StringComparison.Ordinal);
        Assert.Contains("Statuses         Ready, In Progress, Done", page, StringComparison.Ordinal);
        Assert.DoesNotContain("The board has no status", page, StringComparison.Ordinal);
        var written = File.ReadAllText(Path.Combine(folder.FullName, ".toobusy", "settings.toml"));
        Assert.Contains("board = \"https://github.com/orgs/acme/projects/3\"\n\n", written, StringComparison.Ordinal);
        Assert.Contains("[tracker.statuses]\ntodo = \"Ready\"\n\n", written, StringComparison.Ordinal);
    }

    [Fact]
    public async Task OnTheScreenABoardWithFewerThanThreeStatusesIsAWarningOfTheLastPage()
    {
        GitRepository();
        var processes = GitHubWithProjects(labels: "bug\nmanual\ninterrupted\n", statuses: "board\nstatus\tOpen\nstatus\tDone\n");

        // Enter on every question, the last one included.
        var keys = new Keys().Press(Keys.Enter, Keys.Enter, Keys.Enter, Keys.Enter, Keys.Enter, Keys.Enter, Keys.Enter);

        var (exit, output, _) = await RunWithKeysAsync(keys, processes, "init", "--board", "https://github.com/orgs/acme/projects/3", "--blocking-label", "manual", "--take-label", "bug", "--owner-label", "manual", "--interrupt-label", "interrupted", "--no-skill");

        Assert.Equal(0, exit);
        var page = output.Split(Screen.Leave)[0];
        Assert.Contains("! The board has no statuses “Todo”, “In Progress”: it has Open, Done.", page, StringComparison.Ordinal);
        Assert.Contains("A run needs three statuses of a board: add them on GitHub and run `toobusy init` again.", page, StringComparison.Ordinal);
        Assert.Contains("Write the settings?", page, StringComparison.Ordinal);
        Assert.Contains("board = \"https://github.com/orgs/acme/projects/3\"", File.ReadAllText(Path.Combine(folder.FullName, ".toobusy", "settings.toml")), StringComparison.Ordinal);
    }

    [Fact]
    public async Task WithYesANewBoardIsMadeForTheOwnerThatIsNamed()
    {
        GitRepository();
        var processes = GitHubWithProjects(labels: "manual\ninterrupted\n");

        var (exit, output, _) = await RunAsync(Context(processes), ["init", "--yes", "--new-board", "Rocket", "--board-owner", "ann", "--owner-label", "manual"]);

        Assert.Equal(0, exit);
        Assert.Contains("✔ Project          new: Rocket (ann)", output, StringComparison.Ordinal);
        Assert.Contains("The project “Rocket” was made for ann: https://github.com/users/ann/projects/9", output, StringComparison.Ordinal);
        Assert.Contains("The project was linked to acme/rocket.", output, StringComparison.Ordinal);
        Assert.Contains(processes.Asked, asked => asked.Command == "gh" && asked.Arguments.Contains("title=Rocket"));
        Assert.Contains("board = \"https://github.com/users/ann/projects/9\"", File.ReadAllText(Path.Combine(folder.FullName, ".toobusy", "settings.toml")), StringComparison.Ordinal);
    }

    [Fact]
    public async Task WithYesAnExistingSetupIsLeftAsItIsAndAnOptionChangesOnlyItsAnswer()
    {
        GitRepository();
        WriteSettings(Settings);
        var processes = GitHub(milestones: "", labels: Labels + "bug\n");

        var (exit, output, _) = await RunAsync(Context(processes), ["init", "--yes"]);

        Assert.Equal(0, exit);
        Assert.EndsWith("Nothing to change: the settings already say this." + Environment.NewLine, output, StringComparison.Ordinal);
        Assert.Equal(Settings, File.ReadAllText(Path.Combine(folder.FullName, ".toobusy", "settings.toml")));

        (exit, _, _) = await RunAsync(Context(processes), ["init", "--yes", "--take-label", "bug", "--no-blocking-labels"]);

        Assert.Equal(0, exit);
        var written = File.ReadAllText(Path.Combine(folder.FullName, ".toobusy", "settings.toml"));
        Assert.Contains("blocking = []\ntake = [\"bug\"]\nowner = \"manual\"\ninterrupted = \"interrupted\"", written, StringComparison.Ordinal);

        (exit, _, _) = await RunAsync(Context(processes), ["init", "--yes", "--no-take-labels", "--no-board"]);

        Assert.Equal(0, exit);
        Assert.Contains("take = []", File.ReadAllText(Path.Combine(folder.FullName, ".toobusy", "settings.toml")), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("✘ Blocking labels: acme/rocket has no label “urgent”. The settings were not written.", "--blocking-label", "urgent")]
    [InlineData("✘ Labels to take: acme/rocket has no label “nice”. The settings were not written.", "--take-label", "nice")]
    [InlineData("✘ Labels to take: manual is a blocking label. The settings were not written.", "--blocking-label", "manual", "--take-label", "manual")]
    [InlineData("✘ Project: rocket is not a project. Use https://github.com/orgs/<org>/projects/<number>. The settings were not written.", "--board", "rocket")]
    [InlineData("✘ Project: A project cannot be made for acme, only for ann. The settings were not written.", "--new-board", "Rocket", "--board-owner", "acme")]
    [InlineData("✘ Interrupt label: manual is the label of the owner. The settings were not written.", "--owner-label", "manual", "--interrupt-label", "manual")]
    public async Task WithYesAValueThatFailsItsCheckIsAnErrorAndNothingIsWritten(string failure, params string[] options)
    {
        GitRepository();
        var processes = GitHub(milestones: "", labels: "bug\nmanual\n");

        var (exit, output, _) = await RunAsync(Context(processes), ["init", "--yes", .. options]);

        Assert.Equal(1, exit);
        Assert.EndsWith(failure + Environment.NewLine, output, StringComparison.Ordinal);
        Assert.False(Directory.Exists(Path.Combine(folder.FullName, ".toobusy")));
        Assert.DoesNotContain(processes.Asked, asked => asked.Command == "gh" && asked.Arguments.Contains("create"));
    }

    [Fact]
    public async Task WithYesABoardThatCannotBeReachedIsAnErrorAndNothingIsWritten()
    {
        GitRepository();
        var processes = new FakeProcesses((command, arguments) => command == "gh" && arguments[0] == "project"
            ? new ProcessResult(ProcessStatus.Exited, 1, "", "Could not resolve to a ProjectV2")
            : new ProcessResult(ProcessStatus.Exited, 0, command == "git" ? "https://github.com/acme/rocket.git\n" : "", ""));

        var (exit, output, _) = await RunAsync(Context(processes), ["init", "--yes", "--board", "https://github.com/orgs/acme/projects/3"]);

        Assert.Equal(1, exit);
        Assert.Contains("✘ Project: ", output, StringComparison.Ordinal);
        Assert.EndsWith("The settings were not written." + Environment.NewLine, output, StringComparison.Ordinal);
        Assert.False(Directory.Exists(Path.Combine(folder.FullName, ".toobusy")));
    }

    [Fact]
    public async Task WithYesSettingsThatDoNotValidateAreAnErrorAndStayAsTheyAre()
    {
        GitRepository();
        WriteSettings("version = 1\n");

        var (exit, output, _) = await RunAsync(Context(GitHub(milestones: "")), ["init", "--yes"]);

        Assert.Equal(1, exit);
        Assert.Contains("! The settings that exist do not validate:", output, StringComparison.Ordinal);
        Assert.Contains("✘ .toobusy/settings.toml", output, StringComparison.Ordinal);
        Assert.EndsWith("✘ The settings that exist do not validate. The settings were not written." + Environment.NewLine, output, StringComparison.Ordinal);
        Assert.Equal("version = 1\n", File.ReadAllText(Path.Combine(folder.FullName, ".toobusy", "settings.toml")));
    }

    [Fact]
    public async Task WithYesAndWithoutTheGitHubToolTheOptionsAreWrittenAsTheyAreAndThatIsSaid()
    {
        GitRepository();

        var (exit, output, _) = await RunAsync("init", "--yes", "--blocking-label", "manual");

        Assert.Equal(0, exit);
        Assert.Contains("✘ The GitHub command-line tool `gh` is not installed.", output, StringComparison.Ordinal);
        Assert.Contains("nothing is verified", output, StringComparison.Ordinal);
        Assert.Contains("blocking = [\"manual\"]", File.ReadAllText(Path.Combine(folder.FullName, ".toobusy", "settings.toml")), StringComparison.Ordinal);
    }

    [Fact]
    public async Task WithYesANewBoardIsAnErrorWhereTheOriginIsNotAGitHubRepository()
    {
        GitRepository();
        var processes = ElsewhereWithProjects();

        var (exit, output, _) = await RunAsync(Context(processes), ["init", "--yes", "--new-board", "Rocket"]);

        Assert.Equal(1, exit);
        Assert.Contains("nothing is verified", output, StringComparison.Ordinal);
        Assert.EndsWith("✘ Project: A new project cannot be made where nothing is verified. The settings were not written." + Environment.NewLine, output, StringComparison.Ordinal);
        Assert.DoesNotContain(processes.Asked, asked => asked.Arguments.Any(argument => argument.Contains("createProjectV2", StringComparison.Ordinal)));
        Assert.False(File.Exists(Path.Combine(folder.FullName, ".toobusy", "settings.toml")));
    }

    [Fact]
    public async Task WhereTheOriginIsNotAGitHubRepositoryTheSetupOffersNoNewProject()
    {
        GitRepository();
        var processes = ElsewhereWithProjects();
        var keys = new Keys().Press(Keys.Tab, Keys.Enter, Keys.Enter, Keys.Enter, Keys.Enter, Keys.Enter, Keys.Enter, Keys.Enter);

        var (exit, output, _) = await RunWithKeysAsync(keys, processes, "init", "--new-board", "Rocket");

        Assert.Equal(0, exit);
        Assert.Contains("! Project: A new project cannot be made where nothing is verified.", output, StringComparison.Ordinal);
        Assert.Contains("By URL", output, StringComparison.Ordinal);
        Assert.DoesNotContain("New project", output, StringComparison.Ordinal);
        Assert.Contains("✔ Project          none", Report(output), StringComparison.Ordinal);
        Assert.DoesNotContain(processes.Asked, asked => asked.Arguments.Any(argument => argument.Contains("createProjectV2", StringComparison.Ordinal)));
    }

    [Fact]
    public async Task WithYesADemoChangesNothing()
    {
        GitRepository();

        var (exit, output, error) = await RunAsync("init", "--demo", "--yes", "--take-label", "bug");

        Assert.Equal(0, exit);
        Assert.Equal("", error);
        Assert.Contains("✔ Labels to take   bug", output, StringComparison.Ordinal);
        Assert.DoesNotContain("was made in", output, StringComparison.Ordinal);
        Assert.EndsWith("Demo: nothing was made, linked or written; .toobusy/settings.toml is left as it was." + Environment.NewLine, output, StringComparison.Ordinal);
        Assert.False(Directory.Exists(Path.Combine(folder.FullName, ".toobusy")));
    }

    [Fact]
    public async Task WithYesInATerminalNoScreenIsOpened()
    {
        GitRepository();

        var (exit, output, _) = await RunWithKeysAsync(new Keys(), "init", "--demo", "--yes");

        Assert.Equal(0, exit);
        Assert.DoesNotContain("\u001b[?1049h", output, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("toobusy: give one of `--board`, `--new-board` and `--no-board`, not several", "--board", "https://github.com/orgs/acme/projects/3", "--no-board")]
    [InlineData("toobusy: give one of `--board`, `--new-board` and `--no-board`, not several", "--new-board", "Rocket", "--no-board")]
    [InlineData("toobusy: `--board-owner` goes with `--new-board`", "--board-owner", "acme")]
    [InlineData("toobusy: give `--blocking-label` or `--no-blocking-labels`, not both", "--blocking-label", "manual", "--no-blocking-labels")]
    [InlineData("toobusy: give `--take-label` or `--no-take-labels`, not both", "--take-label", "bug", "--no-take-labels")]
    [InlineData("toobusy: give `--skill` or `--no-skill`, not both", "--skill", "task-auto", "--no-skill")]
    [InlineData("toobusy: give `--before` or `--no-before`, not both", "--before", "make free", "--no-before")]
    [InlineData("toobusy: give `--after` or `--no-after`, not both", "--after", "make stop", "--no-after")]
    public async Task OptionsThatAnswerTheSameQuestionDifferentlyAreAWrongCommandLine(string message, params string[] options)
    {
        GitRepository();

        var (exit, _, error) = await RunAsync(["init", "--yes", "--demo", .. options]);

        Assert.Equal(2, exit);
        Assert.Equal(message + Environment.NewLine, error);
    }

    [Fact]
    public async Task WithoutYesTheOptionsAreTheProposedAnswersOfTheScreen()
    {
        GitRepository();
        var processes = GitHub(milestones: "", labels: "bug\nmanual\ninterrupted\n");

        // Enter on every question, the last one included: no board, `manual` blocks, `bug` is taken, `manual` is
        // the label of the owner, `interrupted` the one of interrupted tasks, and `night-shift` is the skill.
        var keys = new Keys().Press(Keys.Enter, Keys.Enter, Keys.Enter, Keys.Enter, Keys.Enter, Keys.Enter, Keys.Enter);

        var (exit, output, _) = await RunWithKeysAsync(keys, processes, "init", "--no-board", "--blocking-label", "manual", "--take-label", "bug", "--owner-label", "manual", "--interrupt-label", "interrupted", "--skill", "night-shift");

        Assert.Equal(0, exit);
        Assert.Contains("[No project]", output, StringComparison.Ordinal);
        var written = File.ReadAllText(Path.Combine(folder.FullName, ".toobusy", "settings.toml"));
        Assert.Contains("blocking = [\"manual\"]\ntake = [\"bug\"]\nowner = \"manual\"\ninterrupted = \"interrupted\"", written, StringComparison.Ordinal);
        Assert.Contains("skill = \"night-shift\"", written, StringComparison.Ordinal);
        Assert.DoesNotContain("was made", Report(output), StringComparison.Ordinal);
    }

    [Fact]
    public async Task ADemoOfTheSetupGoesThroughTheStepsAndWritesNothing()
    {
        GitRepository();

        // The first of the boards, no blocking labels, any label, a new label for the owner by the name that is
        // proposed, the label `interrupted` of the made-up ones, the skill that is proposed, yes.
        var keys = new Keys().Press(Keys.Enter, Keys.Enter, Keys.Enter, Keys.Enter, Keys.Enter, Keys.Enter, Keys.Enter, Keys.Enter);

        var (exit, output, error) = await RunWithKeysAsync(keys, "init", "--demo");

        Assert.Equal(0, exit);
        Assert.Equal("", error);
        Assert.StartsWith("\u001b[?1049h", output, StringComparison.Ordinal);
        Assert.Contains("Setting up this project · demo", output, StringComparison.Ordinal);
        Assert.Contains("The answers above will be written to .toobusy/settings.toml.", output, StringComparison.Ordinal);
        Assert.Contains("The label “needs-owner” will be made in example/project.", output, StringComparison.Ordinal);
        Assert.DoesNotContain("[tracker]", output, StringComparison.Ordinal);
        Assert.Contains("A session starts with this skill of the project. Leave empty to start without one.", output, StringComparison.Ordinal);
        Assert.DoesNotContain("There is no skill", output, StringComparison.Ordinal);
        Assert.Equal(
            [
                $"toobusy · Setting up this project · ~{Path.DirectorySeparatorChar}{folder.Name}",
                "✔ Tracker          GitHub",
                "✔ Project          Rocket (made up)  https://github.com/users/example/projects/1",
                "✔ Blocking labels  none",
                "✔ Labels to take   any task",
                "✔ Owner's label    needs-owner",
                "✔ Interrupt label  interrupted",
                "✔ Skill            task-auto",
                "✔ Assistant        Claude Code",
                "Demo: nothing was made, linked or written; .toobusy/settings.toml is left as it was.",
                "",
            ],
            Report(output).Split(Environment.NewLine));
        Assert.DoesNotContain("repository", Report(output), StringComparison.OrdinalIgnoreCase);
        Assert.False(Directory.Exists(Path.Combine(folder.FullName, ".toobusy")));
    }

    [Fact]
    public async Task ADemoOfTheSetupStartsFromTheSettingsThatExist()
    {
        GitRepository();
        WriteSettings(Settings);
        var keys = new Keys().Press(Keys.Enter, Keys.Enter, Keys.Enter, Keys.Enter, Keys.Enter, Keys.Enter);

        var (exit, output, _) = await RunWithKeysAsync(keys, "init", "--demo");

        Assert.Equal(0, exit);
        Assert.Contains("✔ Project          none", Report(output), StringComparison.Ordinal);
        Assert.Contains("✔ Blocking labels  manual", Report(output), StringComparison.Ordinal);
        Assert.Contains("✔ Owner's label    manual", Report(output), StringComparison.Ordinal);
        Assert.Contains("✔ Skill            none", Report(output), StringComparison.Ordinal);
        Assert.EndsWith("Nothing to change: the settings already say this." + Environment.NewLine, Report(output), StringComparison.Ordinal);
    }

    [Fact]
    public async Task ADemoOfTheSetupReadsTheOriginAndTheBoardsOfTheUser()
    {
        GitRepository();
        var processes = new FakeProcesses(command => new ProcessResult(ProcessStatus.Exited, 0, command == "git"
            ? "https://github.com/bitpatch/toobusy.git\n"
            : "owner\tuser\tdenis\nboard\thttps://github.com/users/denis/projects/3\tFatgard\nlinked\thttps://github.com/orgs/bitpatch/projects/4\tToobusy\n", ""));

        // The board that is linked, no blocking labels, any label, a new label for the owner by the name that is
        // proposed, the label `interrupted` of the made-up ones, the skill that is proposed, yes.
        var keys = new Keys().Press(Keys.Enter, Keys.Enter, Keys.Enter, Keys.Enter, Keys.Enter, Keys.Enter, Keys.Enter, Keys.Enter);

        var (exit, output, _) = await RunWithKeysAsync(keys, processes, "init", "--demo");

        Assert.Equal(0, exit);
        Assert.Contains(processes.Asked, asked => asked.Command == "git" && asked.Arguments.SequenceEqual(["-C", folder.FullName, "remote", "get-url", "origin"]));
        Assert.Contains(processes.Asked, asked => asked.Command == "gh" && asked.Arguments.Contains("owner=bitpatch") && asked.Arguments.Contains("name=toobusy"));
        Assert.Contains("Toobusy  https://github.com/orgs/bitpatch/projects/4  linked to this repository", output, StringComparison.Ordinal);
        Assert.Contains("✔ Project          Toobusy  https://github.com/orgs/bitpatch/projects/4", Report(output), StringComparison.Ordinal);
        Assert.Contains("The label “needs-owner” will be made in bitpatch/toobusy.", output, StringComparison.Ordinal);
        Assert.Contains("✔ Interrupt label  interrupted", Report(output), StringComparison.Ordinal);
        Assert.DoesNotContain(processes.Asked, asked => asked.Arguments.Contains("create"));
    }

    [Fact]
    public async Task ADemoOfTheSetupHasMadeUpBoardsThatCallTheirStatusesOtherwise()
    {
        GitRepository();
        var processes = new FakeProcesses(command => new ProcessResult(ProcessStatus.Exited, 0, command == "git" ? "https://github.com/bitpatch/toobusy.git\n" : "owner\tuser\tdenis\n", ""));

        // The board of the option, the second of its statuses for a task to do and what is proposed after it, then
        // Enter on every question, the last one included.
        var keys = new Keys().Press(Keys.Enter, Keys.Down, Keys.Enter, Keys.Enter, Keys.Enter, Keys.Enter, Keys.Enter, Keys.Enter, Keys.Enter, Keys.Enter, Keys.Enter, Keys.Enter);

        var (exit, output, _) = await RunWithKeysAsync(keys, processes, "init", "--demo", "--board", "https://github.com/users/example/projects/2");

        Assert.Equal(0, exit);
        Assert.Contains("✔ Statuses         Ready, Doing, Shipped", Report(output), StringComparison.Ordinal);
        Assert.False(Directory.Exists(Path.Combine(folder.FullName, ".toobusy")));
    }

    [Fact]
    public async Task ADemoOfTheSetupGoesWithMadeUpBoardsWhenTheRealOnesCannotBeRead()
    {
        GitRepository();
        var processes = new FakeProcesses(_ => new ProcessResult(ProcessStatus.NotFound, 0, "", ""));

        // Nothing is verified without a repository, so the statuses of the board are typed: three questions more,
        // each with the name GitHub gives.
        var keys = new Keys().Press(Keys.Down, Keys.Enter, Keys.Enter, Keys.Enter, Keys.Enter, Keys.Enter, Keys.Enter, Keys.Enter, Keys.Enter, Keys.Enter, Keys.Enter);

        var (exit, output, _) = await RunWithKeysAsync(keys, processes, "init", "--demo");

        Assert.Equal(0, exit);
        Assert.Contains("The `origin` remote is not a GitHub repository", output, StringComparison.Ordinal);
        Assert.Contains("✔ Project          Moon base (made up)  https://github.com/users/example/projects/2", Report(output), StringComparison.Ordinal);
        Assert.Contains("A task is taken only from this status of the board. Type what the board calls it.", output, StringComparison.Ordinal);
        Assert.DoesNotContain("✔ Statuses", Report(output), StringComparison.Ordinal);
    }

    [Fact]
    public async Task ADemoOfTheSetupOffersEnoughOwnersToScroll()
    {
        GitRepository();
        var processes = new FakeProcesses(command => new ProcessResult(ProcessStatus.Exited, 0, command == "git"
            ? "https://github.com/bitpatch/toobusy.git\n"
            : "owner\torg\tbitpatch\nowner\tuser\tdenis\n", ""));
        var keys = new Keys().Press(Keys.Tab, Keys.Tab).Type("Rocket").Press(Keys.Down, Keys.Down, Keys.Enter, Keys.Enter, Keys.Enter, Keys.Enter, Keys.Enter, Keys.Enter, Keys.Enter, Keys.Enter);

        var (exit, output, _) = await RunWithKeysAsync(keys, processes, "init", "--demo");

        Assert.Equal(0, exit);
        Assert.Contains("↓ 5 more", output, StringComparison.Ordinal);
        Assert.Contains("The project “Rocket” will be made for example.", output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task DecliningTheSetupFails()
    {
        GitRepository();
        var keys = new Keys().Press(Keys.Enter, Keys.Enter, Keys.Enter, Keys.Enter, Keys.Enter, Keys.Enter, Keys.Enter, Keys.Down, Keys.Enter);

        var (exit, output, _) = await RunWithKeysAsync(keys, "init", "--demo");

        Assert.Equal(1, exit);
        Assert.EndsWith("✘ The setup was declined: nothing was changed." + Environment.NewLine, Report(output), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task EscapeTwiceFromTheFirstStepAndCtrlCTwiceLeaveTheSetup(bool controlC)
    {
        GitRepository();
        var keys = new Keys().Press(Keys.Enter).Press(controlC ? [Keys.ControlC, Keys.ControlC] : [Keys.Escape, Keys.Escape, Keys.Escape]);

        var (exit, output, _) = await RunWithKeysAsync(keys, "init", "--demo");

        Assert.Equal(1, exit);
        Assert.Equal(
            $"toobusy · Setting up this project · ~{Path.DirectorySeparatorChar}{folder.Name}{Environment.NewLine}✘ The setup was left: nothing was changed.{Environment.NewLine}",
            Report(output));
    }

    [Fact]
    public async Task StatusSaysThatNoRunIsGoingOnAndNeedsNothingButAProject()
    {
        GitRepository();
        var processes = new FakeProcesses(_ => new ProcessResult(ProcessStatus.NotFound, 0, "", ""));

        var (exit, output, error) = await RunAsync(Context(processes) with { PersonalFolder = null }, ["status"]);

        Assert.Equal(0, exit);
        Assert.Equal("", error);
        Assert.Equal("No run is going on." + Environment.NewLine, output);

        // No settings, no command of the machine, and nothing is left behind.
        Assert.Empty(processes.Asked);
        Assert.False(Directory.Exists(Path.Combine(folder.FullName, ".toobusy")));

        var (code, json, _) = await RunAsync("status", "--json");
        Assert.Equal(0, code);
        Assert.Equal("{\n  \"running\": false\n}", json.ReplaceLineEndings("\n").TrimEnd());
    }

    [Fact]
    public async Task StatusNamesTheTaskAnEarlierRunLeftWithASession()
    {
        GitRepository();
        var local = Directory.CreateDirectory(Path.Combine(folder.FullName, ".toobusy", "local"));
        var record = """{"task":14,"session":"abc12345","since":"2030-01-01T09:00:00.0000000Z","state":"paused"}""";
        File.WriteAllText(Path.Combine(local.FullName, "session.json"), record);

        var (exit, output, _) = await RunAsync("status");

        Assert.Equal(0, exit);
        Assert.Equal(
            "No run is going on." + Environment.NewLine + "An earlier run left #14 with a session: the next run goes on with it." + Environment.NewLine,
            output);

        var (code, json, _) = await RunAsync("status", "--json");
        Assert.Equal(0, code);
        Assert.Equal(
            """
            {
              "running": false,
              "left": {
                "task": 14,
                "state": "paused"
              }
            }
            """.ReplaceLineEndings("\n"),
            json.ReplaceLineEndings("\n").TrimEnd());
        Assert.Equal(record, File.ReadAllText(Path.Combine(local.FullName, "session.json")));
        Assert.Equal(["session.json"], local.GetFiles().Select(file => file.Name));
    }

    [Fact]
    public async Task StatusNamesTheTaskOfARunThatGoesOnAndHowItsSessionIsOpened()
    {
        GitRepository();
        GoingRun(Working);

        // The record of the task is there too, as it is while a session works: the run is what is told.
        File.WriteAllText(Path.Combine(folder.FullName, ".toobusy", "local", "session.json"), """{"task":12,"session":"1a2b3c4d","since":"2030-01-01T09:00:00.0000000Z","state":"working"}""");

        var (exit, output, error) = await RunFromAsync(folder.CreateSubdirectory("src").FullName, "status");

        Assert.Equal(3, exit);
        Assert.Equal("", error);
        Assert.Equal("A run is going on: #12 Export the orders as a file · claude attach 1a2b3c4d" + Environment.NewLine, output);

        var (code, json, _) = await RunAsync("status", "--json");
        Assert.Equal(3, code);
        using var told = System.Text.Json.JsonDocument.Parse(json);
        Assert.Equal(
            ["running", "process", "since", "phase", "task", "session"],
            told.RootElement.EnumerateObject().Select(property => property.Name));
        Assert.True(told.RootElement.GetProperty("running").GetBoolean());
        Assert.Equal(Environment.ProcessId, told.RootElement.GetProperty("process").GetInt32());
        Assert.Matches(@"^\d{4}-\d\d-\d\dT\d\d:\d\d:\d\dZ$", told.RootElement.GetProperty("since").GetString());
        Assert.Equal("working", told.RootElement.GetProperty("phase").GetString());
        Assert.Equal(
            """{"number":12,"title":"Export the orders as a file","url":"https://github.com/acme/rocket/issues/12"}""",
            Regex.Replace(told.RootElement.GetProperty("task").GetRawText(), @"\s+(?=[""}])|(?<=[{:,])\s+", ""));
        Assert.Equal("claude attach 1a2b3c4d", told.RootElement.GetProperty("session").GetString());
    }

    [Fact]
    public async Task StatusSaysThatARunIsBetweenTasksOrWaitsForAUsageLimit()
    {
        GitRepository();
        GoingRun(new RunSign(RunPhase.Preparing));

        var (exit, output, _) = await RunAsync("status");
        Assert.Equal(3, exit);
        Assert.Equal("A run is going on: between tasks" + Environment.NewLine, output);
        Assert.Contains("\"phase\": \"between-tasks\"", (await RunAsync("status", "--json")).Output, StringComparison.Ordinal);

        GoingRun(Working with { Phase = RunPhase.WaitingForLimit, Until = new DateTimeOffset(2030, 1, 1, 14, 5, 0, TimeSpan.Zero) });

        (exit, output, _) = await RunAsync("status");
        Assert.Equal(3, exit);
        Assert.Equal(
            "A run is going on: #12 Export the orders as a file · waits for a usage limit until 2030-01-01 14:05 UTC · claude attach 1a2b3c4d" + Environment.NewLine,
            output);
        var json = (await RunAsync("status", "--json")).Output;
        Assert.Contains("\"phase\": \"waiting-for-limit\"", json, StringComparison.Ordinal);
        Assert.Contains("\"until\": \"2030-01-01T14:05:00Z\"", json, StringComparison.Ordinal);
    }

    [Fact]
    public async Task StatusDoesNotCountWhatARunThatDiedLeftBehind()
    {
        GitRepository();
        var local = Directory.CreateDirectory(Path.Combine(folder.FullName, ".toobusy", "local"));

        // No process of the machine has this number.
        var left = """{"process":2147483646,"since":"2030-01-01T09:00:00.0000000Z","phase":"working","task":12,"title":"Export the orders as a file","open":"claude attach 1a2b3c4d"}""";
        File.WriteAllText(Path.Combine(local.FullName, "run.json"), left);
        File.WriteAllText(Path.Combine(local.FullName, "session.json"), """{"task":12,"session":"1a2b3c4d","since":"2030-01-01T09:00:00.0000000Z","state":"working"}""");

        var (exit, output, _) = await RunAsync("status");

        Assert.Equal(0, exit);
        Assert.Equal(
            "No run is going on." + Environment.NewLine + "An earlier run left #12 with a session: the next run goes on with it." + Environment.NewLine,
            output);

        // It changes nothing: what was left is the next run's to put its own over.
        Assert.Equal(left, File.ReadAllText(Path.Combine(local.FullName, "run.json")));
    }

    [Fact]
    public async Task StatusOutsideAGitRepositoryFailsAsTheOtherCommandsDo()
    {
        var (exit, output, error) = await RunAsync("status");

        Assert.Equal(2, exit);
        Assert.Equal("", output);
        Assert.Equal("toobusy: not inside a git repository" + Environment.NewLine, error);
    }

    [Fact]
    public async Task StatusOfADemoShowsAMadeUpRunThatGoesOn()
    {
        GitRepository();

        var (exit, output, error) = await RunAsync("status", "--demo");

        Assert.Equal(3, exit);
        Assert.Equal("", error);
        Assert.Equal(
            "A run is going on: #102 Export the orders as a file · claude attach 1a2b3c4d" + Environment.NewLine + "Demo: the run is made up." + Environment.NewLine,
            output);

        var (code, json, _) = await RunAsync("status", "--demo", "--json");
        Assert.Equal(3, code);
        Assert.StartsWith("{", json, StringComparison.Ordinal);
        Assert.DoesNotContain("Demo", json, StringComparison.Ordinal);
        Assert.False(Directory.Exists(Path.Combine(folder.FullName, ".toobusy")));
    }

    [Fact]
    public async Task ARunDoesNotStartWhileOneGoesOnInTheWorkingCopy()
    {
        GitRepository();
        WriteSettings(Settings);
        await ChooseAllAsync();
        GoingRun(Working);
        var processes = new FakeProcesses((command, _) => new ProcessResult(ProcessStatus.Exited, 0, command == "git" ? "https://github.com/acme/rocket.git\n" : "", ""));

        var (exit, output, error) = await RunAsync(Context(processes), ["run"]);

        Assert.Equal(3, exit);
        Assert.Equal("", output);
        Assert.Equal(
            "toobusy: a run is going on in this working copy already: #12 Export the orders as a file · claude attach 1a2b3c4d" + Environment.NewLine
            + "The run did not start. `toobusy status` says whether that one still goes on." + Environment.NewLine,
            error);

        // It is said before anything is checked or asked of the tracker, and the sign of that run stays.
        Assert.DoesNotContain(processes.Asked, asked => asked.Command is "gh" or "claude");
        Assert.Equal(3, (await RunAsync("status")).Exit);
    }

    [Fact]
    public async Task ARunPutsItsSignUpWhileItGoesOnAndTakesItDownWhenItEnds()
    {
        GitRepository();
        WriteSettings(Settings);
        await ChooseAllAsync();

        // What `status` says while the run reads its tasks.
        (int Exit, string Output, string Error)? meanwhile = null;
        FakeProcesses? processes = null;
        processes = new FakeProcesses((command, arguments) => new ProcessResult(ProcessStatus.Exited, 0, command switch
        {
            "git" when arguments.Contains("remote") => "https://github.com/acme/rocket.git\n",
            "gh" when arguments[0] == "label" => Labels,
            "gh" when arguments is ["api", "graphql", ..] => Do(() => meanwhile = Look()) + "total\t0\n",
            _ => "",
        }, ""));

        var (exit, _, error) = await RunAsync(Context(processes), ["run"]);

        Assert.Equal("", error);
        Assert.Equal(0, exit);
        Assert.Equal((3, "A run is going on: between tasks" + Environment.NewLine, ""), meanwhile);
        Assert.Equal((0, "No run is going on." + Environment.NewLine, ""), Look());
        Assert.Equal([".gitignore"], new DirectoryInfo(Path.Combine(folder.FullName, ".toobusy", "local")).GetFiles().Select(file => file.Name));

        (int Exit, string Output, string Error) Look()
        {
            using var output = new StringWriter();
            using var failed = new StringWriter();
            var code = CliApp.RunAsync(["status"], Context(null) with { Output = output, Error = failed }, TestContext.Current.CancellationToken).GetAwaiter().GetResult();
            return (code, output.ToString(), failed.ToString());
        }
    }

    [Fact]
    public async Task ADemoOfARunIsNotHeldByARunThatGoesOnAndLeavesItsSignAlone()
    {
        GitRepository();
        GoingRun(Working);
        var sign = Path.Combine(folder.FullName, ".toobusy", "local", "run.json");
        var written = File.ReadAllText(sign);

        var (exit, output, error) = await RunAsync("run", "--demo", "--task", "101");

        Assert.Equal("", error);
        Assert.Equal(0, exit);
        Assert.Contains("✔ #101 Show the total of an order in its header — done", output, StringComparison.Ordinal);
        Assert.Equal(written, File.ReadAllText(sign));
    }

    [Fact]
    public async Task RunOfTheMenuOpensNoRunWhileOneGoesOnInTheWorkingCopy()
    {
        GitRepository();
        WriteSettings(Settings);
        await ChooseAllAsync();
        GoingRun(Working);
        var processes = new FakeProcesses((command, arguments) => new ProcessResult(ProcessStatus.Exited, 0, command switch
        {
            "git" => "https://github.com/acme/rocket.git\n",
            "gh" when arguments is ["api", "graphql", ..] => "total\t1\n12\tExport the orders as a file\thttps://github.com/acme/rocket/issues/12\t\t\t\t\tfeature\n",
            _ => "",
        }, ""));
        using var terminal = new TestTerminal(width: 120, height: 30);

        // `Run`, which opens nothing; then the last of the menu.
        terminal.Keys.Press(Keys.Enter, Keys.Up, Keys.Enter);
        var exit = await CliApp.RunAsync([], Context(processes) with { Output = terminal.Output, Terminal = terminal.Device }, TestContext.Current.CancellationToken);

        Assert.Equal(0, exit);
        terminal.AssertSaw("""
             A run is going on here already: #12 Export the orders as a file · claude attach 1a2b3c4d
             What to do
             ❯ Run        1 task
            """.ReplaceLineEndings("\n").TrimEnd(' '));
        Assert.False(terminal.Saw("Running the queue"));

        // Nothing was checked for a run that does not start.
        Assert.DoesNotContain(processes.Asked, asked => asked.Command == "claude");
    }

    // What `gh` answers of the boards of a user who can reach two, one of them linked to the repository.
    const string TwoBoards = "owner\tuser\tann\nlinked\thttps://github.com/orgs/acme/projects/1\tRocket\nboard\thttps://github.com/users/ann/projects/2\tGarden\n";

    // A machine with git and a logged-in `gh`, in a project cloned from acme/rocket: the repository has these
    // labels and these open milestones, the user reaches the two boards, and the second of them has statuses of
    // its own.
    static FakeProcesses GitHubForAssist(string labels = "", string milestones = "") => new((command, arguments) => new ProcessResult(ProcessStatus.Exited, 0, command switch
    {
        "git" => "https://github.com/acme/rocket.git\n",
        "gh" when arguments[0] == "label" => labels,
        "gh" when arguments[0] == "api" && arguments[1].StartsWith("repos/", StringComparison.Ordinal) => milestones,
        "gh" when arguments.Any(argument => argument.Contains("options { name }", StringComparison.Ordinal)) =>
            arguments.Contains("number=2") ? "board\nstatus\tBacklog\nstatus\tDone\n" : NewBoard,
        "gh" when arguments[0] == "api" => TwoBoards,
        _ => "",
    }, ""));

    // What is in the folders of the test, to see that a command left them as they were.
    string[] Files() => [.. new[] { folder, personal }.SelectMany(root => root.GetFileSystemInfos("*", SearchOption.AllDirectories)).Select(entry => entry.FullName).Order(StringComparer.Ordinal)];

    [Fact]
    public async Task AssistTellsTheIntroductionAndTheFirstStepWithoutATerminalAndAsksNothingOfTheTracker()
    {
        GitRepository();
        var processes = GitHubForAssist();
        var before = Files();

        var (exit, output, error) = await RunAsync(Context(processes), ["init", "--assist"]);

        Assert.Equal(0, exit);
        Assert.Equal("", error);
        Assert.StartsWith("# Setting a project up for toobusy\n", output, StringComparison.Ordinal);
        Assert.Contains("1. **The machine.** `toobusy doctor` tells; the `origin` remote is acme/rocket.\n2. **Which tasks are taken.** Not set: there are no settings yet.\n", output, StringComparison.Ordinal);
        Assert.EndsWith("Next: `toobusy init --assist 2`\n", output, StringComparison.Ordinal);
        Assert.DoesNotContain("\r", output, StringComparison.Ordinal);

        // The remote is read, and nothing else is asked of the machine.
        Assert.Equal(["git"], processes.Asked.Select(asked => asked.Command).Distinct());
        Assert.Equal(before, Files());
    }

    [Fact]
    public async Task AssistTellsTheStandingOfAProjectThatIsSetUp()
    {
        GitRepository();
        WriteSettings(SettingsWithABoard + SkillOfTheProject + "\n[task]\nbefore = \"scripts/dev.sh ports free\"\n");
        WriteSkill("task-auto");
        await ChooseAllAsync();
        var before = Files();

        var (exit, output, _) = await RunFromAsync(folder.CreateSubdirectory("src").FullName, "init", "--assist");

        Assert.Equal(0, exit);
        Assert.Contains(
            """
            2. **Which tasks are taken.** Take: `feature`, `bug`; blocking: `draft`; owner: `needs-owner`; interrupted: `interrupted`.
            3. **The board.** The board is https://github.com/orgs/acme/projects/1.
            4. **The owner's own choices.** Milestone: no milestone; model: the assistant's own; effort: high.
            5. **The instructions of the project.** The project's own; toobusy does not read them.
            6. **The skill a session starts with.** `task-auto`, written.
            7. **The commands around a task.** Before: `scripts/dev.sh ports free`; after: none.
            """.ReplaceLineEndings("\n"),
            output,
            StringComparison.Ordinal);
        folder.GetDirectories("src")[0].Delete();
        Assert.Equal(before, Files());
    }

    [Fact]
    public async Task AssistSaysThatTheSetupWaitsForARunThatGoesOn()
    {
        GitRepository();
        GoingRun(Working);

        var (exit, output, _) = await RunAsync("init", "--assist");

        Assert.Equal(0, exit);
        Assert.Contains("**A run is going on in this working copy: #12 Export the orders as a file · claude attach 1a2b3c4d.** The setup waits for it", output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AStepOfAssistReadsFromTheTrackerWhatItChoosesAmongAndChangesNothing()
    {
        GitRepository();
        WriteSettings(SettingsWithABoard);
        var processes = GitHubForAssist(labels: "bug\nfeature\ndraft\nquestion\n", milestones: "v2\t\t1\nv1\t2030-03-01T00:00:00Z\t12\n");
        var before = Files();
        var settings = File.ReadAllText(Path.Combine(folder.FullName, ".toobusy", "settings.toml"));

        var (exit, labels, error) = await RunAsync(Context(processes), ["init", "--assist", "2"]);
        Assert.Equal(0, exit);
        Assert.Equal("", error);
        Assert.StartsWith("## Step 2 of 9: Which tasks are taken\n\nAs things are: the labels to take: `feature`, `bug`;", labels, StringComparison.Ordinal);
        Assert.Contains("The labels of acme/rocket: `bug`, `feature`, `draft`, `question`. The settings name `needs-owner`, `interrupted`, which the repository does not have.", labels, StringComparison.Ordinal);
        Assert.Contains("    toobusy init --yes --take-label feature,bug --blocking-label draft --owner-label needs-owner --interrupt-label interrupted\n", labels, StringComparison.Ordinal);
        Assert.EndsWith("Next: `toobusy init --assist 3`\n", labels, StringComparison.Ordinal);
        Assert.DoesNotContain("# Setting a project up", labels, StringComparison.Ordinal);

        var (_, boards, _) = await RunAsync(Context(processes), ["init", "--assist", "3"]);
        Assert.Contains(
            "- Rocket, https://github.com/orgs/acme/projects/1, linked to acme/rocket: Todo, In Progress, Done\n"
            + "- Garden, https://github.com/users/ann/projects/2: Backlog, Done. **A run needs three statuses.**\n",
            boards,
            StringComparison.Ordinal);

        var (_, milestones, _) = await RunAsync(Context(processes), ["init", "--assist", "4", "--demo"]);
        Assert.Contains("The open milestones of acme/rocket, each with its open tasks:\n\n- v1: 12 tasks, due 2030-03-01\n- v2: 1 task\n", milestones, StringComparison.Ordinal);

        // Nothing is asked of GitHub but what is read: no label and no board is made, and nothing is linked.
        Assert.DoesNotContain(processes.Asked, asked => asked.Arguments.Any(argument =>
            argument is "create" or "edit" or "delete" || argument.Contains("mutation", StringComparison.Ordinal) || argument is "POST" or "PATCH" or "PUT" or "DELETE"));
        Assert.DoesNotContain(processes.Asked, asked => asked.Command == "claude");
        Assert.Equal(before, Files());
        Assert.Equal(settings, File.ReadAllText(Path.Combine(folder.FullName, ".toobusy", "settings.toml")));
    }

    [Fact]
    public async Task TheBoardOfTheSettingsStandsAmongTheBoardsOfAStepWhenTheUserDoesNotReachIt()
    {
        GitRepository();
        WriteSettings(SettingsWithABoard.Replace("projects/1", "projects/7", StringComparison.Ordinal));

        var (_, output, _) = await RunAsync(Context(GitHubForAssist()), ["init", "--assist", "3"]);

        Assert.Contains("each with what it calls its statuses:\n\n- https://github.com/orgs/acme/projects/7: Todo, In Progress, Done\n- Rocket, ", output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AStepOfAssistIsToldWithoutATrackerThatCannotBeRead()
    {
        GitRepository();

        // No command runs on this machine: `gh` is not there.
        var (exit, output, error) = await RunAsync("init", "--assist", "2");

        Assert.Equal(0, exit);
        Assert.Equal("", error);
        Assert.Contains("The labels of the repository cannot be read: the GitHub command-line tool `gh` is not installed. Step 1 puts it right: `toobusy init --assist 1`. The step is told without them.", output, StringComparison.Ordinal);
        Assert.Contains("    toobusy init --yes --no-board --no-skill --take-label", output, StringComparison.Ordinal);

        var loggedOut = new FakeProcesses((command, arguments) => command == "gh" && arguments[0] == "auth"
            ? new ProcessResult(ProcessStatus.Exited, 1, "", "")
            : new ProcessResult(ProcessStatus.Exited, 0, command == "git" ? "https://github.com/acme/rocket.git\n" : "", ""));
        var (_, boards, _) = await RunAsync(Context(loggedOut), ["init", "--assist", "3"]);
        Assert.Contains("The boards cannot be read: `gh` is not logged in to github.com. Step 1 puts it right", boards, StringComparison.Ordinal);

        var elsewhere = new FakeProcesses(command => new ProcessResult(ProcessStatus.Exited, 0, command == "git" ? "https://gitlab.com/acme/rocket.git\n" : "", ""));
        var (_, milestones, _) = await RunAsync(Context(elsewhere), ["init", "--assist", "4"]);
        Assert.Contains("The open milestones cannot be read: the `origin` remote is not a GitHub repository. Step 1 puts it right", milestones, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AStepOfAssistStartsWithTheErrorsOfSettingsThatDoNotValidate()
    {
        GitRepository();
        WriteSettings(Settings.Replace("type = \"github\"", "type = \"jira\"", StringComparison.Ordinal));

        var (exit, output, _) = await RunAsync("init", "--assist", "6");

        Assert.Equal(0, exit);
        Assert.StartsWith("## Step 6 of 9: The skill a session starts with\n\n**The settings of this project do not validate:**\n\n- .toobusy/settings.toml:4: tracker.type: ", output, StringComparison.Ordinal);
        Assert.Contains("Put `.toobusy/settings.toml` right by hand first", output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ANumberAfterTheLastStepOfAssistSaysThatTheSetupIsCompleteAndNothingElse()
    {
        GitRepository();

        var (exit, output, error) = await RunAsync("init", "--assist", "10");

        Assert.Equal(0, exit);
        Assert.Equal("", error);
        Assert.Equal("All the steps are done: the setup is complete.\n", output);
    }

    [Theory]
    [InlineData("toobusy: `--assist` takes the number of a step, which is 1 or more", "--assist", "0")]
    [InlineData("toobusy: `--assist` takes the number of a step, which is 1 or more", "--assist", "two")]
    [InlineData("toobusy: `--assist` takes the number of a step, which is 1 or more", "--assist", "1.5")]
    [InlineData("toobusy: `--assist` goes with no other option of `init`: it tells the steps and sets nothing", "--assist", "--yes")]
    [InlineData("toobusy: `--assist` goes with no other option of `init`: it tells the steps and sets nothing", "--assist", "3", "--no-board")]
    [InlineData("toobusy: `--assist` goes with no other option of `init`: it tells the steps and sets nothing", "--skill", "task-auto", "--assist")]
    public async Task AssistWithANumberThatIsNotOneOrWithAnotherOptionIsAWrongCommandLine(string message, params string[] options)
    {
        GitRepository();

        var (exit, output, error) = await RunAsync(["init", .. options]);

        Assert.Equal(2, exit);
        Assert.Equal("", output);
        Assert.Equal(message + Environment.NewLine, error);
        Assert.False(Directory.Exists(Path.Combine(folder.FullName, ".toobusy")));
    }

    [Fact]
    public async Task AssistWithANegativeNumberIsAWrongCommandLineToo()
    {
        GitRepository();

        var (exit, output, _) = await RunAsync("init", "--assist", "-1");

        Assert.Equal(2, exit);
        Assert.DoesNotContain("Step", output, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("--assist")]
    [InlineData("--assist", "3")]
    [InlineData("--assist", "10")]
    public async Task AssistNeedsAGitRepositoryAsInitDoes(params string[] options)
    {
        var (exit, output, error) = await RunAsync(["init", .. options]);

        Assert.Equal(2, exit);
        Assert.Equal("", output);
        Assert.Equal("toobusy: not inside a git repository" + Environment.NewLine, error);
    }

    [Fact]
    public async Task InATerminalAssistPrintsTheSameAndOpensNoScreen()
    {
        GitRepository();
        var (_, plain, _) = await RunAsync("init", "--assist", "5");

        var (exit, output, _) = await RunWithKeysAsync(new Keys(), "init", "--assist", "5");

        Assert.Equal(0, exit);
        Assert.Equal(plain, output);
    }

    [Fact]
    public async Task ADemoChangesNothingOfAssist()
    {
        GitRepository();
        var (_, plain, _) = await RunAsync("init", "--assist");

        var (exit, output, _) = await RunAsync("init", "--assist", "--demo");

        Assert.Equal(0, exit);
        Assert.Equal(plain, output);
    }

    // The projects the steps are told over: one that toobusy has never seen, one whose settings do not validate,
    // and one that is set up, with a board, a skill and commands around a task, and with labels that need quotes.
    public static TheoryData<string> ProjectsOfAssist =>
    [
        "",
        Settings.Replace("type = \"github\"", "type = \"jira\"", StringComparison.Ordinal),
        Settings,
        SettingsWithABoard.Replace("take = [\"feature\", \"bug\"]", "take = [\"feature\", \"good first issue\"]", StringComparison.Ordinal) + SkillOfTheProject + "\n[task]\nbefore = \"scripts/dev.sh ports free\"\nafter = \"scripts/dev.sh server stop\"\n",
    ];

    [Theory]
    [MemberData(nameof(ProjectsOfAssist))]
    public async Task EveryCommandAStepOfAssistNamesIsOneTheToolHas(string settings)
    {
        GitRepository();
        if (settings.Length > 0)
            WriteSettings(settings);
        var processes = GitHubForAssist(labels: "bug\nfeature\n", milestones: "v1\t\t3\n");
        var root = CliApp.CreateRootCommand(Context(null));

        var texts = new List<string> { (await RunAsync(Context(processes), ["init", "--assist"])).Output };
        for (var step = 1; step <= 10; step++)
            texts.Add((await RunAsync(Context(processes), ["init", "--assist", $"{step}"])).Output);

        // A command stands on a line of its own in a block of code, or between backticks in a sentence.
        var commands = texts
            .SelectMany(text => Regex.Matches(text, @"^    (toobusy .+)$|`(toobusy [^`]+)`", RegexOptions.Multiline))
            .Select(match => match.Groups[1].Success ? match.Groups[1].Value : match.Groups[2].Value)
            .Distinct()
            .ToList();

        Assert.True(commands.Count > 25, string.Join('\n', commands));
        Assert.Contains(commands, command => command.EndsWith(" --before \"<command>\" --after \"<command>\"", StringComparison.Ordinal));
        Assert.Contains("toobusy queue --json", commands);
        foreach (var command in commands)
        {
            var parsed = root.Parse([.. System.CommandLine.Parsing.CommandLineParser.SplitCommandLine(command).Skip(1)]);
            Assert.True(parsed.Errors.Count == 0, $"{command}: {string.Join("; ", parsed.Errors.Select(wrong => wrong.Message))}");
            Assert.Empty(parsed.UnmatchedTokens);
        }
    }

    [Fact]
    public async Task TheHelpOfTheToolAndOfInitSayWhereAnAssistantStarts()
    {
        const string Said = "An assistant that sets a project up for its owner starts with `toobusy init --assist`.";

        var (_, tool, _) = await RunAsync("--help");
        var (_, init, _) = await RunAsync("init", "--help");

        Assert.Contains(Said, Regex.Replace(tool, @"\s+", " "), StringComparison.Ordinal);
        Assert.Contains(Said, Regex.Replace(init, @"\s+", " "), StringComparison.Ordinal);
        Assert.Contains("--assist <step>", init, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AboutNamesAssistAtItsHead()
    {
        var (_, output, _) = await RunAsync("about");

        Assert.Contains("An assistant that sets a project up for its owner starts with `toobusy init --assist`", output.Split("## A run")[0], StringComparison.Ordinal);
    }

    [Fact]
    public async Task InATerminalTheNotSetUpMessageDoesNotSpeakToAnAssistant()
    {
        GitRepository();

        var (_, _, error) = await RunWithKeysAsync(new Keys(), "run");

        Assert.Equal("toobusy: this project is not set up yet." + Environment.NewLine + "Run `toobusy init` to set it up." + Environment.NewLine, error);
    }

    // A task that a run works on, as its sign says it.
    static readonly RunSign Working = new(RunPhase.Working)
    {
        Task = 12,
        Title = "Export the orders as a file",
        Url = "https://github.com/acme/rocket/issues/12",
        Open = "claude attach 1a2b3c4d",
    };

    // The sign of a run that goes on in the project: the process of the test is the one that lives.
    void GoingRun(RunSign sign)
    {
        var file = new TooBusy.Infrastructure.Run.RunSignFile(TooBusy.Infrastructure.Run.LocalFolder.Ensure(folder.FullName));
        Assert.Null(file.Raise(sign));
    }

    void GitRepository() => folder.CreateSubdirectory(".git");

    // A skill of the project, where Claude Code reads it.
    void WriteSkill(string name) =>
        File.WriteAllText(Path.Combine(folder.CreateSubdirectory(Path.Combine(".claude", "skills", name)).FullName, "SKILL.md"), $"---\nname: {name}\n---\n");

    void WriteSettings(string text) =>
        File.WriteAllText(Path.Combine(folder.CreateSubdirectory(".toobusy").FullName, "settings.toml"), text);

    Task<(int Exit, string Output, string Error)> RunAsync(params string[] args) => RunFromAsync(folder.FullName, args);

    Task<(int Exit, string Output, string Error)> RunFromAsync(string from, params string[] args) => RunAsync(Context(null) with { Folder = from }, args);

    Task<(int Exit, string Output, string Error)> RunWithKeysAsync(Keys keys, params string[] args) => RunWithKeysAsync(keys, null, args);

    // Runs in a terminal of a hundred and twenty columns and thirty lines.
    Task<(int Exit, string Output, string Error)> RunWithKeysAsync(Keys keys, FakeProcesses? processes, params string[] args) =>
        RunAsync(Context(processes) with { Terminal = new TerminalDevice(keys.Read, () => (120, 30)) }, args);

    // Runs in a terminal that says whether a key is pressed, as the page of a run asks it: the run goes on by
    // itself there, and the keys are held back until it has got where the test waits for it.
    Task<(int Exit, string Output, string Error)> RunLiveAsync(Keys keys, FakeProcesses? processes, params string[] args) =>
        RunAsync(Context(processes) with { Terminal = new TerminalDevice(keys.Read, () => (120, 30)) { KeyWaiting = () => keys.Waiting } }, args);

    // The project lies right under the home folder, and the choices of the user are kept in a folder of the test.
    // A run does not make the test wait: its clock goes on by itself.
    CliContext Context(FakeProcesses? processes) => new(folder.FullName, TextWriter.Null, TextWriter.Null)
    {
        Home = folder.Parent!.FullName,
        Processes = processes,
        PersonalFolder = personal.FullName,
        Clock = new InstantClock(),
    };

    // Chooses everything a run needs: no milestone, the assistant's own model and the proposed effort.
    async Task ChooseAllAsync()
    {
        await RunAsync("milestone", "--none");
        await RunAsync("model", "--default");
        await RunAsync("effort", "high");
    }

    // What `toobusy milestone` prints without a terminal, and its exit code.
    async Task<(string Output, int Exit)> ShownAsync(FakeProcesses? processes)
    {
        var (exit, output, _) = await RunAsync(Context(processes), ["milestone"]);
        return (output, exit);
    }

    // What `gh` answers of the statuses of a board that has those GitHub gives a new one.
    const string NewBoard = "board\nstatus\tTodo\nstatus\tIn Progress\nstatus\tDone\n";

    // A machine with git and a logged-in `gh`: the project is cloned from acme/rocket, the user can make boards for
    // themselves, the repository has the given milestones and labels, and every board the given statuses.
    static FakeProcesses GitHub(string milestones, string labels = "", string statuses = NewBoard) => new((command, arguments) => new ProcessResult(ProcessStatus.Exited, 0, command switch
    {
        "git" => "https://github.com/acme/rocket.git\n",
        "gh" when arguments[0] == "label" => labels,
        "gh" when arguments[0] == "api" && arguments[1].StartsWith("repos/", StringComparison.Ordinal) => milestones,
        "gh" when arguments.Any(argument => argument.Contains("options { name }", StringComparison.Ordinal)) => statuses,
        "gh" when arguments[0] == "api" => "owner\tuser\tann\n",
        _ => "",
    }, ""));

    // A machine whose `gh` answers the request for the open tasks with these, in a project cloned from acme/rocket.
    static FakeProcesses Tracker(string tasks) => new((command, arguments) => new ProcessResult(ProcessStatus.Exited, 0, command switch
    {
        "git" => "https://github.com/acme/rocket.git\n",
        "gh" when arguments is ["api", "graphql", ..] => tasks,
        _ => "",
    }, ""));

    // The same machine, whose `gh` answers what is asked about projects too: a board can be read, made for the
    // user and linked to the repository.
    static FakeProcesses GitHubWithProjects(string labels, string statuses = NewBoard) => new((command, arguments) => new ProcessResult(ProcessStatus.Exited, 0, command switch
    {
        "git" => "https://github.com/acme/rocket.git\n",
        "gh" when arguments[0] == "label" => labels,
        "gh" when arguments.Any(argument => argument.Contains("linkProjectV2ToRepository", StringComparison.Ordinal)) => "R_1\n",
        "gh" when arguments.Any(argument => argument.Contains("createProjectV2", StringComparison.Ordinal)) => "https://github.com/users/ann/projects/9\n",
        "gh" when arguments.Any(argument => argument.Contains("projectV2(number:", StringComparison.Ordinal) && argument.Contains("repository(owner:", StringComparison.Ordinal)) => "P_1\tR_1\n",
        "gh" when arguments.Any(argument => argument.Contains("options { name }", StringComparison.Ordinal)) => statuses,
        "gh" when arguments.Any(argument => argument.Contains("projectV2(number:", StringComparison.Ordinal)) => "P_1\n",
        "gh" when arguments.Any(argument => argument.Contains("(login: $login) { id }", StringComparison.Ordinal)) => "U_1\n",
        "gh" when arguments[0] == "api" => "owner\tuser\tann\n",
        _ => "",
    }, ""));

    // A machine whose `gh` is logged in and can make a board for the user, in a project whose `origin` remote is
    // not a GitHub repository.
    static FakeProcesses ElsewhereWithProjects() => new((command, arguments) => new ProcessResult(ProcessStatus.Exited, 0, command switch
    {
        "git" => "https://gitlab.com/acme/rocket.git\n",
        "gh" when arguments.Any(argument => argument.Contains("createProjectV2", StringComparison.Ordinal)) => "https://github.com/users/ann/projects/9\n",
        "gh" when arguments.Any(argument => argument.Contains("(login: $login) { id }", StringComparison.Ordinal)) => "U_1\n",
        "gh" when arguments[0] == "api" => "owner\tuser\tann\n",
        _ => "",
    }, ""));

    // Does something while a command is answered, and answers with nothing.
    static string Do(Action action)
    {
        action();
        return "";
    }

    // What is left in the terminal when the screen is closed.
    static string Report(string output) => output.Split(Screen.Leave)[^1];

    async Task<(int Exit, string Output, string Error)> RunAsync(CliContext context, string[] args)
    {
        using var output = new StringWriter();
        using var error = new StringWriter();
        shown = output;
        var exit = await CliApp.RunAsync(args, context with { Output = output, Error = error }, TestContext.Current.CancellationToken);
        return (exit, output.ToString(), error.ToString());
    }

    // Whether the page has shown the text by now, so many times: what keys that are held back wait for.
    bool Shown(string text, int times = 1) => shown is not null && shown.ToString().Split(text).Length > times;

    // A clock whose time passes as the run asks for it but twenty times faster than the real one: a demo is over in
    // seconds, and a key that is held back for a moment of it still finds the run where it waits for it.
    sealed class PacedClock : IClock
    {
        long ticks = InstantClock.Start.UtcTicks;

        public DateTimeOffset Now => new(Interlocked.Read(ref ticks), TimeSpan.Zero);

        public async Task DelayAsync(TimeSpan time, CancellationToken cancellationToken)
        {
            await Task.Delay(time / 20, cancellationToken);
            Interlocked.Add(ref ticks, time.Ticks);
        }
    }

    // Answers every command as it is told to and remembers what it was asked.
    sealed class FakeProcesses(Func<string, IReadOnlyList<string>, ProcessResult> answer) : IProcessRunner
    {
        public FakeProcesses(Func<string, ProcessResult> answer)
            : this((command, _) => answer(command))
        {
        }

        public List<(string Command, IReadOnlyList<string> Arguments)> Asked { get; } = [];

        public Task<ProcessResult> RunAsync(string command, IReadOnlyList<string> arguments, TimeSpan timeout, CancellationToken cancellationToken)
        {
            Asked.Add((command, arguments));
            return Task.FromResult(answer(command, arguments));
        }

        public IProcessRunner Inside(string folder) => this;
    }
}
