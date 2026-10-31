using System.Text.RegularExpressions;
using TooBusy.Cli.Terminal;
using TooBusy.Core.Processes;

namespace TooBusy.Cli.Tests;

// `toobusy` without a command: the first setup and the menu, its Run, Skip labels, Settings and Assistant, and
// a demo of it.
public sealed class MenuTests : CliAppTests
{
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
    public async Task TheAssistantOfTheMenuChangesTheSessionsAndTheRunTakesThem()
    {
        var keys = new Keys()
            .Until("❯ Assistant", Keys.Down).Press(Keys.Enter)
            .Until("❯ Sessions", Keys.Down).Press(Keys.Enter)
            .Until("❯ 2", Keys.Down).Press(Keys.Enter)
            .Until("❯ Back", Keys.Down).Press(Keys.Enter)
            .Until("2 sessions at once", Keys.Up)
            .Until("❯ Run", Keys.Up).Press(Keys.Enter)
            .Until("#102", Keys.Enter)
            .Press(Keys.ControlC, Keys.ControlC);

        var (_, output, _) = await RunLiveAsync(keys, null, "--demo");

        Assert.Contains("own model · high · 2 sessions at once", output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task TheAssistantOfTheMenuChangesTheModelAndTheEffort()
    {
        GitRepository();
        WriteSettings(Settings);
        await ChooseAllAsync();

        // The assistant; the model: opus; the effort: one above; a look at the model that is left with Escape; `Back`
        // of the list, and out. On the first run there was nowhere to go back to, and here there is.
        var keys = new Keys()
            .Until("❯ Assistant", Keys.Down).Press(Keys.Enter)
            .Until("❯ Model", Keys.Down).Press(Keys.Enter)
            .Until("❯ opus", Keys.Down).Press(Keys.Enter)
            .Until("❯ Effort", Keys.Down).Press(Keys.Enter)
            .Until("❯ xhigh", Keys.Down).Press(Keys.Enter)
            .Until("❯ Model", Keys.Up).Press(Keys.Enter, Keys.Escape)
            .Until("❯ Back", Keys.Up).Press(Keys.Enter)
            .Until("❯ Exit", Keys.Up).Press(Keys.Enter);

        var (exit, output, _) = await RunWithKeysAsync(keys, []);

        Assert.Equal(0, exit);
        Assert.Contains("Model     the assistant's own", output, StringComparison.Ordinal);
        Assert.Contains("Model     opus", output, StringComparison.Ordinal);
        Assert.Contains("Effort    xhigh", output, StringComparison.Ordinal);
        Assert.Contains("Assistant    opus · xhigh", output, StringComparison.Ordinal);
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
    public async Task WithoutACommandAProjectIsSetUpTheChoicesAreMadeAndTheMenuOpens()
    {
        GitRepository();
        var processes = GitHub(milestones: "1\tv0.2.0\t\t2\n", labels: "bug\n");

        // No project, no blocking labels, any label; a new label for the owner and one for interrupted tasks, each
        // by the name that is proposed; the skill that is proposed; yes; the first milestone; the model after the
        // assistant's own; the proposed effort; the last of the menu.
        var keys = new Keys().Press(
            Keys.Tab, Keys.Tab, Keys.Enter, Keys.Enter, Keys.Enter,
            Keys.Enter, Keys.Enter, Keys.Enter, Keys.Enter, Keys.Enter,
            Keys.Enter, Keys.Enter)
            .Until("❯ fable", Keys.Down).Press(Keys.Enter, Keys.Enter)
            .Until("❯ Exit", Keys.Up).Press(Keys.Enter);

        var (exit, output, error) = await RunWithKeysAsync(keys, processes, []);

        Assert.Equal(0, exit);
        Assert.Equal("", error);
        Assert.Contains("What to do", output, StringComparison.Ordinal);
        Assert.Contains("Assistant    fable · high", output, StringComparison.Ordinal);
        Assert.Contains("Milestone    v0.2.0", output, StringComparison.Ordinal);
        Assert.Equal(
            [
                $"toobusy · ~{Path.DirectorySeparatorChar}{folder.Name}",
                "✔ Tracker          GitHub",
                "✔ Project          none",
                "✔ Blocking labels  none",
                "✔ Labels to take   any task",
                "✔ Manual label     manual",
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
        Assert.Contains("The label “manual” will be made in acme/rocket.", output, StringComparison.Ordinal);
        Assert.Contains("There is no skill “task-auto” yet: write .claude/skills/task-auto/SKILL.md before a run.", output, StringComparison.Ordinal);
        Assert.Contains("✔ Skill            task-auto", output, StringComparison.Ordinal);
        Assert.Contains("[session]\nskill = \"task-auto\"", File.ReadAllText(Path.Combine(folder.FullName, ".toobusy", "settings.toml")), StringComparison.Ordinal);
        Assert.Contains(processes.Asked, asked => asked.Command == "gh" && asked.Arguments.SequenceEqual(["label", "create", "manual", "--repo", "acme/rocket"]));
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
        var keys = new Keys().Until("❯ Settings", Keys.Down).Press(Keys.Enter, Keys.Escape, Keys.Escape, Keys.Escape);

        var (exit, output, _) = await RunWithKeysAsync(keys, []);

        Assert.Equal(0, exit);
        Assert.Contains("✔ Milestone        none: tasks are taken whatever their milestone", output, StringComparison.Ordinal);
        Assert.Contains("✔ Model            the assistant's own", output, StringComparison.Ordinal);
        Assert.Contains("✔ Effort           high", output, StringComparison.Ordinal);
        Assert.Contains("Assistant    own model · high", output, StringComparison.Ordinal);
        Assert.Contains("Changing the settings", output, StringComparison.Ordinal);
        Assert.Contains("Choose a setting to change it: a change is saved when you say so.", output, StringComparison.Ordinal);
        Assert.DoesNotContain("The setup was left", output, StringComparison.Ordinal);
        Assert.Contains("press esc again to exit", output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task TheBarOfTheMenuShowsTheVersionOfTheToolAtItsRight()
    {
        GitRepository();
        WriteSettings(Settings);
        await ChooseAllAsync();
        using var terminal = new TestTerminal(width: 120, height: 30);

        // The last of the menu.
        terminal.Keys.Until("❯ Exit", Keys.Up).Press(Keys.Enter);
        var exit = await CliApp.RunAsync([], Context(null) with { Output = terminal.Output, Terminal = terminal.Device }, TestContext.Current.CancellationToken);

        Assert.Equal(0, exit);
        terminal.AssertSaw("What to do");
        Assert.Equal(Bar(120, $"v{CliApp.Version}"), terminal.Frame[1]);
    }

    [Fact]
    public async Task TheBarOfTheMenuOfADemoShowsTheMadeUpProjectTheVersionAndThatItIsADemo()
    {
        GitRepository();
        using var terminal = new TestTerminal(width: 120, height: 30);
        terminal.Device = terminal.Device with { KeyWaiting = () => terminal.Keys.Waiting };

        // The last of the menu, once it is there.
        terminal.Keys.Hold(() => terminal.Saw("What to do")).Until("❯ Exit", Keys.Up).Press(Keys.Enter);
        var exit = await CliApp.RunAsync(["--demo"], Context(null) with { Output = terminal.Output, Terminal = terminal.Device }, TestContext.Current.CancellationToken);

        Assert.Equal(0, exit);
        Assert.Equal(Bar(120, $"v{CliApp.Version} · demo", "~/Projects/demo-project"), terminal.Frame[1]);
    }

    [Fact]
    public async Task InANarrowTerminalTheVersionGivesWayToTheFolder()
    {
        GitRepository();
        WriteSettings(Settings);
        await ChooseAllAsync();

        // One column short of the room the folder and the version need side by side: two columns between them and
        // two after the version.
        var title = $"  toobusy · ~{Path.DirectorySeparatorChar}{folder.Name}";
        using var terminal = new TestTerminal(width: title.Length + 2 + $"v{CliApp.Version}".Length + 2 - 1, height: 30);
        terminal.Keys.Until("❯ Exit", Keys.Up).Press(Keys.Enter);
        var exit = await CliApp.RunAsync([], Context(null) with { Output = terminal.Output, Terminal = terminal.Device }, TestContext.Current.CancellationToken);

        Assert.Equal(0, exit);
        terminal.AssertSaw("What to do");
        Assert.Equal(title, terminal.Frame[1]);
    }

    [Fact]
    public async Task RunOfTheMenuSaysHowManyTasksARunWouldTake()
    {
        GitRepository();
        WriteSettings(Settings);
        await ChooseAllAsync();

        // Three open tasks: one to take, one that opens after it, and one that a label holds.
        var processes = new ScriptedProcesses((command, arguments) => new ProcessResult(ProcessStatus.Exited, 0, command switch
        {
            "git" => "https://github.com/acme/rocket.git\n",
            "gh" when arguments[1] == "graphql" => "total\t3\n12\tExport the data\thttps://github.com/acme/rocket/issues/12\t\t\t\t\tfeature\n"
                + "13\tDescribe the export\thttps://github.com/acme/rocket/issues/13\t\t12\t\t\n14\tMove the site\thttps://github.com/acme/rocket/issues/14\t\t\t\t\tmanual\n",
            _ => "",
        }, ""));

        var (exit, output, _) = await RunWithKeysAsync(new Keys().Until("❯ Exit", Keys.Up).Press(Keys.Enter), processes, []);

        Assert.Equal(0, exit);
        Assert.Contains("❯ Run          2 tasks", output, StringComparison.Ordinal);
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
        var processes = new ScriptedProcesses((command, arguments) => new ProcessResult(ProcessStatus.Exited, 0, command switch
        {
            "git" => "https://github.com/acme/rocket.git\n",
            "gh" when arguments[1] == "graphql" => "total\t2\n12\tExport the data\thttps://github.com/acme/rocket/issues/12\t\t\t\t\tfeature\n"
                + "13\tDescribe the export\thttps://github.com/acme/rocket/issues/13\t\t12\t\t\n",
            _ => "",
        }, ""));

        var (exit, output, _) = await RunWithKeysAsync(new Keys().Until("❯ Exit", Keys.Up).Press(Keys.Enter), processes, []);

        Assert.Equal(0, exit);
        Assert.Contains("❯ Run          interrupted: #13 Describe the export", output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RunOfTheMenuOpensNoRunThatWouldTakeNoTask()
    {
        GitRepository();
        WriteSettings(Settings);
        await ChooseAllAsync();
        var processes = new ScriptedProcesses((command, _) => new ProcessResult(ProcessStatus.Exited, 0, command == "git" ? "https://github.com/acme/rocket.git\n" : "total\t0\n", ""));

        // Enter on `Run`, which counts again and opens nothing; then the last of the menu.
        var (exit, output, _) = await RunWithKeysAsync(new Keys().Press(Keys.Enter).Until("❯ Exit", Keys.Up).Press(Keys.Enter), processes, []);

        Assert.Equal(0, exit);
        Assert.Contains("❯ Run          no tasks", output, StringComparison.Ordinal);
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
        var processes = new ScriptedProcesses((command, arguments) => new ProcessResult(ProcessStatus.Exited, 0, command switch
        {
            "git" when arguments.Contains("remote") => "https://github.com/acme/rocket.git\n",
            "gh" when arguments[0] == "label" => Labels,
            "gh" when arguments is ["api", "graphql", ..] && !counted => Do(() => counted = true) + "total\t1\n12\tExport the data\thttps://github.com/acme/rocket/issues/12\t\t\t\t\tfeature\n",
            "gh" => "total\t0\n",
            _ => "",
        }, ""));

        // `Run`; the menu is back when the run is over, and the last of it leaves.
        var keys = new Keys().Press(Keys.Enter).Hold(() => Shown("0 tasks in 0 s")).Until("❯ Exit", Keys.Up).Press(Keys.Enter);
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
    public async Task EscapeStopsTheChecksBeforeARunOfTheMenuAndOpensNoRun()
    {
        GitRepository();
        WriteSettings(Settings);
        await ChooseAllAsync();

        // A task for the menu to count; the login of Claude Code takes until it is told to stop.
        var processes = new ScriptedProcesses(
            (command, arguments) => new ProcessResult(ProcessStatus.Exited, 0, command switch
            {
                "git" when arguments.Contains("remote") => "https://github.com/acme/rocket.git\n",
                "gh" when arguments is ["api", "graphql", ..] => "total\t1\n12\tExport the data\thttps://github.com/acme/rocket/issues/12\t\t\t\t\tfeature\n",
                "gh" => "total\t0\n",
                _ => "",
            }, ""),
            (command, arguments) => command == "claude" && arguments[0] == "auth");

        // `Run`; Escape while the checks are made; the menu stays as it was, and Escape twice leaves it.
        var keys = new Keys()
            .Hold(() => Shown("1 task"))
            .Press(Keys.Enter)
            .Hold(() => Shown("checking: Claude Code login"))
            .Press(Keys.Escape)
            .Hold(() => Shown("enter choose · ctrl+c exit", 2))
            .Press(Keys.Escape)
            .Hold(() => Shown("press esc again to exit"))
            .Press(Keys.Escape);
        var (exit, output, error) = await RunLiveAsync(keys, processes, []);

        Assert.Equal(0, exit);
        Assert.Equal("", error);
        await processes.Stopped.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        Assert.DoesNotContain("Running the queue", output, StringComparison.Ordinal);
        Assert.DoesNotContain("The run did not start.", output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RunOfTheMenuOpensNoRunWhileACheckFailsAndSaysTheFailuresWithTheirFixesAboveTheMenu()
    {
        GitRepository();
        WriteSettings(Settings);
        await ChooseAllAsync();

        // A task to take; Claude Code says that it is not logged in, and the repository lacks a label of the settings.
        var processes = new ScriptedProcesses((command, arguments) => (command, arguments[0]) switch
        {
            ("claude", "auth") => new ProcessResult(ProcessStatus.Exited, 1, """{"loggedIn": false}""", ""),
            ("git", _) => new ProcessResult(ProcessStatus.Exited, 0, "https://github.com/acme/rocket.git\n", ""),
            ("gh", "label") => new ProcessResult(ProcessStatus.Exited, 0, "manual\n", ""),
            ("gh", "api") when arguments[1] == "graphql" => new ProcessResult(ProcessStatus.Exited, 0, "total\t1\n12\tExport the data\thttps://github.com/acme/rocket/issues/12\t\t\t\t\tfeature\n", ""),
            _ => new ProcessResult(ProcessStatus.Exited, 0, "", ""),
        });
        using var terminal = new TestTerminal(width: 120, height: 30);

        // `Run`, which opens nothing; then the last of the menu.
        terminal.Keys.Press(Keys.Enter).Until("❯ Exit", Keys.Up).Press(Keys.Enter);
        var exit = await CliApp.RunAsync([], Context(processes) with { Output = terminal.Output, Terminal = terminal.Device }, TestContext.Current.CancellationToken);

        Assert.Equal(0, exit);
        terminal.AssertSaw("""
             ✘ Claude Code login  Claude Code is not logged in.
               fix: claude, then /login
             ✘ labels             acme/rocket has no label “interrupted”.
               fix: gh label create "interrupted" --repo acme/rocket
             The run did not start. `toobusy doctor` checks all of it again.
             What to do
             ❯ Run          1 task
            """.ReplaceLineEndings("\n").TrimEnd(' '));
        Assert.False(terminal.Saw("Running the queue"));
        Assert.DoesNotContain(processes.Asked, asked => asked.Command == "claude" && asked.Arguments[0] == "--bg");
    }

    // An error of the file system, and a cancellation that the menu did not ask for, as of a part that gave up by
    // itself.
    [Theory]
    [InlineData(nameof(IOException), "The disk is gone.")]
    [InlineData(nameof(OperationCanceledException), "The login gave up.")]
    public async Task RunOfTheMenuOpensNoRunWhenTheChecksCannotBeMadeAndSaysWhyAboveTheMenu(string kind, string words)
    {
        GitRepository();
        WriteSettings(Settings);
        await ChooseAllAsync();

        // A task to take; the login of Claude Code fails with an error nobody foresaw.
        Exception unforeseen = kind == nameof(IOException) ? new IOException(words) : new OperationCanceledException(words);
        var processes = new ScriptedProcesses((command, arguments) => (command, arguments[0]) switch
        {
            ("claude", "auth") => throw unforeseen,
            ("git", _) => new ProcessResult(ProcessStatus.Exited, 0, "https://github.com/acme/rocket.git\n", ""),
            ("gh", "label") => new ProcessResult(ProcessStatus.Exited, 0, Labels, ""),
            ("gh", "api") when arguments[1] == "graphql" => new ProcessResult(ProcessStatus.Exited, 0, "total\t1\n12\tExport the data\thttps://github.com/acme/rocket/issues/12\t\t\t\t\tfeature\n", ""),
            _ => new ProcessResult(ProcessStatus.Exited, 0, "", ""),
        });
        using var terminal = new TestTerminal(width: 160, height: 30);

        // `Run`, which opens nothing; then the last of the menu.
        terminal.Keys.Press(Keys.Enter).Until("❯ Exit", Keys.Up).Press(Keys.Enter);
        var exit = await CliApp.RunAsync([], Context(processes) with { Output = terminal.Output, Terminal = terminal.Device }, TestContext.Current.CancellationToken);

        Assert.Equal(0, exit);
        terminal.AssertSaw($"""
             ✘ checks             The checks could not be made: {words}
               This is an error toobusy did not foresee ({kind}): run `toobusy doctor` with TOOBUSY_TRACE=1 to see where it happened.
             The run did not start. `toobusy doctor` checks all of it again.
             What to do
             ❯ Run          1 task
            """.ReplaceLineEndings("\n").TrimEnd(' '));
        Assert.False(terminal.Saw("Running the queue"));
        Assert.DoesNotContain(processes.Asked, asked => asked.Command == "claude" && asked.Arguments[0] == "--bg");
    }

    [Fact]
    public async Task SkipLabelsOfTheMenuChoosesTheLabelsWhoseTasksTheRunsOfTheMenuLeaveOut()
    {
        GitRepository();
        WriteSettings(Settings);
        await ChooseAllAsync();

        // A task to take, one that opens after it, one that a label of the settings holds and one with a label to skip.
        var processes = new ScriptedProcesses((command, arguments) => new ProcessResult(ProcessStatus.Exited, 0, command switch
        {
            "git" => "https://github.com/acme/rocket.git\n",
            "gh" when arguments[0] == "label" => "feature\ndesign\nmanual\n",
            "gh" when arguments is ["api", "graphql", ..] => "total\t4\n12\tExport the data\thttps://github.com/acme/rocket/issues/12\t\t\t\t\tfeature\n"
                + "13\tDescribe the export\thttps://github.com/acme/rocket/issues/13\t\t12\t\t\n14\tMove the site\thttps://github.com/acme/rocket/issues/14\t\t\t\t\tmanual\n"
                + "15\tDraw the logo\thttps://github.com/acme/rocket/issues/15\t\t\t\t\tdesign\n",
            _ => "",
        }, ""));

        // `Skip labels`, where `design` is chosen; the list again, left with Escape; then the last of the menu.
        var keys = new Keys()
            .Until("❯ Skip labels", Keys.Up).Press(Keys.Enter)
            .Until("❯ ◯ design", Keys.Down).Press(Keys.Space, Keys.Enter)
            .Until("❯ Skip labels", Keys.Up).Press(Keys.Enter, Keys.Escape)
            .Until("❯ Exit", Keys.Up).Press(Keys.Enter);
        var (exit, output, _) = await RunWithKeysAsync(keys, processes, []);

        Assert.Equal(0, exit);
        Assert.Contains("❯ Run          3 tasks", output, StringComparison.Ordinal);
        Assert.Contains("Skip labels  none", output, StringComparison.Ordinal);

        // The labels of the repository and, after them, those of the settings it lacks, nothing chosen at first.
        Assert.Contains("Reading the labels", output, StringComparison.Ordinal);
        Assert.Contains("❯ ◯ feature\u001b[K\r\n   ◯ design\u001b[K\r\n   ◯ manual\u001b[K\r\n   ◯ interrupted\u001b[K\r\n   Back  esc\u001b[K\r\n", output, StringComparison.Ordinal);
        Assert.Contains(processes.Asked, asked => asked.Command == "gh" && asked.Arguments.SequenceEqual(["label", "list", "--repo", "acme/rocket", "--limit", "1000", "--json", "name", "--jq", ".[].name"]));

        // The choice stands above the menu and at its row, and the tasks are counted again without those it skips.
        Assert.Contains("✔ Skipped labels   design", output, StringComparison.Ordinal);
        Assert.Contains("Skip labels  design", output, StringComparison.Ordinal);
        Assert.Contains("❯ Run          2 tasks", output, StringComparison.Ordinal);

        // The list opens with the choice, and Escape leaves it as it was.
        Assert.Contains(" ❯ ◉ design\u001b[K", output, StringComparison.Ordinal);
        Assert.Contains("The labels to skip are left as they were.", output, StringComparison.Ordinal);
        Assert.True(output.LastIndexOf("✔ Skipped labels   design", StringComparison.Ordinal) > output.LastIndexOf(" ❯ ◉ design", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("", ProcessStatus.NotFound, "The `origin` remote is not a GitHub repository, so there are no labels to choose from.")]
    [InlineData("https://github.com/acme/rocket.git\n", ProcessStatus.NotFound, "The GitHub command-line tool `gh` is not installed, so the labels cannot be read.")]
    [InlineData("https://github.com/acme/rocket.git\n", ProcessStatus.NotStarted, "The GitHub command-line tool `gh` could not be started: Permission denied, so the labels cannot be read.")]
    public async Task SkipLabelsOfTheMenuSaysWhyTheLabelsCannotBeReadAndOffersBackAlone(string origin, ProcessStatus gh, string why)
    {
        GitRepository();
        WriteSettings(Settings);
        await ChooseAllAsync();
        var processes = new ScriptedProcesses((command, _) => command == "gh"
            ? new ProcessResult(gh, 0, "", gh == ProcessStatus.NotStarted ? "Permission denied" : "")
            : new ProcessResult(ProcessStatus.Exited, 0, origin, ""));

        var (exit, output, _) = await RunWithKeysAsync(new Keys().Until("❯ Skip labels", Keys.Up).Press(Keys.Enter).Until("❯ Back", Keys.Down).Press(Keys.Enter).Until("❯ Exit", Keys.Up).Press(Keys.Enter), processes, []);

        Assert.Equal(0, exit);
        Assert.Contains($"{why}\u001b[K\r\n", output, StringComparison.Ordinal);
        Assert.Contains("❯ Back  esc", output, StringComparison.Ordinal);
        Assert.Contains("The labels to skip are left as they were.", output, StringComparison.Ordinal);
    }

    // `gh auth status` asks GitHub: one that does not answer in time is not a login that is missing.
    [Fact]
    public async Task SkipLabelsOfTheMenuSaysThatGitHubDidNotAnswer()
    {
        GitRepository();
        WriteSettings(Settings);
        await ChooseAllAsync();
        var processes = new ScriptedProcesses((command, arguments) => command == "gh" && arguments[0] == "auth"
            ? new ProcessResult(ProcessStatus.TimedOut, 0, "", "")
            : new ProcessResult(ProcessStatus.Exited, 0, command == "git" ? "https://github.com/acme/rocket.git\n" : "", ""));

        var (exit, output, _) = await RunWithKeysAsync(new Keys().Until("❯ Skip labels", Keys.Up).Press(Keys.Enter).Until("❯ Back", Keys.Down).Press(Keys.Enter).Until("❯ Exit", Keys.Up).Press(Keys.Enter), processes, []);

        Assert.Equal(0, exit);
        Assert.Contains("GitHub did not answer, so the labels cannot be read.\u001b[K\r\n", output, StringComparison.Ordinal);
        Assert.DoesNotContain("not logged in", output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ADemoSkipsTheChosenLabelsOfItsMenuInTheRunsItOpens()
    {
        // `feature` and `docs` among the made-up labels; the run, which goes on with the task of the record and
        // takes none with them; the menu it comes back to counts the tasks made up anew without them; and out.
        var keys = new Keys()
            .Hold(() => Shown("interrupted: #100 Rename the settings page")).Until("❯ Skip labels", Keys.Up).Press(Keys.Enter)
            .Hold(() => Shown("◯ interrupted")).Until("❯ ◯ feature", Keys.Down).Press(Keys.Space).Until("❯ ◯ docs", Keys.Down).Press(Keys.Space, Keys.Enter)
            .Hold(() => Shown("Skip labels  feature, docs")).Until("❯ Run", Keys.Up).Press(Keys.Enter)
            .Hold(() => Shown("❯ Run          4 tasks")).Until("❯ Exit", Keys.Up).Press(Keys.Enter);

        var (exit, output, error) = await RunLiveAsync(keys, null, ["--demo"]);

        Assert.Equal(0, exit);
        Assert.Equal("", error);
        Assert.Contains("✔ Skipped labels   feature, docs", output, StringComparison.Ordinal);
        var rows = Scroll.Read(output).Rows.Where(row => row.StartsWith(" ✔ #", StringComparison.Ordinal) || row.StartsWith(" ◐ #", StringComparison.Ordinal)).ToList();
        Assert.Equal(
            [
                " ✔ #100 Rename the settings page  0:06",
                " ◐ #103 Fix the rounding of a discount  0:11",
                " ✔ #104 Update the dependencies  0:16",
                " ✔ #108 Find products whatever accents the search has  0:04",
                " ✔ #111 Load the list of orders page by page  0:04",
            ],
            rows);
    }

    [Fact]
    public async Task EscapeStopsTheSettingsOfTheMenuWhileTheyAreOpenedAndTheMenuStays()
    {
        GitRepository();
        WriteSettings(Settings);
        await ChooseAllAsync();

        // Once the menu is there, the login of `gh` takes until it is told to stop, as when GitHub does not answer.
        var processes = new ScriptedProcesses(
            (command, arguments) => new ProcessResult(ProcessStatus.Exited, 0, command switch
            {
                "git" when arguments.Contains("remote") => "https://github.com/acme/rocket.git\n",
                "gh" => "total\t0\n",
                _ => "",
            }, ""),
            (command, arguments) => command == "gh" && arguments[0] == "auth" && Shown("What to do"));

        // Settings; Escape while they are opened; the menu stays as it was, and Escape twice leaves it.
        var keys = new Keys()
            .Hold(() => Shown("What to do"))
            .Until("❯ Settings", Keys.Down).Press(Keys.Enter)
            .Hold(() => Shown("esc stop"))
            .Press(Keys.Escape)
            .Hold(() => Shown("enter choose · ctrl+c exit", 2))
            .Press(Keys.Escape)
            .Hold(() => Shown("press esc again to exit"))
            .Press(Keys.Escape);
        var (exit, output, error) = await RunLiveAsync(keys, processes, []);

        Assert.Equal(0, exit);
        Assert.Equal("", error);
        await processes.Stopped.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        Assert.DoesNotContain("Changing the settings", output, StringComparison.Ordinal);
        Assert.DoesNotContain("Choose a setting to change it", output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ASettingThatIsLeftChangedIsSavedWhenTheUserSaysSo()
    {
        GitRepository();
        WriteSettings(Settings);
        await ChooseAllAsync();

        // Settings; the skill, typed and left with Escape; saved; out of the settings and of the menu.
        var keys = new Keys()
            .Until("❯ Settings", Keys.Down).Press(Keys.Enter)
            .Until("❯ Skill", Keys.Down).Press(Keys.Enter)
            .Type("night-shift")
            .Press(Keys.Escape).Until("❯ Save", Keys.Up).Press(Keys.Enter, Keys.Escape, Keys.Escape, Keys.Escape);

        var (exit, output, _) = await RunWithKeysAsync(keys, []);

        Assert.Equal(0, exit);
        Assert.Contains("  → Skill: none → night-shift", output, StringComparison.Ordinal);
        Assert.Contains("Save the change?", output, StringComparison.Ordinal);
        Assert.Contains("The settings are written to .toobusy/settings.toml. Commit the file.", output, StringComparison.Ordinal);
        Assert.Contains("❯ Skill            night-shift", output, StringComparison.Ordinal);
        Assert.Contains("[session]\nskill = \"night-shift\"", File.ReadAllText(Path.Combine(folder.FullName, ".toobusy", "settings.toml")), StringComparison.Ordinal);
        Assert.Contains("✔ Skill            night-shift", Report(output), StringComparison.Ordinal);
        Assert.Contains("✔ The settings are written to .toobusy/settings.toml. Commit the file.", Report(output), StringComparison.Ordinal);
    }

    [Fact]
    public async Task ASettingThatIsNotSavedLeavesTheFileAsItWas()
    {
        GitRepository();
        WriteSettings(Settings);
        await ChooseAllAsync();

        // Settings; the branch, typed and confirmed; not saved; out.
        var keys = new Keys()
            .Until("❯ Settings", Keys.Down).Press(Keys.Enter)
            .Until("❯ Branch", Keys.Up).Press(Keys.Enter)
            .Type("develop")
            .Press(Keys.Enter).Until("❯ Don't save", Keys.Down).Press(Keys.Enter, Keys.Escape, Keys.Escape, Keys.Escape);

        var (exit, output, _) = await RunWithKeysAsync(keys, []);

        Assert.Equal(0, exit);
        Assert.Contains("  → Branch: none → develop", output, StringComparison.Ordinal);
        Assert.Contains("The change was not saved.", output, StringComparison.Ordinal);
        Assert.Equal(Settings, File.ReadAllText(Path.Combine(folder.FullName, ".toobusy", "settings.toml")));
        Assert.DoesNotContain("Branch", Report(output), StringComparison.Ordinal);
    }

    [Fact]
    public async Task ADemoShowsTheMadeUpProjectReadyOutsideAGitRepository()
    {
        // Straight to the menu, which names the task a run was killed over: the run, which goes on with it and
        // comes back to the menu by itself when it is over, and out.
        var keys = new Keys()
            .Hold(() => Shown("interrupted: #100 Rename the settings page")).Press(Keys.Enter)
            .Hold(() => Shown("What to do", times: 2)).Until("❯ Exit", Keys.Up).Press(Keys.Enter);

        var (exit, output, error) = await RunLiveAsync(keys, null, ["--demo"]);

        Assert.Equal(0, exit);
        Assert.Equal("", error);
        Assert.DoesNotContain("Write the settings?", output, StringComparison.Ordinal);
        Assert.Contains("✔ Project          https://github.com/orgs/example/projects/1", output, StringComparison.Ordinal);
        Assert.Contains("✔ Statuses         Todo: Todo · In Progress: In Progress · Done: Done", output, StringComparison.Ordinal);
        Assert.Contains("✔ Blocking labels  draft, design", output, StringComparison.Ordinal);
        Assert.Contains("✔ Labels to take   feature, bug, chore, docs", output, StringComparison.Ordinal);
        Assert.Contains("✔ Manual label     manual", output, StringComparison.Ordinal);
        Assert.Contains("✔ Interrupt label  interrupted", output, StringComparison.Ordinal);
        Assert.Contains("✔ Skill            task-auto", output, StringComparison.Ordinal);
        Assert.Contains("✔ Milestone        v1.0.0 · due 2030-03-01 · 15 open tasks", output, StringComparison.Ordinal);
        Assert.Contains("✔ Model            the assistant's own", output, StringComparison.Ordinal);
        Assert.Contains("✔ Effort           high", output, StringComparison.Ordinal);
        Assert.Contains("❯ Run          interrupted: #100 Rename the settings page", output, StringComparison.Ordinal);

        // The menu that the run comes back to says how the run went, and counts the tasks that are made up anew.
        Assert.Contains("❯ Run          11 tasks", output, StringComparison.Ordinal);
        Assert.Contains("12 tasks in 93 s · 11 done · 1 done in part", output.Split("What to do")[^2], StringComparison.Ordinal);

        // The screen is left for the run and entered again for the menu; the tasks of the run stay in the history
        // of the terminal, and only what else there is to say is printed under them.
        Assert.Equal(2, output.Split("\u001b[?1049h").Length - 1);
        var rows = Scroll.Read(output).Rows.Where(row => row.Length > 0).ToList();
        Assert.Contains(" ✔ #100 Rename the settings page  0:06", rows);
        Assert.Equal([" 12 tasks in 93 s · 11 done · 1 done in part", "Demo: the tasks and the sessions were made up, and nothing was changed."], rows.TakeLast(2));
        Assert.False(Directory.Exists(Path.Combine(folder.FullName, ".toobusy")));
        Assert.False(File.Exists(Path.Combine(personal.FullName, "projects.toml")));
    }

    [Fact]
    public async Task ADemoRemembersAnAbortAndTheMenuAndTheNextRunStartWithTheInterruptedTask()
    {
        // The run is aborted while the first new task is worked on. The menu it comes back to says which task was
        // interrupted instead of the number of tasks; Run takes that task first, and when the run is over the tasks
        // are counted.
        var clock = new PacedClock();
        var keys = new Keys()
            .Hold(() => Shown("interrupted: #100 Rename the settings page")).Press(Keys.Enter)
            .Hold(() => Shown("Read src/Orders/Order.cs")).Type("/")
            .Hold(() => Shown("❯ stop")).Press(Keys.Down, Keys.Enter)
            .Hold(() => clock.Hurried = Shown("interrupted: #101 Show the total of an order in its header")).Press(Keys.Enter)
            .Hold(() => Shown("❯ Run          11 tasks")).Until("❯ Exit", Keys.Up).Press(Keys.Enter);
        var context = Context(null) with
        {
            Clock = clock,
            Terminal = new TerminalDevice(Watched(keys).Read, () => (120, 30)) { KeyWaiting = () => keys.Waiting },
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

    // The menu of a demo in a project with settings and a choice of the milestone of its own shows neither, and asks
    // the machine nothing.
    [Fact]
    public async Task ADemoShowsNoneOfTheProjectItIsOpenedIn()
    {
        GitRepository();
        WriteSettings(SettingsWithABoard);
        await RunAsync("milestone", "--none");
        var processes = new ScriptedProcesses();

        var (exit, output, error) = await RunWithKeysAsync(new Keys().Until("❯ Exit", Keys.Up).Press(Keys.Enter), processes, ["--demo"]);

        Assert.Equal(0, exit);
        Assert.Equal("", error);
        Assert.Empty(processes.Asked);
        Assert.Contains("✔ Project          https://github.com/orgs/example/projects/1", output, StringComparison.Ordinal);
        Assert.Contains("✔ Blocking labels  draft, design", output, StringComparison.Ordinal);
        Assert.Contains("✔ Manual label     manual", output, StringComparison.Ordinal);
        Assert.Contains("✔ Milestone        v1.0.0 · ", output, StringComparison.Ordinal);
        Assert.Contains("❯ Run          interrupted: #100 Rename the settings page", output, StringComparison.Ordinal);
        Assert.DoesNotContain("acme", output, StringComparison.Ordinal);
        Assert.DoesNotContain("needs-owner", output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RunOfTheMenuOpensNoRunWhileOneGoesOnInTheWorkingCopy()
    {
        GitRepository();
        WriteSettings(Settings);
        await ChooseAllAsync();
        GoingRun(Working);
        var processes = new ScriptedProcesses((command, arguments) => new ProcessResult(ProcessStatus.Exited, 0, command switch
        {
            "git" => "https://github.com/acme/rocket.git\n",
            "gh" when arguments is ["api", "graphql", ..] => "total\t1\n12\tExport the orders as a file\thttps://github.com/acme/rocket/issues/12\t\t\t\t\tfeature\n",
            _ => "",
        }, ""));
        using var terminal = new TestTerminal(width: 120, height: 30);

        // `Run`, which opens nothing; then the last of the menu.
        terminal.Keys.Press(Keys.Enter).Until("❯ Exit", Keys.Up).Press(Keys.Enter);
        var exit = await CliApp.RunAsync([], Context(processes) with { Output = terminal.Output, Terminal = terminal.Device }, TestContext.Current.CancellationToken);

        Assert.Equal(0, exit);
        terminal.AssertSaw("""
             A run is going on here already: #12 Export the orders as a file · claude attach 1a2b3c4d
             What to do
             ❯ Run          1 task
            """.ReplaceLineEndings("\n").TrimEnd(' '));
        Assert.False(terminal.Saw("Running the queue"));

        // Nothing was checked for a run that does not start.
        Assert.DoesNotContain(processes.Asked, asked => asked.Command == "claude");
    }

    // The line of the bar of the page in a terminal so wide: the tool and the folder at the left, the one of the test
    // where no other is given, and the status two columns from the right edge.
    string Bar(int width, string status, string? shown = null)
    {
        var title = $"  toobusy · {shown ?? $"~{Path.DirectorySeparatorChar}{folder.Name}"}";
        return title + new string(' ', width - 2 - title.Length - status.Length) + status;
    }
}
