using System.Globalization;
using TooBusy.Core.Queue;

namespace TooBusy.Cli.Terminal;

// The question about the milestone to work on: the open milestones of the repository, in the order they are to be
// done, and after them working without a milestone.
public static class MilestoneScreen
{
    // `open` is null when the milestones cannot be read. Gives the choice, or null when Escape is pressed or `Back`
    // is chosen.
    public static MilestoneChoice? Ask(Page page, IReadOnlyList<Milestone>? open, MilestoneStanding standing)
    {
        var offered = MilestoneOrder.Sorted(open ?? []);
        Choice[] choices =
        [
            .. offered.Select(milestone => new Choice(milestone.Title, Describe(milestone))),
            new("No milestone", "take tasks whatever their milestone"),
        ];
        var reason = standing.State == MilestoneState.Gone ? $"The milestone “{standing.Name}” is not open any more. Choose again."
            : open is null ? "The milestones cannot be read from GitHub, so there are none to choose from."
            : null;
        var at = standing.Choice switch
        {
            { Title: null } => offered.Count,
            { Title: var title } => Math.Max(0, offered.ToList().FindIndex(milestone => milestone.Title == title)),
            _ => 0,
        };

        var picked = Picker.Pick(page, Picker.Head("Milestone", "The milestone to work on: only its tasks are taken.", reason), choices, at: at);
        return picked is not { } index ? null : index < offered.Count ? new MilestoneChoice(offered[index].Title) : MilestoneChoice.None;
    }

    // What is known of a milestone besides its title: when it is due and how much of it is left.
    public static string Describe(Milestone milestone)
    {
        var tasks = milestone.OpenTasks switch
        {
            0 => "no open tasks",
            1 => "1 open task",
            var number => string.Create(CultureInfo.InvariantCulture, $"{number} open tasks"),
        };
        return milestone.Due is { } due ? $"due {due.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)} · {tasks}" : tasks;
    }
}
