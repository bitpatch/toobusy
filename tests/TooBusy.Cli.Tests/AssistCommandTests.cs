using System.Text.RegularExpressions;
using TooBusy.Core.Processes;

namespace TooBusy.Cli.Tests;

// `toobusy init --assist`: the walk through the setup for an assistant, and where the tool points an assistant
// to it.
public sealed class AssistCommandTests : CliAppTests
{
    // A machine with git and a logged-in `gh`, in a project cloned from acme/rocket: the repository has these
    // labels and these open milestones, the user reaches the two boards, and the second of them has statuses of
    // its own.
    static ScriptedProcesses GitHubForAssist(string labels = "", string milestones = "") => new((command, arguments) => new ProcessResult(ProcessStatus.Exited, 0, command switch
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
        WriteSettings(SettingsWithABoard + SkillOfTheProject + "\n[task]\nbefore = \"scripts/dev.sh ports free\"\n\n[workspace]\nbranch = \"develop\"\n");
        WriteSkill("task-auto");
        await ChooseAllAsync();
        var before = Files();

        var (exit, output, _) = await RunFromAsync(folder.CreateSubdirectory("src").FullName, "init", "--assist");

        Assert.Equal(0, exit);
        Assert.Contains(
            """
            2. **Which tasks are taken.** Take: `feature`, `bug`; blocking: `draft`; manual: `needs-owner`; interrupted: `interrupted`.
            3. **The board.** The board is https://github.com/orgs/acme/projects/1.
            4. **The owner's own choices.** Milestone: no milestone; model: the assistant's own; effort: high.
            5. **The instructions of the project.** The project's own; toobusy does not read them.
            6. **The skill a session starts with.** `task-auto`, written.
            7. **The branch and the commands around a task.** Branch: `develop`; before: `scripts/dev.sh ports free`; after: none.
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
        var processes = GitHubForAssist(labels: "bug\nfeature\ndraft\nquestion\n", milestones: "1\tv2\t\t1\n2\tv1\t2030-03-01T00:00:00Z\t12\n");
        var before = Files();
        var settings = File.ReadAllText(Path.Combine(folder.FullName, ".toobusy", "settings.toml"));

        var (exit, labels, error) = await RunAsync(Context(processes), ["init", "--assist", "2"]);
        Assert.Equal(0, exit);
        Assert.Equal("", error);
        Assert.StartsWith("## Step 2 of 9: Which tasks are taken\n\nAs things are: the labels to take: `feature`, `bug`;", labels, StringComparison.Ordinal);
        Assert.Contains("The labels of acme/rocket: `bug`, `feature`, `draft`, `question`. The settings name `needs-owner`, `interrupted`, which the repository does not have.", labels, StringComparison.Ordinal);
        Assert.Contains("    toobusy init --yes --take-label feature,bug --blocking-label draft --manual-label needs-owner --interrupt-label interrupted\n", labels, StringComparison.Ordinal);
        Assert.EndsWith("Next: `toobusy init --assist 3`\n", labels, StringComparison.Ordinal);
        Assert.DoesNotContain("# Setting a project up", labels, StringComparison.Ordinal);

        var (_, boards, _) = await RunAsync(Context(processes), ["init", "--assist", "3"]);
        Assert.Contains(
            "- Rocket, https://github.com/orgs/acme/projects/1, linked to acme/rocket: Todo, In Progress, Done\n"
            + "- Garden, https://github.com/users/ann/projects/2: Backlog, Done. **A run needs three statuses.**\n",
            boards,
            StringComparison.Ordinal);

        var (_, milestones, _) = await RunAsync(Context(processes), ["init", "--assist", "4"]);
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

        var loggedOut = new ScriptedProcesses((command, arguments) => command == "gh" && arguments[0] == "auth"
            ? new ProcessResult(ProcessStatus.Exited, 1, "", "")
            : new ProcessResult(ProcessStatus.Exited, 0, command == "git" ? "https://github.com/acme/rocket.git\n" : "", ""));
        var (_, boards, _) = await RunAsync(Context(loggedOut), ["init", "--assist", "3"]);
        Assert.Contains("The boards cannot be read: `gh` is not logged in to github.com. Step 1 puts it right", boards, StringComparison.Ordinal);

        var elsewhere = new ScriptedProcesses(command => new ProcessResult(ProcessStatus.Exited, 0, command == "git" ? "https://gitlab.com/acme/rocket.git\n" : "", ""));
        var (_, milestones, _) = await RunAsync(Context(elsewhere), ["init", "--assist", "4"]);
        Assert.Contains("The open milestones cannot be read: the `origin` remote is not a GitHub repository. Step 1 puts it right", milestones, StringComparison.Ordinal);
    }

    // A GitHub that does not answer in time is neither a `gh` that is logged out nor one that reaches no board.
    [Fact]
    public async Task AStepOfAssistSaysThatGitHubDidNotAnswer()
    {
        GitRepository();
        ScriptedProcesses Silent(Func<IReadOnlyList<string>, bool> unanswered) => new((command, arguments) => command == "gh" && unanswered(arguments)
            ? new ProcessResult(ProcessStatus.TimedOut, 0, "", "")
            : new ProcessResult(ProcessStatus.Exited, 0, command == "git" ? "https://github.com/acme/rocket.git\n" : "", ""));

        var (_, login, _) = await RunAsync(Context(Silent(arguments => arguments[0] == "auth")), ["init", "--assist", "3"]);
        var (_, boards, _) = await RunAsync(Context(Silent(arguments => arguments[0] == "api")), ["init", "--assist", "3"]);

        foreach (var told in (string[])[login, boards])
        {
            Assert.Contains("The boards cannot be read: GitHub did not answer. Ask for the step again, or see step 1: `toobusy init --assist 1`.", told, StringComparison.Ordinal);
            Assert.DoesNotContain("not logged in", told, StringComparison.Ordinal);
            Assert.DoesNotContain("No board can be read", told, StringComparison.Ordinal);
        }
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

    [Fact]
    public async Task InATerminalAssistPrintsTheSameAndOpensNoScreen()
    {
        GitRepository();
        var (_, plain, _) = await RunAsync("init", "--assist", "5");

        var (exit, output, _) = await RunWithKeysAsync(new Keys(), "init", "--assist", "5");

        Assert.Equal(0, exit);
        Assert.Equal(plain, output);
    }

    // A demo tells the walk over the made-up project as it stands before a setup, outside a git repository too: what
    // the steps choose among is made up, and nothing is asked of the machine.
    [Fact]
    public async Task ADemoOfAssistTellsTheStepsOverTheMadeUpProject()
    {
        var processes = new ScriptedProcesses();

        var (exit, introduction, _) = await RunAsync(Context(processes), ["init", "--assist", "--demo"]);
        var (_, labels, _) = await RunAsync(Context(processes), ["init", "--assist", "2", "--demo"]);
        var (_, boards, _) = await RunAsync(Context(processes), ["init", "--assist", "3", "--demo"]);
        var (_, milestones, _) = await RunAsync(Context(processes), ["init", "--assist", "4", "--demo"]);

        Assert.Equal(0, exit);
        Assert.Empty(processes.Asked);
        Assert.Contains("2. **Which tasks are taken.** Not set: there are no settings yet.", introduction, StringComparison.Ordinal);
        Assert.Contains("The labels of example/demo-project: `feature`, `bug`, `chore`, `docs`, `draft`, `design`, `manual`, `interrupted`.", labels, StringComparison.Ordinal);
        Assert.Contains(
            "- Demo project, https://github.com/orgs/example/projects/1, linked to example/demo-project: Todo, In Progress, Done\n"
            + "- Marketing site, https://github.com/orgs/example/projects/2: Backlog, Ready, Doing, Review, Shipped. **It is not named as GitHub names a board: the command says which status is which.**\n",
            boards,
            StringComparison.Ordinal);
        Assert.Contains("The open milestones of example/demo-project, each with its open tasks:\n\n- v1.0.0: 15 tasks, due 2030-03-01\n- v1.1.0: 6 tasks, due 2030-06-01\n- Later: 9 tasks\n", milestones, StringComparison.Ordinal);
    }

    // The projects the steps are told over: one that toobusy has never seen, one whose settings do not validate,
    // and one that is set up, with a board, a skill that is written and commands around a task, and with labels
    // that need quotes.
    public static TheoryData<string> ProjectsOfAssist =>
    [
        "",
        Settings.Replace("type = \"github\"", "type = \"jira\"", StringComparison.Ordinal),
        Settings,
        SettingsWithABoard.Replace("take = [\"feature\", \"bug\"]", "take = [\"feature\", \"good first issue\"]", StringComparison.Ordinal) + SkillOfTheProject + "\n[task]\nbefore = \"scripts/dev.sh ports free\"\nafter = \"scripts/dev.sh server stop\"\n\n[workspace]\nbranch = \"develop\"\n",
    ];

    [Theory]
    [MemberData(nameof(ProjectsOfAssist))]
    public async Task EveryCommandAStepOfAssistNamesIsOneTheToolHas(string settings)
    {
        GitRepository();
        if (settings.Length > 0)
            WriteSettings(settings);
        if (settings.Contains(SkillOfTheProject, StringComparison.Ordinal))
            WriteSkill("task-auto");
        var processes = GitHubForAssist(labels: "bug\nfeature\n", milestones: "1\tv1\t\t3\n");
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
        Assert.Contains(commands, command => command.EndsWith(" --branch <name> --before \"<command>\" --after \"<command>\"", StringComparison.Ordinal));
        Assert.Contains(commands, command => command.EndsWith(" --no-branch --no-before --no-after", StringComparison.Ordinal));
        Assert.Contains("toobusy queue --json", commands);
        if (settings.Contains(SkillOfTheProject, StringComparison.Ordinal))
            Assert.Contains(texts, text => text.Contains("The skill is written already", StringComparison.Ordinal));
        foreach (var command in commands)
        {
            var parsed = root.Parse([.. System.CommandLine.Parsing.CommandLineParser.SplitCommandLine(command).Skip(1)]);
            Assert.True(parsed.Errors.Count == 0, $"{command}: {string.Join("; ", parsed.Errors.Select(wrong => wrong.Message))}");
            Assert.Empty(parsed.UnmatchedTokens);
        }
    }

    [Fact]
    public async Task TheHelpOfTheToolAndOfInitWorkOutsideAGitRepositoryAndSayWhereAnAssistantStarts()
    {
        const string Said = "An assistant that sets a project up for its owner starts with `toobusy init --assist`.";

        var (exit, tool, _) = await RunAsync("--help");
        var (_, init, _) = await RunAsync("init", "--help");

        Assert.Equal(0, exit);
        Assert.Contains("Usage:", tool, StringComparison.Ordinal);
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
}
