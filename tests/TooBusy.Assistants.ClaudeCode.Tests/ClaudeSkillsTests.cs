using TooBusy.Core.Assistant;

namespace TooBusy.Assistants.ClaudeCode.Tests;

public sealed class ClaudeSkillsTests : IDisposable
{
    readonly DirectoryInfo folder = Directory.CreateTempSubdirectory("toobusy-skills-");
    readonly string root;
    readonly string configuration;

    public ClaudeSkillsTests()
    {
        root = folder.CreateSubdirectory("rocket").FullName;
        configuration = folder.CreateSubdirectory("claude").FullName;
    }

    public void Dispose() => folder.Delete(recursive: true);

    [Fact]
    public void ASkillThatNobodyWroteIsToBeWrittenAmongTheSkillsOfTheProject()
    {
        Assert.Equal(new SkillLook(false, Path.Combine(".claude", "skills", "task-auto", "SKILL.md")), Find("task-auto"));
    }

    [Fact]
    public void ASkillOfTheProjectIsFoundAndShownFromItsRoot()
    {
        Write(root, ".claude", "skills", "task-auto", "SKILL.md");

        Assert.Equal(new SkillLook(true, Path.Combine(".claude", "skills", "task-auto", "SKILL.md")), Find("task-auto"));
        Assert.False(Find("night-shift").Found);
    }

    [Fact]
    public void ACommandOfTheProjectIsASkillToo()
    {
        Write(root, ".claude", "commands", "task-auto.md");

        Assert.Equal(new SkillLook(true, Path.Combine(".claude", "commands", "task-auto.md")), Find("task-auto"));
    }

    [Fact]
    public void ASkillOfTheUserIsFoundInClaudeCodesOwnFolder()
    {
        Write(configuration, "skills", "task-auto", "SKILL.md");
        Assert.Equal(new SkillLook(true, Path.Combine(configuration, "skills", "task-auto", "SKILL.md")), Find("task-auto"));

        Write(configuration, "commands", "night-shift.md");
        Assert.Equal(new SkillLook(true, Path.Combine(configuration, "commands", "night-shift.md")), Find("night-shift"));
    }

    [Fact]
    public void AFolderWithoutItsSkillFileIsNoSkill()
    {
        Directory.CreateDirectory(Path.Combine(root, ".claude", "skills", "task-auto"));

        Assert.False(Find("task-auto").Found);
    }

    [Fact]
    public void ASkillOfAPluginIsNotLookedFor()
    {
        Assert.Equal(new SkillLook(null, "toolbox:task-auto"), Find("toolbox:task-auto"));
    }

    [Fact]
    public void ClaudeCodesOwnFolderIsTheOneOfTheVariableOrOfTheHomeFolder()
    {
        Assert.Equal(Path.Combine("/home/ann", ".claude"), ClaudeFolders.Configuration("/home/ann", _ => null));
        Assert.Equal("/elsewhere", ClaudeFolders.Configuration("/home/ann", name => name == "CLAUDE_CONFIG_DIR" ? "/elsewhere" : null));
    }

    SkillLook Find(string name) => ClaudeSkills.Find(root, configuration, name);

    static void Write(params string[] path)
    {
        var file = Path.Combine(path);
        Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        File.WriteAllText(file, "---\nname: task-auto\n---\n");
    }
}
