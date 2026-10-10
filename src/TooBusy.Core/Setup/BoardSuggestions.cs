namespace TooBusy.Core.Setup;

// What the question about the board does with what is typed.
public static class BoardSuggestions
{
    const string Projects = "/projects/";

    // The address of a board out of what people paste: the address of any page of the project.
    // What is not such an address is given back as it is, trimmed: whether it is a board is for the caller to check.
    public static string AddressOf(string answer)
    {
        var text = answer.Trim().Split('?', '#')[0].TrimEnd('/');
        var number = text.IndexOf(Projects, StringComparison.Ordinal) + Projects.Length;
        if (number < Projects.Length)
            return text;

        var end = number;
        while (end < text.Length && char.IsAsciiDigit(text[end]))
            end++;
        return end > number && end < text.Length && text[end] == '/' ? text[..end] : text;
    }

    // The boards that fit what is typed, the best first: the board of that address, then a board whose title starts
    // with it, then one that has it in its title, then one that has it in its address, as the name of its owner is.
    // Nothing typed gives them all.
    public static IReadOnlyList<SetupBoard> Matching(IReadOnlyList<SetupBoard> known, string typed)
    {
        var text = AddressOf(typed);
        return text.Length == 0
            ? known
            : [.. known.Select(board => (Board: board, Rank: Rank(board, text))).Where(match => match.Rank < 4).OrderBy(match => match.Rank).Select(match => match.Board)];
    }

    static int Rank(SetupBoard board, string text) =>
        board.Address.Equals(text, StringComparison.OrdinalIgnoreCase) ? 0
        : board.Title.StartsWith(text, StringComparison.OrdinalIgnoreCase) ? 1
        : board.Title.Contains(text, StringComparison.OrdinalIgnoreCase) ? 2
        : board.Address.Contains(text, StringComparison.OrdinalIgnoreCase) ? 3
        : 4;
}
