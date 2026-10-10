using System.Globalization;
using TooBusy.Core.Run;

namespace TooBusy.Cli.Terminal;

// The question about how much of the weekly limit a run may use: the shares that are offered, from the least to the
// most, and among them the one in force when it is another.
public static class LimitScreen
{
    // `share` is the one in force: the pointer starts on it. Gives the share, or null when Escape is pressed or
    // `Back` is chosen.
    public static int? Ask(Page page, int share)
    {
        var shares = UsageShare.Offered.Append(share).Distinct().Order().ToList();
        var picked = Picker.Pick(
            page,
            Picker.Head("Weekly limit", "A run takes no next task once this much of the weekly limit is used."),
            [.. shares.Select(offered => new Choice(Describe(offered)))],
            at: shares.IndexOf(share));
        return picked is { } index ? shares[index] : null;
    }

    public static string Describe(int share) => string.Create(CultureInfo.InvariantCulture, $"{share}%");
}
