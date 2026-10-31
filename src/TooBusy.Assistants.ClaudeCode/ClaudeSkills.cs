using TooBusy.Core.Assistant;

namespace TooBusy.Assistants.ClaudeCode;

// The skills Claude Code loads by a command at the head of a message: a folder with a `SKILL.md` among the skills of
// the project or of the user, or a file among their commands, as skills were written before. A skill of a plugin,
// which is named with the plugin before a colon, is not looked for.
public static class ClaudeSkills
{
    public static SkillLook Find(string root, string configuration, string name)
    {
        var written = Path.Combine(".claude", "skills", name, "SKILL.md");
        if (name.Contains(':', StringComparison.Ordinal))
            return new SkillLook(null, name);

        string[] ofProject = [written, Path.Combine(".claude", "commands", name + ".md")];
        if (ofProject.FirstOrDefault(place => File.Exists(Path.Combine(root, place))) is { } here)
            return new SkillLook(true, here);

        string[] ofUser = [Path.Combine(configuration, "skills", name, "SKILL.md"), Path.Combine(configuration, "commands", name + ".md")];
        return ofUser.FirstOrDefault(File.Exists) is { } own ? new SkillLook(true, own) : new SkillLook(false, written);
    }
}
