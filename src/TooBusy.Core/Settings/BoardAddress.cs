using System.Globalization;
using System.Text.RegularExpressions;

namespace TooBusy.Core.Settings;

// The address of a board, `https://github.com/orgs/<org>/projects/<n>` or
// `https://github.com/users/<user>/projects/<n>`, in its parts: whether an organisation owns it or a user, the login of
// the owner, and the number of the board among the boards of the owner. It is the one rule of what a board is, for the
// settings, the setup and the tracker alike.
public sealed partial record BoardAddress(bool Organisation, string Login, int Number)
{
    // Null when the text is not the address of a board.
    public static BoardAddress? Parse(string text) =>
        Pattern().Match(text) is { Success: true } match
        && int.TryParse(match.Groups[3].ValueSpan, NumberStyles.None, CultureInfo.InvariantCulture, out var number)
            ? new BoardAddress(match.Groups[1].Value == "orgs", match.Groups[2].Value, number)
            : null;

    [GeneratedRegex(@"^https://github\.com/(orgs|users)/([A-Za-z0-9-]+)/projects/([0-9]+)\z")]
    private static partial Regex Pattern();
}
