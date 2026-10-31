namespace TooBusy.Core.Assistant;

// What is found of a skill by its name. Found is null for a skill that cannot be looked for, as one that a plugin of
// the assistant brings. Place is where the skill is, shown from the root of the project when it is under it, or,
// for one that is not found, where it is to be written.
public sealed record SkillLook(bool? Found, string Place);
