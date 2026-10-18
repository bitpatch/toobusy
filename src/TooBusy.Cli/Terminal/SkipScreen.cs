namespace TooBusy.Cli.Terminal;

// What `Skip labels` of the menu opens: the labels of the repository, any of which the runs of the menu leave out
// with their tasks and the tasks that wait for them, for as long as the page is open.
public static class SkipScreen
{
    public const string Label = "Skip labels";

    public const string Hint = "A run of this menu leaves their tasks out, until toobusy is closed.";

    // `labels` are those to choose from, null when they cannot be read, and then `unread` says why: the list has
    // nothing but `Back`. `skipped` are the labels that are chosen; one the repository does not have stays among the
    // choices, so that it can be left. Gives the labels that are chosen, or null when Escape is pressed or `Back` is
    // chosen.
    public static IReadOnlyList<string>? Ask(Page page, IReadOnlyList<string>? labels, string? unread, IReadOnlyList<string> skipped)
    {
        page.EscapeLeaves = false;
        page.Keys = "esc back";
        if (labels is null)
        {
            Picker.Pick(page, Picker.Head(Label, Hint, unread), []);
            return null;
        }

        List<string> options = [.. labels.Concat(skipped).Distinct(StringComparer.OrdinalIgnoreCase)];
        var chosen = Enumerable.Range(0, options.Count).Where(index => skipped.Contains(options[index], StringComparer.OrdinalIgnoreCase)).ToList();
        return Picker.PickMany(page, Picker.Head(Label, Hint), [.. options.Select(option => new Choice(option))], chosen) is { } picked ? [.. picked.Select(index => options[index])] : null;
    }
}
