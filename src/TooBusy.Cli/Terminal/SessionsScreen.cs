using System.Globalization;

namespace TooBusy.Cli.Terminal;

// The question about how many sessions work at once: one in the working copy, or several, each in a git worktree of its
// own. They share the usage limits of the account.
public static class SessionsScreen
{
    // As many are offered; a number that was given to `toobusy sessions` beyond them is offered too.
    const int Offered = 6;

    // `chosen` is null when the user never chose: that is one session. Gives the number, or null when Escape is pressed
    // or `Back` is chosen.
    public static int? Ask(Page page, int? chosen)
    {
        var now = chosen ?? 1;
        List<int> counts = [.. Enumerable.Range(1, Math.Max(Offered, now))];
        var picked = Picker.Pick(
            page,
            Picker.Head("Sessions", "How many sessions work at once. They share the usage limits of your account."),
            [.. counts.Select(count => new Choice(count.ToString(CultureInfo.InvariantCulture), count == 1 ? "in the working copy, one task after another" : "each in a git worktree of its own", Current: chosen == count))],
            at: now - 1);
        return picked is { } index ? counts[index] : null;
    }

    // The number in a few words, as the menu and the report show it.
    public static string Describe(int? chosen) => (chosen ?? 1) == 1 ? "1 session" : string.Create(CultureInfo.InvariantCulture, $"{chosen} sessions");
}
