using System.Globalization;
using System.Text.RegularExpressions;

namespace TooBusy.Core.Queue;

// The order milestones are offered in: those with a version in the title first, the lowest version first, and the
// rest after them by their titles.
public static partial class MilestoneOrder
{
    public static IReadOnlyList<Milestone> Sorted(IEnumerable<Milestone> milestones) =>
    [
        .. milestones
            .Select(milestone => (Milestone: milestone, Version: VersionOf(milestone.Title)))
            .OrderBy(candidate => candidate.Version is null)
            .ThenBy(candidate => candidate.Version ?? [], VersionOrder.Instance)
            .ThenBy(candidate => candidate.Milestone.Title, StringComparer.OrdinalIgnoreCase)
            .Select(candidate => candidate.Milestone),
    ];

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
