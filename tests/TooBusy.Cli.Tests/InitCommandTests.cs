using System.Text.RegularExpressions;
using TooBusy.Cli.Terminal;
using TooBusy.Core.Processes;

namespace TooBusy.Cli.Tests;

// `toobusy init`: the setup on its screen, with `--yes` and without a terminal, and a demo of it.
public sealed class InitCommandTests : CliAppTests
{
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

        // No project; `manual` blocks; any label; `manual` is the manual label too, the one the list points at
        // as proposed; a new label for interrupted tasks, by the name that is proposed; no skill, the proposed name
        // being deleted; yes.
        var keys = new Keys().Press(
            Keys.Tab, Keys.Tab, Keys.Enter, Keys.Down, Keys.Space, Keys.Enter, Keys.Enter,
            Keys.Enter, Keys.Enter, Keys.Enter, Keys.Control('u'), Keys.Enter, Keys.Enter);

        var (exit, output, _) = await RunWithKeysAsync(keys, processes, "init");

        Assert.Equal(0, exit);
        Assert.EndsWith(
            "✔ The settings are written to .toobusy/settings.toml. Commit the file." + Environment.NewLine
            + "`toobusy about` tells how a run works, for you and for your assistant." + Environment.NewLine,
            Report(output),
            StringComparison.Ordinal);
        var written = File.ReadAllText(Path.Combine(folder.FullName, ".toobusy", "settings.toml"));
        Assert.Contains("blocking = [\"manual\"]", written, StringComparison.Ordinal);
        Assert.Contains("manual = \"manual\"\ninterrupted = \"interrupted\"", written, StringComparison.Ordinal);
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
        foreach (var option in (string[])["--board <URL>", "--no-board", "--new-board <title>", "--board-owner <login>", "--blocking-label <name>", "--no-blocking-labels", "--take-label <name>", "--no-take-labels", "--manual-label <name>", "--interrupt-label <name>", "--skill <name>", "--no-skill", "--before <command>", "--no-before", "--after <command>", "--no-after", "--branch <name>", "--no-branch", "--permission-mode <mode>", "-y, --yes"])
            Assert.Contains(option, output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task TheHelpOfInitSaysHowASettingThatIsNotAskedIsChangedAndEndsWithExamples()
    {
        var (exit, output, _) = await RunAsync("init", "--help");
        var (_, run, _) = await RunAsync("run", "--help");
        var said = Regex.Replace(output, @"\s+", " ");

        Assert.Equal(0, exit);
        Assert.Contains("Sets the project up or changes its settings", said, StringComparison.Ordinal);
        Assert.Contains("`--before`, `--after`, `--branch` and `--permission-mode` are not asked: give them as options.", said, StringComparison.Ordinal);
        Assert.Contains("keep every other setting as it is", said, StringComparison.Ordinal);
        Assert.EndsWith(
            string.Join(Environment.NewLine,
                "Examples:",
                "  Set the command before a task; the other settings stay as they are:",
                "    toobusy init --before \"scripts/dev.sh ports free\" -y",
                "  Take the command after a task away:",
                "    toobusy init --no-after -y",
                "",
                ""),
            output,
            StringComparison.Ordinal);
        Assert.DoesNotContain("Examples:", run, StringComparison.Ordinal);
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
                "✔ Manual label     manual",
                "✔ Interrupt label  interrupted",
                "✔ Skill            task-auto",
                "✔ Assistant        Claude Code",
                "The label “interrupted” was made in acme/rocket.",
                "✔ The settings are written to .toobusy/settings.toml. Commit the file.",
                "`toobusy about` tells how a run works, for you and for your assistant.",
                "",
            ],
            output.Split(Environment.NewLine));
        var written = File.ReadAllText(Path.Combine(folder.FullName, ".toobusy", "settings.toml"));
        Assert.Contains("blocking = []\ntake = []\nmanual = \"manual\"\ninterrupted = \"interrupted\"", written, StringComparison.Ordinal);
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
        Assert.EndsWith("[session]\nskill = \"night-shift\"\n", File.ReadAllText(Path.Combine(folder.FullName, ".toobusy", "settings.toml")), StringComparison.Ordinal);

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
        var processes = GitHub(milestones: "", labels: "bug\nmanual\ninterrupted\n");
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

    [Fact]
    public async Task WithYesTheOptionsOfTheBranchNameItAndTakeItAway()
    {
        GitRepository();
        var processes = GitHub(milestones: "", labels: "bug\nmanual\ninterrupted\n");
        var file = Path.Combine(folder.FullName, ".toobusy", "settings.toml");

        var (exit, output, _) = await RunAsync(Context(processes), ["init", "--yes", "--no-skill", "--branch", "develop"]);

        Assert.Equal(0, exit);
        Assert.Contains("✔ Assistant        Claude Code" + Environment.NewLine + "✔ Branch           none → develop" + Environment.NewLine, output, StringComparison.Ordinal);
        Assert.EndsWith("[workspace]\nbranch = \"develop\"\n", File.ReadAllText(file), StringComparison.Ordinal);

        (exit, output, _) = await RunAsync(Context(processes), ["init", "--yes", "--no-branch"]);

        Assert.Equal(0, exit);
        Assert.Contains("✔ Branch           develop → none" + Environment.NewLine, output, StringComparison.Ordinal);
        Assert.DoesNotContain("branch =", File.ReadAllText(file), StringComparison.Ordinal);
    }

    [Fact]
    public async Task WithYesTheOptionOfThePermissionModeNamesItInTheSectionOfTheAssistant()
    {
        GitRepository();
        var processes = GitHub(milestones: "", labels: "bug\nmanual\ninterrupted\n");
        var file = Path.Combine(folder.FullName, ".toobusy", "settings.toml");

        var (exit, output, _) = await RunAsync(Context(processes), ["init", "--yes", "--no-skill", "--permission-mode", "dontAsk"]);

        Assert.Equal(0, exit);
        Assert.Contains("✔ Assistant        Claude Code" + Environment.NewLine + "✔ Permission mode  auto by default → dontAsk" + Environment.NewLine, output, StringComparison.Ordinal);
        Assert.Contains("[assistant]\ntype = \"claude-code\"\npermission-mode = \"dontAsk\"\n", File.ReadAllText(file), StringComparison.Ordinal);
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
    public async Task WithYesEveryOptionIsTheAnswer()
    {
        GitRepository();
        var processes = GitHubWithProjects(labels: "bug\nfeature\nmanual\ndraft\n");

        var (exit, output, _) = await RunAsync(
            Context(processes),
            ["init", "-y", "--board", "https://github.com/orgs/acme/projects/3/views/1", "--blocking-label", "manual", "--blocking-label", "draft", "--take-label", "bug", "--take-label", "feature", "--manual-label", "manual", "--interrupt-label", "paused"]);

        Assert.Equal(0, exit);
        var written = File.ReadAllText(Path.Combine(folder.FullName, ".toobusy", "settings.toml"));
        Assert.Contains("board = \"https://github.com/orgs/acme/projects/3\"", written, StringComparison.Ordinal);
        Assert.Contains("blocking = [\"manual\", \"draft\"]\ntake = [\"bug\", \"feature\"]\nmanual = \"manual\"\ninterrupted = \"paused\"", written, StringComparison.Ordinal);
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

        var (exit, output, error) = await RunAsync(Context(processes), ["init", "--yes", "--board", "https://github.com/orgs/acme/projects/3", "--manual-label", "manual", "--no-skill"]);

        Assert.Equal(0, exit);
        Assert.Equal("", error);
        Assert.Contains("! The board has no status “In Progress”: it has Todo, Doing, Done.", output, StringComparison.Ordinal);
        Assert.Contains("Say which of its statuses is which with `--todo-status`, `--in-progress-status` and `--done-status`", output, StringComparison.Ordinal);
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

    // Each of the options of a status names its own: the one a task is taken from and the one it is moved to when it
    // is done, as the board writes them, and the status between them is the one GitHub names.
    [Fact]
    public async Task WithYesTheOptionsOfTheStatusesNameTheStatusOfATaskToDoAndOfADoneOne()
    {
        GitRepository();
        var processes = GitHubWithProjects(Labels, statuses: "board\nstatus\tBacklog\nstatus\tReady\nstatus\tIn Progress\nstatus\tShipped\n");

        var (exit, output, error) = await RunAsync(
            Context(processes),
            ["init", "--yes", "--board", "https://github.com/orgs/acme/projects/3", "--todo-status", "ready", "--done-status", "Shipped", "--manual-label", "manual", "--no-skill"]);

        Assert.Equal(0, exit);
        Assert.Equal("", error);
        Assert.DoesNotContain("The board has no status", output, StringComparison.Ordinal);
        Assert.Contains("✔ Statuses         Ready, In Progress, Shipped", output, StringComparison.Ordinal);
        Assert.Contains("[tracker.statuses]\ntodo = \"Ready\"\ndone = \"Shipped\"\n", File.ReadAllText(Path.Combine(folder.FullName, ".toobusy", "settings.toml")), StringComparison.Ordinal);
    }

    [Fact]
    public async Task OnTheScreenABoardThatIsNotNamedAsGitHubNamesOneIsAskedWhichOfItsStatusesIsWhich()
    {
        GitRepository();
        var processes = GitHubWithProjects(labels: "bug\nmanual\ninterrupted\n", statuses: "board\nstatus\tBacklog\nstatus\tReady\nstatus\tIn Progress\nstatus\tDone\n");

        // The board, then the second of its statuses for a task to do and what is proposed for the other two, then
        // Enter on every question, the last one included.
        var keys = new Keys().Press(Keys.Enter, Keys.Down, Keys.Enter, Keys.Enter, Keys.Enter, Keys.Enter, Keys.Enter, Keys.Enter, Keys.Enter, Keys.Enter, Keys.Enter);

        var (exit, output, _) = await RunWithKeysAsync(keys, processes, "init", "--board", "https://github.com/orgs/acme/projects/3", "--blocking-label", "manual", "--take-label", "bug", "--manual-label", "manual", "--interrupt-label", "interrupted", "--no-skill");

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

        var (exit, output, _) = await RunWithKeysAsync(keys, processes, "init", "--board", "https://github.com/orgs/acme/projects/3", "--blocking-label", "manual", "--take-label", "bug", "--manual-label", "manual", "--interrupt-label", "interrupted", "--no-skill");

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

        var (exit, output, _) = await RunAsync(Context(processes), ["init", "--yes", "--new-board", "Rocket", "--board-owner", "ann", "--manual-label", "manual"]);

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
        Assert.Contains("blocking = []\ntake = [\"bug\"]\nmanual = \"manual\"\ninterrupted = \"interrupted\"", written, StringComparison.Ordinal);

        (exit, _, _) = await RunAsync(Context(processes), ["init", "--yes", "--no-take-labels", "--no-board"]);

        Assert.Equal(0, exit);
        Assert.Contains("take = []", File.ReadAllText(Path.Combine(folder.FullName, ".toobusy", "settings.toml")), StringComparison.Ordinal);
    }

    [Fact]
    public async Task WithYesAValueThatFailsItsCheckIsAnErrorAndNothingIsWritten()
    {
        GitRepository();
        var processes = GitHub(milestones: "", labels: "bug\nmanual\n");

        var (exit, output, _) = await RunAsync(Context(processes), ["init", "--yes", "--blocking-label", "urgent"]);

        Assert.Equal(1, exit);
        Assert.EndsWith("✘ Blocking labels: acme/rocket has no label “urgent”. The settings were not written." + Environment.NewLine, output, StringComparison.Ordinal);
        Assert.False(Directory.Exists(Path.Combine(folder.FullName, ".toobusy")));
        Assert.DoesNotContain(processes.Asked, asked => asked.Command == "gh" && asked.Arguments.Contains("create"));
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
    [InlineData("toobusy: `--board-owner` goes with `--new-board`", "--board-owner", "acme")]
    [InlineData("toobusy: `--todo-status`, `--in-progress-status` and `--done-status` name the statuses of a board, and do not go with `--no-board`", "--no-board", "--done-status", "Shipped")]
    [InlineData("toobusy: give `--blocking-label` or `--no-blocking-labels`, not both", "--blocking-label", "manual", "--no-blocking-labels")]
    [InlineData("toobusy: give `--take-label` or `--no-take-labels`, not both", "--take-label", "bug", "--no-take-labels")]
    [InlineData("toobusy: give `--skill` or `--no-skill`, not both", "--skill", "task-auto", "--no-skill")]
    [InlineData("toobusy: give `--before` or `--no-before`, not both", "--before", "make free", "--no-before")]
    [InlineData("toobusy: give `--after` or `--no-after`, not both", "--after", "make stop", "--no-after")]
    [InlineData("toobusy: give `--branch` or `--no-branch`, not both", "--branch", "develop", "--no-branch")]
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
        // the manual label, `interrupted` the one of interrupted tasks, and `night-shift` is the skill.
        var keys = new Keys().Press(Keys.Enter, Keys.Enter, Keys.Enter, Keys.Enter, Keys.Enter, Keys.Enter, Keys.Enter);

        var (exit, output, _) = await RunWithKeysAsync(keys, processes, "init", "--no-board", "--blocking-label", "manual", "--take-label", "bug", "--manual-label", "manual", "--interrupt-label", "interrupted", "--skill", "night-shift");

        Assert.Equal(0, exit);
        Assert.Contains("[No project]", output, StringComparison.Ordinal);
        var written = File.ReadAllText(Path.Combine(folder.FullName, ".toobusy", "settings.toml"));
        Assert.Contains("blocking = [\"manual\"]\ntake = [\"bug\"]\nmanual = \"manual\"\ninterrupted = \"interrupted\"", written, StringComparison.Ordinal);
        Assert.Contains("skill = \"night-shift\"", written, StringComparison.Ordinal);
        Assert.DoesNotContain("was made", Report(output), StringComparison.Ordinal);
    }

    [Fact]
    public async Task ADemoOfTheSetupGoesThroughTheStepsAndWritesNothing()
    {
        GitRepository();

        // The first of the boards, no blocking labels, any label, the label `manual` of the made-up ones for the
        // owner, the label `interrupted` of the made-up ones, the skill that is proposed, yes.
        var keys = new Keys().Press(Keys.Enter, Keys.Enter, Keys.Enter, Keys.Enter, Keys.Enter, Keys.Enter, Keys.Enter);

        var (exit, output, error) = await RunWithKeysAsync(keys, "init", "--demo");

        Assert.Equal(0, exit);
        Assert.Equal("", error);
        Assert.StartsWith("\u001b[?1049h", output, StringComparison.Ordinal);
        Assert.Contains("Setting up this project · demo", output, StringComparison.Ordinal);
        Assert.Contains("The answers above will be written to .toobusy/settings.toml.", output, StringComparison.Ordinal);
        Assert.DoesNotContain("will be made", output, StringComparison.Ordinal);
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
                "✔ Manual label     manual",
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
        Assert.Contains("✔ Manual label     manual", Report(output), StringComparison.Ordinal);
        Assert.Contains("✔ Skill            none", Report(output), StringComparison.Ordinal);
        Assert.EndsWith("Nothing to change: the settings already say this." + Environment.NewLine, Report(output), StringComparison.Ordinal);
    }

    [Fact]
    public async Task ADemoOfTheSetupReadsTheOriginAndTheBoardsOfTheUser()
    {
        GitRepository();
        var processes = new ScriptedProcesses(command => new ProcessResult(ProcessStatus.Exited, 0, command == "git"
            ? "https://github.com/bitpatch/toobusy.git\n"
            : "owner\tuser\tdenis\nboard\thttps://github.com/users/denis/projects/3\tFatgard\nlinked\thttps://github.com/orgs/bitpatch/projects/4\tToobusy\n", ""));

        // The board that is linked, no blocking labels, any label, the label `manual` of the made-up ones for the
        // owner, the label `interrupted` of the made-up ones, the skill that is proposed, yes.
        var keys = new Keys().Press(Keys.Enter, Keys.Enter, Keys.Enter, Keys.Enter, Keys.Enter, Keys.Enter, Keys.Enter);

        var (exit, output, _) = await RunWithKeysAsync(keys, processes, "init", "--demo");

        Assert.Equal(0, exit);
        Assert.Contains(processes.Asked, asked => asked.Command == "git" && asked.Arguments.SequenceEqual(["-C", folder.FullName, "remote", "get-url", "origin"]));
        Assert.Contains(processes.Asked, asked => asked.Command == "gh" && asked.Arguments.Contains("owner=bitpatch") && asked.Arguments.Contains("name=toobusy"));
        Assert.Contains("Toobusy  https://github.com/orgs/bitpatch/projects/4  linked to this repository", output, StringComparison.Ordinal);
        Assert.Contains("✔ Project          Toobusy  https://github.com/orgs/bitpatch/projects/4", Report(output), StringComparison.Ordinal);
        Assert.Contains("✔ Manual label     manual", Report(output), StringComparison.Ordinal);
        Assert.Contains("✔ Interrupt label  interrupted", Report(output), StringComparison.Ordinal);
        Assert.DoesNotContain(processes.Asked, asked => asked.Arguments.Contains("create"));
    }

    [Fact]
    public async Task ADemoOfTheSetupHasMadeUpBoardsThatCallTheirStatusesOtherwise()
    {
        GitRepository();
        var processes = new ScriptedProcesses(command => new ProcessResult(ProcessStatus.Exited, 0, command == "git" ? "https://github.com/bitpatch/toobusy.git\n" : "owner\tuser\tdenis\n", ""));

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
        var processes = new ScriptedProcesses(_ => new ProcessResult(ProcessStatus.NotFound, 0, "", ""));

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
        var processes = new ScriptedProcesses(command => new ProcessResult(ProcessStatus.Exited, 0, command == "git"
            ? "https://github.com/bitpatch/toobusy.git\n"
            : "owner\torg\tbitpatch\nowner\tuser\tdenis\n", ""));
        var keys = new Keys().Press(Keys.Tab, Keys.Tab).Type("Rocket").Until("❯ example", Keys.Down).Press(Keys.Enter, Keys.Enter, Keys.Enter, Keys.Enter, Keys.Enter, Keys.Enter, Keys.Enter, Keys.Enter);

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

    // The same machine, whose `gh` answers what is asked about projects too: a board can be read, made for the
    // user and linked to the repository.
    static ScriptedProcesses GitHubWithProjects(string labels, string statuses = NewBoard) => new((command, arguments) => new ProcessResult(ProcessStatus.Exited, 0, command switch
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
    static ScriptedProcesses ElsewhereWithProjects() => new((command, arguments) => new ProcessResult(ProcessStatus.Exited, 0, command switch
    {
        "git" => "https://gitlab.com/acme/rocket.git\n",
        "gh" when arguments.Any(argument => argument.Contains("createProjectV2", StringComparison.Ordinal)) => "https://github.com/users/ann/projects/9\n",
        "gh" when arguments.Any(argument => argument.Contains("(login: $login) { id }", StringComparison.Ordinal)) => "U_1\n",
        "gh" when arguments[0] == "api" => "owner\tuser\tann\n",
        _ => "",
    }, ""));
}
