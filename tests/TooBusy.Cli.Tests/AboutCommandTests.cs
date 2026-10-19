using TooBusy.Core.Queue;
using TooBusy.Core.Run;

namespace TooBusy.Cli.Tests;

// `toobusy about`: how a run works, with the labels, the skill and the commands of the project.
public sealed class AboutCommandTests : CliAppTests
{
    [Fact]
    public async Task AboutTellsHowARunWorksOutsideAGitRepository()
    {
        var (exit, output, error) = await RunAsync("about");

        Assert.Equal(0, exit);
        Assert.Equal("", error);
        Assert.StartsWith("# How toobusy works\n", output, StringComparison.Ordinal);
        Assert.Contains("No settings are read here, so its labels are not named below", output, StringComparison.Ordinal);
        Assert.Contains(Briefing.Task(new(12, "<the title of the task>", "<the address of the task>", [], BoardStatus.Todo, "Todo", [], []), new NewTasks("<the manual label>", [], "<the milestone the user works on>", "<the address of the board>"), interrupted: false), output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AboutNamesTheLabelsTheSkillTheCommandsAndThePermissionModeOfAProjectThatIsSetUp()
    {
        GitRepository();
        WriteSettings(Settings + "\npermission-mode = \"dontAsk\"\n" + SkillOfTheProject + "\n[task]\nbefore = \"scripts/dev.sh ports free\"\nafter = \"scripts/dev.sh server stop\"\n");

        var (exit, output, error) = await RunAsync("about");

        Assert.Equal(0, exit);
        Assert.Equal("", error);
        Assert.Contains("- A task with the label `manual` is not taken.\n- The project has no board", output, StringComparison.Ordinal);
        Assert.Contains("takes the `interrupted` label off a task that had it", output, StringComparison.Ordinal);
        Assert.Contains("This project names the skill `task-auto`, so its first message begins with `/task-auto #12 <the title of the task>`, as shown below.", output, StringComparison.Ordinal);
        Assert.Contains("This project runs `scripts/dev.sh ports free` before a task and `scripts/dev.sh server stop` after it.", output, StringComparison.Ordinal);
        Assert.Contains("This project names the permission mode `dontAsk` of Claude Code in its settings, `assistant.permission-mode`: a session is started in it", output, StringComparison.Ordinal);
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
}
