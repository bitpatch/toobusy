using System.Globalization;
using System.Text.RegularExpressions;
using TooBusy.Core.Settings;

namespace TooBusy.Core.Queue;

// Which of the open milestones is the current one under each rule of the settings.
public static partial class MilestoneRules
{
    public static Milestone? Choose(MilestoneSettings settings, IReadOnlyList<Milestone> open) => settings.Rule switch
    {
        MilestoneRule.LowestVersion => open
            .Select(milestone => (Milestone: milestone, Version: VersionOf(milestone.Title)))
            .Where(candidate => candidate.Version is not null)
            .OrderBy(candidate => candidate.Version!, VersionOrder.Instance)
            .Select(candidate => candidate.Milestone)
            .FirstOrDefault(),
        MilestoneRule.EarliestDue => open.Where(milestone => milestone.Due is not null).OrderBy(milestone => milestone.Due).FirstOrDefault(),
        MilestoneRule.Fixed => open.FirstOrDefault(milestone => milestone.Title == settings.Title),
        _ => null,
    };

    // The first run of dot-separated numbers in the title: `v.0.2.0`, `v1.4`, `Release 2.0`.
    public static IReadOnlyList<long>? VersionOf(string title)
    {
        var match = VersionPattern().Match(title);
        return match.Success ? [.. match.Value.Split('.').Select(number => long.Parse(number, CultureInfo.InvariantCulture))] : null;
    }

    [GeneratedRegex(@"\d{1,18}(\.\d{1,18})*")]
    private static partial Regex VersionPattern();

    // Number by number; a version that ends earlier counts zeros for the rest.
    sealed class VersionOrder : IComparer<IReadOnlyList<long>>
    {
        public static VersionOrder Instance { get; } = new();

        public int Compare(IReadOnlyList<long>? left, IReadOnlyList<long>? right)
        {
            for (var index = 0; index < Math.Max(left!.Count, right!.Count); index++)
            {
                var order = left.ElementAtOrDefault(index).CompareTo(right.ElementAtOrDefault(index));
                if (order != 0)
                    return order;
            }

            return 0;
        }
    }
}
