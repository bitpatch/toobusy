namespace TooBusy.Core.Queue;

// What the user chose to work on: the milestone with the given title, or, with no title, tasks whatever their
// milestone. It is the choice of one person on one machine, not a setting of the project.
public sealed record MilestoneChoice(string? Title)
{
    public static MilestoneChoice None { get; } = new((string?)null);
}

// The open milestones of a repository of the tracker.
public interface IMilestones
{
    // Null when they cannot be read.
    Task<IReadOnlyList<Milestone>?> ReadOpenAsync(string repository, CancellationToken cancellationToken);
}

public enum MilestoneState
{
    // The user has not chosen: nothing can be run yet.
    NotChosen,

    // The chosen milestone is open.
    Chosen,

    // The user chose to work without a milestone.
    NoMilestone,

    // The chosen milestone is not among the open ones: it was closed, renamed or deleted, and is to be chosen again.
    Gone,

    // The milestones cannot be read, so the choice stands as it was made.
    Unverified,
}

// Where the choice of the user stands against the open milestones. Milestone is the chosen one when it is open.
public sealed record MilestoneStanding(MilestoneState State, MilestoneChoice? Choice, Milestone? Milestone)
{
    // Whether there is something to run with.
    public bool Ready => State is MilestoneState.Chosen or MilestoneState.NoMilestone or MilestoneState.Unverified;

    // The choice in a few words, as a screen and a message name it.
    public string Name => State switch
    {
        MilestoneState.NotChosen => "not chosen",
        MilestoneState.NoMilestone => "no milestone",
        _ => Choice!.Title!,
    };

    // `open` is null when the milestones cannot be read.
    public static MilestoneStanding Of(MilestoneChoice? choice, IReadOnlyList<Milestone>? open) => choice switch
    {
        null => new MilestoneStanding(MilestoneState.NotChosen, null, null),
        { Title: null } => new MilestoneStanding(MilestoneState.NoMilestone, choice, null),
        _ when open is null => new MilestoneStanding(MilestoneState.Unverified, choice, null),
        _ when Find(open, choice.Title) is { } found => new MilestoneStanding(MilestoneState.Chosen, choice, found),
        _ => new MilestoneStanding(MilestoneState.Gone, choice, null),
    };

    // The open milestone with the title: as it is written, or, when no title is written so, the only one that
    // differs in the case of its letters.
    public static Milestone? Find(IReadOnlyList<Milestone> open, string title) =>
        open.FirstOrDefault(milestone => milestone.Title == title)
        ?? (open.Where(milestone => milestone.Title.Equals(title, StringComparison.OrdinalIgnoreCase)).ToList() is [var only] ? only : null);
}
