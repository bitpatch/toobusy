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
        Assert.Contains(Briefing.Task(new(12, "<the title of the task>", "<the address of the task>", [], BoardStatus.Todo, "Todo", [], []), interrupted: false), output, StringComparison.Ordinal);
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
    public async Task WithoutSettingsTheProjectIsNotSetUp(params string[] args)
    {
        GitRepository();

        var (exit, output, error) = await RunAsync(args);

        Assert.Equal(2, exit);
        Assert.Equal("", output);
        Assert.Equal(
            "toobusy: this project is not set up yet." + Environment.NewLine + "Run `toobusy init` to set it up." + Environment.NewLine,
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
        Assert.StartsWith("Demo: nothing is changed." + Environment.NewLine + "✻ v0.1.0 (made up) · 5 tasks in the queue", output, StringComparison.Ordinal);
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
                " ✔ #101 Show the total of an order in its header  0:09",
                " ✔ #102 Export the orders as a file  0:21",
                " ◐ #103 Fix the rounding of a discount  0:10",
                " ✔ #104 Update the dependencies  0:16",
                " ✔ #105 Describe the export in the manual  0:07",
                "",
                " 5 tasks in 63 s · 4 done · 1 done in part",
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
            ("gh", "api") => "total\t1\n12\tExport the data\thttps://github.com/acme/rocket/issues/12\t\t\t\tfeature\n",
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
        Assert.Equal(" ■ Killed: #12 stays In Progress · claude attach abc12345 goes on with it", rows[3]);
        Assert.Equal(4, rows.Count);
        Assert.Contains(processes.Asked, asked => asked.Command == "claude" && asked.Arguments.Contains("stop"));
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
            ✔ labels

            """.ReplaceLineEndings(),
            output);
        Assert.Contains(processes.Asked, asked => asked.Command == "claude" && asked.Arguments.SequenceEqual(["auth", "status"]));
        Assert.Contains(processes.Asked, asked => asked.Command == "gh" && asked.Arguments.SequenceEqual(["api", "repos/acme/rocket", "--jq", ".full_name"]));
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
                "○ labels             waits for the settings",
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
            ["✔ git", "✔ GitHub CLI", "✔ GitHub login", "✔ Claude Code", "✔ Claude Code login", "✔ settings           .toobusy/settings.toml", "✔ repository         acme/rocket", "○ board              the project has no board", "✔ labels"],
            Scroll.Read(output).Rows.Where(row => row.Length > 0));
    }

    [Fact]
    public async Task DoctorOfADemoChecksAMadeUpMachineThatHasItAll()
    {
        GitRepository();

        var (exit, output, _) = await RunAsync("doctor", "--demo");

        Assert.Equal(0, exit);
        Assert.DoesNotContain("✘", output, StringComparison.Ordinal);
        Assert.Contains("✔ labels" + Environment.NewLine + "Demo: the checks were made up." + Environment.NewLine, output, StringComparison.Ordinal);
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
            ("gh", "api") => closed ? "total\t0\n" : "total\t1\n12\tExport the data\thttps://github.com/acme/rocket/issues/12\t\t\t\tfeature\n",
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
            ✔ #12 Export the data — done in 0 s
              #12 is closed, with the report of the session as its comment
            ✔ No task is left · 1 done
              Worked 0 s

            """.ReplaceLineEndings(),
            output);

        var started = processes.Asked.Single(asked => asked.Command == "claude" && asked.Arguments[0] == "--bg").Arguments;
        Assert.Equal(["--effort", "high", "-n", "#12 Export the data"], started.Skip(7).Take(4));
        Assert.Equal(Briefing.Task(new(12, "Export the data", "https://github.com/acme/rocket/issues/12", ["feature"], BoardStatus.Todo, "Todo", [], []), interrupted: false), started[^1]);

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
        // by the name that is proposed; yes; the first milestone; the model after the assistant's own; the proposed
        // effort; the last of the menu.
        var keys = new Keys().Press(
            Keys.Tab, Keys.Tab, Keys.Enter, Keys.Enter, Keys.Enter,
            Keys.Enter, Keys.Enter, Keys.Enter, Keys.Enter,
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
            "gh" when arguments[1] == "graphql" => "total\t3\n12\tExport the data\thttps://github.com/acme/rocket/issues/12\t\t\t\tfeature\n"
                + "13\tDescribe the export\thttps://github.com/acme/rocket/issues/13\t\t12\t\n14\tMove the site\thttps://github.com/acme/rocket/issues/14\t\t\t\tmanual\n",
            _ => "",
        }, ""));

        var (exit, output, _) = await RunWithKeysAsync(new Keys().Press(Keys.Up, Keys.Enter), processes, []);

        Assert.Equal(0, exit);
        Assert.Contains("❯ Run        2 tasks", output, StringComparison.Ordinal);
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
    public async Task BackOfTheSettingsGoesToTheMenuAsEscapeDoes()
    {
        GitRepository();
        WriteSettings(Settings);
        await ChooseAllAsync();

        // Settings; the tab without a project, and `Back` under it; the last of the menu.
        var keys = new Keys().Press(Keys.Down, Keys.Down, Keys.Down, Keys.Enter, Keys.Tab, Keys.Down, Keys.Enter, Keys.Up, Keys.Enter);

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

        // Straight to the menu: the run, which comes back to the menu by itself when it is over, and out.
        var keys = new Keys()
            .Hold(() => Shown("5 tasks")).Press(Keys.Enter)
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
        Assert.Contains("❯ Run        5 tasks", output, StringComparison.Ordinal);

        // The menu that the run comes back to says how the run went.
        Assert.Contains("5 tasks in 63 s · 4 done · 1 done in part", output.Split("What to do")[^2], StringComparison.Ordinal);

        // The screen is left for the run and entered again for the menu; the tasks of the run stay in the history
        // of the terminal, and only what else there is to say is printed under them.
        Assert.Equal(2, output.Split("\u001b[?1049h").Length - 1);
        Assert.Equal(
            [
                " ✔ #101 Show the total of an order in its header  0:09",
                " ✔ #102 Export the orders as a file  0:21",
                " ◐ #103 Fix the rounding of a discount  0:10",
                " ✔ #104 Update the dependencies  0:16",
                " ✔ #105 Describe the export in the manual  0:07",
                " 5 tasks in 63 s · 4 done · 1 done in part",
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

        // The run is aborted while the first task is worked on. The menu it comes back to says which task was
        // interrupted instead of the number of tasks; Run takes that task first, and when the run is over the tasks
        // are counted as they were.
        var keys = new Keys()
            .Hold(() => Shown("5 tasks")).Press(Keys.Enter)
            .Hold(() => Shown("Read src/Orders/Order.cs")).Type("/")
            .Hold(() => Shown("❯ stop")).Press(Keys.Down, Keys.Enter)
            .Hold(() => Shown("interrupted: #101 Show the total of an order in its header")).Press(Keys.Enter)
            .Hold(() => Shown("5 tasks", times: 2)).Press(Keys.Up, Keys.Enter);
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
        Assert.False(Directory.Exists(Path.Combine(folder.FullName, ".toobusy")));
        Assert.False(File.Exists(Path.Combine(personal.FullName, "projects.toml")));
    }

    [Fact]
    public async Task ADemoOfTheRunNeedsNoSettingsEither()
    {
        GitRepository();

        var (exit, output, _) = await RunAsync("run", "--demo");

        // The whole of a demo: a task that is done, a session that asks and is told to go on alone, a task done in
        // part whose rest is a new task for the owner, a usage limit that is waited out, a task that opened after
        // another one, and the tasks that are held.
        Assert.Equal(0, exit);
        Assert.Equal(
            """
            Demo: nothing is changed.
            ✻ v0.1.0 (made up) · 5 tasks in the queue · the assistant's own model · high effort
              Opens later: #105 after #102
              Held: #106 it has the manual label · #107 its status is In Progress
            → #101 Show the total of an order in its header — started · a new session · claude attach 4f2a101 (made up)
              #101 is In Progress on the board; the session is told that toobusy keeps the tracker
            ✔ #101 Show the total of an order in its header — done in 9 s
              #101 is closed, with the report of the session as its comment
            → #102 Export the orders as a file — started · a new session · claude attach 4f2a102 (made up)
              #102 is In Progress on the board; the session is told that toobusy keeps the tracker
            ‖ #102 waits for the owner: input needed · it is told to go on alone in 10 s: /nudge does it now, /hold never · claude attach 4f2a102 (made up)
              │ The task does not say what kind of file the export is. Should it be CSV or JSON?
            ▶ #102 is told to go on without the owner: its turn is cut, and this is the next message of its conversation · claude attach 4f2a102 (made up)
              “The owner is away: no answer to your question and no approval will come. Decide yourself and go on with task #102 from where you stopped. What cannot be decided without the owner goes into the new task for the owner, as the rules of this session say. End your reply with a `TOOBUSY:` line.”
            ✔ #102 Export the orders as a file — done in 21 s
              #102 is closed, with the report of the session as its comment
            → #103 Fix the rounding of a discount — started · a new session · claude attach 4f2a103 (made up)
              #103 is In Progress on the board; the session is told that toobusy keeps the tracker
            ◐ #103 Fix the rounding of a discount — done in part in 10 s
              #103 is closed; what is left is #108 “Decide what a discount of more than the price does”, which waits for the owner with the needs-owner label
            → #104 Update the dependencies — started · a new session · claude attach 4f2a104 (made up)
              #104 is In Progress on the board; the session is told that toobusy keeps the tracker
            ‖ #104 ran into a usage limit: You have hit your usage limit.
            ‖ #104 waits 7 s for the 5-hour limit to reset, then goes on · /stop pauses it for the next run
            ▶ #104 goes on after the reset of the limit · claude attach 4f2a104 (made up)
            ✔ #104 Update the dependencies — done in 16 s
              #104 is closed, with the report of the session as its comment
            → #105 Describe the export in the manual — started · a new session · claude attach 4f2a105 (made up)
              #105 is In Progress on the board; the session is told that toobusy keeps the tracker
            ✔ #105 Describe the export in the manual — done in 7 s
              #105 is closed, with the report of the session as its comment
              Held: #106 it has the manual label · #107 its status is In Progress · #108 it waits for the owner: it has the needs-owner label
            ✔ No task is left · 5 done
              Worked 63 s

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
        // a new label for interrupted tasks, by the name that is proposed; yes.
        var keys = new Keys().Press(
            Keys.Tab, Keys.Tab, Keys.Enter, Keys.Down, Keys.Space, Keys.Enter, Keys.Enter,
            Keys.Up, Keys.Enter, Keys.Enter, Keys.Enter, Keys.Enter);

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
            $"{Palette.Dark.Muted("toobusy:")} this project is not set up yet.{Environment.NewLine}Run {Palette.Dark.Accent("`toobusy init`")} to set it up.{Environment.NewLine}",
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
    public async Task InitNeedsATerminal()
    {
        GitRepository();

        var (exit, _, error) = await RunAsync("init");

        Assert.Equal(1, exit);
        Assert.Contains("needs a terminal", error, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ADemoOfTheSetupGoesThroughTheStepsAndWritesNothing()
    {
        GitRepository();

        // The first of the boards, no blocking labels, any label, a new label for the owner by the name that is
        // proposed, the label `interrupted` of the made-up ones, yes.
        var keys = new Keys().Press(Keys.Enter, Keys.Enter, Keys.Enter, Keys.Enter, Keys.Enter, Keys.Enter, Keys.Enter);

        var (exit, output, error) = await RunWithKeysAsync(keys, "init", "--demo");

        Assert.Equal(0, exit);
        Assert.Equal("", error);
        Assert.StartsWith("\u001b[?1049h", output, StringComparison.Ordinal);
        Assert.Contains("Setting up this project · demo", output, StringComparison.Ordinal);
        Assert.Contains("The answers above will be written to .toobusy/settings.toml.", output, StringComparison.Ordinal);
        Assert.Contains("The label “needs-owner” will be made in example/project.", output, StringComparison.Ordinal);
        Assert.DoesNotContain("[tracker]", output, StringComparison.Ordinal);
        Assert.Equal(
            [
                $"toobusy · Setting up this project · ~{Path.DirectorySeparatorChar}{folder.Name}",
                "✔ Tracker          GitHub",
                "✔ Project          Rocket (made up)  https://github.com/users/example/projects/1",
                "✔ Blocking labels  none",
                "✔ Labels to take   any task",
                "✔ Owner's label    needs-owner",
                "✔ Interrupt label  interrupted",
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
        var keys = new Keys().Press(Keys.Tab, Keys.Tab, Keys.Tab, Keys.Enter, Keys.Enter, Keys.Enter, Keys.Enter, Keys.Enter);

        var (exit, output, _) = await RunWithKeysAsync(keys, "init", "--demo");

        Assert.Equal(0, exit);
        Assert.Contains("✔ Project          none", Report(output), StringComparison.Ordinal);
        Assert.Contains("✔ Blocking labels  manual", Report(output), StringComparison.Ordinal);
        Assert.Contains("✔ Owner's label    manual", Report(output), StringComparison.Ordinal);
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
        // proposed, the label `interrupted` of the made-up ones, yes.
        var keys = new Keys().Press(Keys.Enter, Keys.Enter, Keys.Enter, Keys.Enter, Keys.Enter, Keys.Enter, Keys.Enter);

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
    public async Task ADemoOfTheSetupGoesWithMadeUpBoardsWhenTheRealOnesCannotBeRead()
    {
        GitRepository();
        var processes = new FakeProcesses(_ => new ProcessResult(ProcessStatus.NotFound, 0, "", ""));
        var keys = new Keys().Press(Keys.Down, Keys.Enter, Keys.Enter, Keys.Enter, Keys.Enter, Keys.Enter, Keys.Enter);

        var (exit, output, _) = await RunWithKeysAsync(keys, processes, "init", "--demo");

        Assert.Equal(0, exit);
        Assert.Contains("The `origin` remote is not a GitHub repository", output, StringComparison.Ordinal);
        Assert.Contains("✔ Project          Moon base (made up)  https://github.com/users/example/projects/2", Report(output), StringComparison.Ordinal);
    }

    [Fact]
    public async Task ADemoOfTheSetupOffersEnoughOwnersToScroll()
    {
        GitRepository();
        var processes = new FakeProcesses(command => new ProcessResult(ProcessStatus.Exited, 0, command == "git"
            ? "https://github.com/bitpatch/toobusy.git\n"
            : "owner\torg\tbitpatch\nowner\tuser\tdenis\n", ""));
        var keys = new Keys().Press(Keys.Tab, Keys.Tab).Type("Rocket").Press(Keys.Down, Keys.Down, Keys.Enter, Keys.Enter, Keys.Enter, Keys.Enter, Keys.Enter, Keys.Enter, Keys.Enter);

        var (exit, output, _) = await RunWithKeysAsync(keys, processes, "init", "--demo");

        Assert.Equal(0, exit);
        Assert.Contains("↓ 5 more", output, StringComparison.Ordinal);
        Assert.Contains("The project “Rocket” will be made for example.", output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task DecliningTheSetupFails()
    {
        GitRepository();
        var keys = new Keys().Press(Keys.Enter, Keys.Enter, Keys.Enter, Keys.Enter, Keys.Enter, Keys.Enter, Keys.Down, Keys.Enter);

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

    void GitRepository() => folder.CreateSubdirectory(".git");

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

    // A machine with git and a logged-in `gh`: the project is cloned from acme/rocket, the user can make boards for
    // themselves, and the repository has the given milestones and labels.
    static FakeProcesses GitHub(string milestones, string labels = "") => new((command, arguments) => new ProcessResult(ProcessStatus.Exited, 0, command switch
    {
        "git" => "https://github.com/acme/rocket.git\n",
        "gh" when arguments[0] == "label" => labels,
        "gh" when arguments[0] == "api" && arguments[1].StartsWith("repos/", StringComparison.Ordinal) => milestones,
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
