using System.Globalization;
using System.Text.RegularExpressions;
using TooBusy.Cli.Terminal;
using TooBusy.Core.Processes;
using TooBusy.Core.Queue;
using TooBusy.Core.Run;

namespace TooBusy.Cli.Tests;

// `toobusy run`: what a run needs before it starts, a run without a terminal and on its page, and a demo of one.
public sealed class RunCommandTests : CliAppTests
{
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

    [Theory]
    [InlineData("toobusy: give `--task` or `--max`, not both", "--task", "3", "--max", "2")]
    [InlineData("toobusy: `--task` takes the number of a task, which is 1 or more", "--task", "0")]
    [InlineData("toobusy: `--max` takes a number of tasks, which is 1 or more", "--max", "0")]
    [InlineData("toobusy: `--max` takes a number of tasks, which is 1 or more", "--max=-2")]
    [InlineData("--task", "--task", "twelve")]
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
        Assert.Contains("✻ v0.1.0 (made up) · #101 and no other task · ", output, StringComparison.Ordinal);
        Assert.Contains("✔ #101 Show the total of an order in its header — done", output, StringComparison.Ordinal);
        Assert.Contains("‖ Stopped after #101, as you asked · 1 done", output, StringComparison.Ordinal);
        Assert.Equal(["#101"], Regex.Matches(output, @"#\d+").Select(match => match.Value).Distinct());
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
    [InlineData("999", "it is not among the open tasks of the milestone v0.1.0 (made up)")]
    public async Task ARunOfATaskThatARunWouldNotTakeStartsNothingAndFails(string number, string why)
    {
        GitRepository();

        var (exit, output, _) = await RunAsync("run", "--demo", "--task", number);

        // A demo writes no log, so it names none.
        Assert.Equal(1, exit);
        Assert.Equal(
            $"Demo: nothing is changed.{Environment.NewLine}✖ Stopped: #{number} is not taken: {why} · no task was started{Environment.NewLine}  Worked 0 s{Environment.NewLine}",
            output);
        Assert.False(Directory.Exists(Path.Combine(folder.FullName, ".toobusy", "local", "logs")));
    }

    [Fact]
    public async Task ARunLeavesOutTheTasksWithTheLabelsItSkipsAndGoesOnWithTheTaskOfTheRecord()
    {
        GitRepository();

        // The option is given again, and with several names at once.
        var (exit, output, _) = await RunAsync("run", "--demo", "--skip-label", "docs", "--skip-label", "Feature, chore");

        Assert.Equal(0, exit);
        var lines = output.Split(Environment.NewLine);
        Assert.Equal("✻ v0.1.0 (made up) · 2 tasks in the queue · skips docs, Feature, chore · the assistant's own model · high effort · auto mode", lines[1]);
        Assert.Equal(
            "  Held: #101 it has the feature label, which this run skips · #102 it has the feature label, which this run skips · #104 it has the chore label, which this run skips"
            + " · #105 it has the docs label, which this run skips · #106 it has the draft label · #107 it has the feature label, which this run skips",
            lines[2]);

        // The task an earlier run left with a session has a label the run skips, and is gone on with all the same.
        Assert.Contains("→ #100 Rename the settings page — taken again · its session was stopped, and goes on", output, StringComparison.Ordinal);
        Assert.Contains("→ #103 Fix the rounding of a discount — started", output, StringComparison.Ordinal);
        Assert.DoesNotContain("→ #101", output, StringComparison.Ordinal);
        Assert.DoesNotContain("→ #104", output, StringComparison.Ordinal);
        Assert.DoesNotContain("→ #105", output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task TheHelpOfRunAndOfQueueWorkOutsideAGitRepositoryAndTellOfTheLabelsToSkip()
    {
        var (exit, run, _) = await RunAsync("run", "--help");
        var (_, queue, _) = await RunAsync("queue", "--help");

        Assert.Equal(0, exit);
        Assert.Contains("--demo", run, StringComparison.Ordinal);
        Assert.Contains("--skip-label <name>", run, StringComparison.Ordinal);
        Assert.Contains("Leave out the tasks with this label for this run, and the tasks that wait for them", run, StringComparison.Ordinal);
        Assert.Contains("--skip-label <name>", queue, StringComparison.Ordinal);
        Assert.Contains("Show the queue of a run that leaves out the tasks with this label", queue, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ADemoOfARunOfANumberOfTasksStopsAfterThem()
    {
        GitRepository();

        var (exit, output, _) = await RunAsync("run", "--demo", "--max", "2");

        Assert.Equal(0, exit);
        Assert.Contains("✻ v0.1.0 (made up) · 6 tasks in the queue, 2 at most · the assistant's own model · high effort · auto mode", output, StringComparison.Ordinal);
        Assert.Contains("✔ #100 Rename the settings page — done", output, StringComparison.Ordinal);
        Assert.Contains("✔ #101 Show the total of an order in its header — done", output, StringComparison.Ordinal);
        Assert.DoesNotContain("#102 Export the orders as a file —", output, StringComparison.Ordinal);
        Assert.EndsWith($"‖ Stopped after 2 tasks, as you asked · 2 done{Environment.NewLine}  Worked 15 s{Environment.NewLine}", output, StringComparison.Ordinal);
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

        // A run that stopped over a problem names its log, which holds what it said and every command it ran.
        Assert.Equal(1, exit);
        Assert.Equal($"✖ Stopped: #40 is not taken: it is not among the open tasks · no task was started{Environment.NewLine}  Worked 0 s{Environment.NewLine}  the log of the run: {Log}{Environment.NewLine}", output);
        Assert.DoesNotContain(processes.Asked, asked => asked.Command == "claude" && asked.Arguments[0] == "--bg");
        Assert.DoesNotContain(processes.Asked, asked => asked.Command == "gh" && asked.Arguments[0] == "issue");
        var written = File.ReadAllText(Path.Combine(folder.FullName, Log));
        Assert.Contains("✖ Stopped: #40 is not taken: it is not among the open tasks · no task was started", written, StringComparison.Ordinal);
        Assert.Contains(" $ gh api ", written, StringComparison.Ordinal);
        Assert.Contains("→ gh exited 0 in ", written, StringComparison.Ordinal);
        Assert.Contains("run ended: problem · #40 is not taken", written, StringComparison.Ordinal);
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
                " ▲ #103 ended its turn with a line for toobusy, but Claude Code marks it as waiting for an answer · it is told that",
                "   none will come · claude attach 4f2a103 (made up)",
                " ◐ #103 Fix the rounding of a discount  0:11",
                " ✔ #104 Update the dependencies  0:16",
                " ✔ #105 Describe the export in the manual  0:07",
                "",
                " 6 tasks in 70 s · 5 done · 1 done in part",
                "Demo: the tasks and the sessions were made up, and nothing was changed.",
            ],
            rows.Skip(3));
        Assert.True(Scroll.Read(output).CursorShown);
        Assert.True(Scroll.Read(output).Wraps);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CtrlCTwiceKillsARunAndTheTapeSaysWhatIsLeft(bool stoppedMeanwhile)
    {
        GitRepository();
        WriteSettings(Settings);
        await ChooseAllAsync();

        // One open task, and a session of Claude Code that works on it for as long as it is let. One more Ctrl+C
        // while the session is stopped is the terminal's own, and stops the tool from outside.
        using var outside = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        var processes = new FakeProcesses((command, arguments) =>
        {
            if (stoppedMeanwhile && command == "claude" && arguments.Contains("stop"))
                outside.Cancel();
            return new ProcessResult(ProcessStatus.Exited, 0, (command, arguments[0]) switch
            {
                ("git", _) when arguments.Contains("remote") => "https://github.com/acme/rocket.git\n",
                ("git", _) => "# branch.oid 1a2b\n# branch.ab +0 -0\n",
                ("gh", "api") => "total\t1\n12\tExport the data\thttps://github.com/acme/rocket/issues/12\t\t\t\t\tfeature\n",
                ("gh", "label") => Labels,
                ("gh", "issue") => "Write it as CSV.\n",
                ("claude", "--bg") => "backgrounded · abc12345 · #12 Export the data\n",
                ("claude", "agents") => """[{"id":"abc12345","sessionId":"abc12345-0000","kind":"background","state":"working","status":"busy","pid":1}]""",
                _ => "",
            }, "");
        });
        // The clock is the real one here: with a clock that makes nobody wait, a session that never ends would be
        // looked at without end while the keys are waited for.
        var context = Context(processes) with { Home = personal.FullName, Clock = new TooBusy.Infrastructure.Machine.SystemClock() };
        var transcript = Transcript(context, "abc12345-0000");
        Directory.CreateDirectory(Path.GetDirectoryName(transcript)!);
        File.WriteAllText(transcript, """{"type":"assistant","timestamp":"2099-01-01T09:00:00Z","message":{"model":"claude","content":[{"type":"tool_use","name":"Bash","input":{"command":"dotnet test"}}]}}""" + "\n");
        var keys = new Keys()
            .Hold(() => Shown("Bash: dotnet test")).Press(Keys.ControlC)
            .Hold(() => Shown("press ctrl+c again to stop the session and exit")).Press(Keys.ControlC);

        var (exit, output, error) = await RunAsync(context with { Terminal = new TerminalDevice(keys.Read, () => (120, 30)) { KeyWaiting = () => keys.Waiting } }, ["run"], outside.Token);

        Assert.Equal("", error);
        Assert.Equal(130, exit);
        Assert.Equal(stoppedMeanwhile, outside.IsCancellationRequested);
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
    public async Task ASessionIsStartedInThePermissionModeOfTheSettingsAndTheLogSaysIt()
    {
        GitRepository();
        WriteSettings(Settings + "\npermission-mode = \"dontAsk\"\n");
        await ChooseAllAsync();

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
        var transcript = Transcript(context, "abc12345-0000");
        Directory.CreateDirectory(Path.GetDirectoryName(transcript)!);
        File.WriteAllText(transcript, """{"type":"assistant","timestamp":"2030-01-01T09:00:00Z","message":{"model":"claude","content":[{"type":"text","text":"The export is written.\nTOOBUSY: done"}]}}""" + "\n");

        var (exit, output, error) = await RunAsync(context, ["run"]);

        Assert.Equal("", error);
        Assert.Equal(0, exit);
        Assert.StartsWith("✻ No milestone · 1 task in the queue · the assistant's own model · high effort · dontAsk mode" + Environment.NewLine, output, StringComparison.Ordinal);
        var started = processes.Asked.Single(asked => asked.Command == "claude" && asked.Arguments[0] == "--bg").Arguments;
        Assert.Equal(["--bg", "--permission-mode", "dontAsk", "--permission-prompts", "none", "--settings"], started.Take(6));
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
        var transcript = Transcript(context, "abc12345-0000");
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
        var transcript = Transcript(context, "abc12345-0000");
        Directory.CreateDirectory(Path.GetDirectoryName(transcript)!);
        File.WriteAllText(transcript, """{"type":"assistant","timestamp":"2030-01-01T09:00:00Z","message":{"model":"claude","content":[{"type":"text","text":"The export is written.\nTOOBUSY: done"}]}}""" + "\n");

        var (exit, output, error) = await RunAsync(context, ["run"]);

        Assert.Equal("", error);
        Assert.Equal(0, exit);
        Assert.Equal(
            $$"""
            ✻ No milestone · 1 task in the queue · the assistant's own model · high effort · auto mode
            → #12 Export the data — started · a new session · claude attach abc12345
              #12 is assigned to the current user; the session is told that toobusy keeps its task
            ✔ #12 Export the data — done in 0 s
              #12 is closed, with the report of the session as its comment
            ✔ No task is left · 1 done
              Worked 0 s
              the log of the run: {{Log}}

            """.ReplaceLineEndings(),
            output);

        // The log holds how the session was seen, and none of what the list of the sessions answered.
        var written = File.ReadAllText(Path.Combine(folder.FullName, Log));
        Assert.Contains(" #12 session abc12345: ended", written, StringComparison.Ordinal);
        Assert.Contains("$ claude agents --json --all", written, StringComparison.Ordinal);
        Assert.DoesNotContain("\"sessionId\"", written, StringComparison.Ordinal);

        var started = processes.Asked.Single(asked => asked.Command == "claude" && asked.Arguments[0] == "--bg").Arguments;
        Assert.Equal(["--bg", "--permission-mode", "auto"], started.Take(3));
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
    public async Task ARunReadsTheConversationOfASessionWhereClaudeConfigDirPutsTheFolderOfClaudeCode()
    {
        GitRepository();
        WriteSettings(Settings);
        await ChooseAllAsync();

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

        // Claude Code keeps its folder where `CLAUDE_CONFIG_DIR` says, not under the home folder, and writes the
        // conversation of the session there.
        var elsewhere = personal.CreateSubdirectory("elsewhere").FullName;
        var context = Context(processes) with { Home = personal.FullName, Variables = name => name == "CLAUDE_CONFIG_DIR" ? elsewhere : null };
        var transcript = Transcript(context, "abc12345-0000");
        Assert.StartsWith(Path.Combine(elsewhere, "projects") + Path.DirectorySeparatorChar, transcript, StringComparison.Ordinal);
        Directory.CreateDirectory(Path.GetDirectoryName(transcript)!);
        File.WriteAllText(transcript, """{"type":"assistant","timestamp":"2030-01-01T09:00:00Z","message":{"model":"claude","content":[{"type":"text","text":"The export is written.\nTOOBUSY: done"}]}}""" + "\n");

        var (exit, output, error) = await RunAsync(context, ["run"]);

        Assert.Equal("", error);
        Assert.Equal(0, exit);
        Assert.Contains("✔ #12 Export the data — done in 0 s", output, StringComparison.Ordinal);
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
    public async Task ADemoOfTheRunNeedsNoSettingsEither()
    {
        GitRepository();

        var (exit, output, _) = await RunAsync("run", "--demo");

        // The whole of a demo: a task whose session a kill stopped and which goes on with it, a task that is done, a
        // session that asks and is told to go on alone, a task done in part whose session made a task of what is left
        // and was marked as waiting after its report, a usage limit that is waited out, a task that opened after
        // another one, and the tasks that are held.
        Assert.Equal(0, exit);
        Assert.Equal(
            """
            Demo: nothing is changed.
            ✻ v0.1.0 (made up) · 6 tasks in the queue · the assistant's own model · high effort · auto mode
              Opens later: #105 after #102
              Held: #106 it has the draft label · #107 its status is In Progress
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
              “The owner is away: no answer to your question and no approval will come. Decide yourself and go on with task #102 from where you stopped. What cannot be decided without the owner, make a new task of it yourself: describe there what is left and your questions for the owner, and put the label `manual` on it, so that no run takes it before the owner answers. End your reply with a `TOOBUSY:` line.”
            ✔ #102 Export the orders as a file — done in 21 s
              #102 is closed, with the report of the session as its comment
            → #103 Fix the rounding of a discount — started · a new session with the skill task-auto · claude attach 4f2a103 (made up)
              #103 is In Progress on the board and assigned to the current user; the session is told that toobusy keeps its task
            ▲ #103 ended its turn with a line for toobusy, but Claude Code marks it as waiting for an answer · it is told that none will come · claude attach 4f2a103 (made up)
              │ A discount is rounded once, to the cent, half up, and the cart and the order show the same price.
              │ - Checked with the tests of the prices: 31 pass.
              │ - Commit c3d4e5f.
              │ Left for the owner: a discount may be larger than the price it is taken from, and the price becomes negative. What such a discount does is theirs to say. I made #108 “Decide what a discount of more than the price does” of it, with the manual label and the two answers I see: the price is zero, or the discount is refused when it is entered.
              “No answer will come: the owner is away, and nobody reads your questions. Go on with task #103 from where you stopped. What still needs the owner, make a new task of it yourself: describe there what is left and your questions for the owner, and put the label `manual` on it, so that no run takes it before the owner answers. When everything that can be done is done, say so: end your reply with a `TOOBUSY:` line, and ask nothing after it.”
            ◐ #103 Fix the rounding of a discount — done in part in 11 s
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
              Held: #106 it has the draft label · #107 its status is In Progress · #108 it waits for the owner: it has the manual label
            ✔ No task is left · 5 done · 1 done in part
              Worked 70 s

            """.ReplaceLineEndings(),
            output);
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
        // The lock that the sign was put up under stays.
        Assert.Equal([".gitignore", "run.lock"], new DirectoryInfo(Path.Combine(folder.FullName, ".toobusy", "local")).GetFiles().Select(file => file.Name).Order());

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

    // The log a run of a test writes: its clock starts at the same moment every time.
    static string Log => $".toobusy/local/logs/run-{InstantClock.Start.ToLocalTime().ToString("yyyy-MM-dd-HH-mm-ss", CultureInfo.InvariantCulture)}.log";

    // Where Claude Code keeps a conversation of the project for a run in this context: under its home folder, or
    // where its `CLAUDE_CONFIG_DIR` says.
    string Transcript(CliContext context, string conversation) =>
        TooBusy.Assistants.ClaudeCode.ClaudeFolders.Of(folder.FullName, "", () => { }, context.Home, context.Variables).Transcript(conversation);
}
