namespace TooBusy.Core.Assistant;

// The model the tasks are done with by default: the one with the given name, or, with no name, the one the assistant
// takes by itself. It is the choice of one person on one machine, not a setting of the project.
public sealed record ModelChoice(string? Name)
{
    public static ModelChoice AssistantsOwn { get; } = new((string?)null);
}
