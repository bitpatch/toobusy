using TooBusy.Core.Assistant;

namespace TooBusy.Cli.Terminal;

// The question about the model the tasks are done with by default: the assistant's own, the models that have a
// short name, and after them any other one, whose name is typed.
public static class ModelScreen
{
    const string Label = "Model";

    // `chosen` is null when the user has not chosen yet. Gives the choice, or null when Escape is pressed or `Back`
    // is chosen.
    public static ModelChoice? Ask(Page page, ModelChoice? chosen)
    {
        var offered = ClaudeCodeOptions.Models;
        var known = chosen?.Name is { } name ? offered.ToList().IndexOf(name) : -1;
        var other = chosen?.Name is not null && known < 0 ? chosen.Name : "";
        Choice[] choices =
        [
            new("Assistant's own", "no model is named: the assistant takes its default"),
            .. offered.Select(model => new Choice(model)),
            new("Other…", "any model, by its name", other),
        ];
        var at = chosen?.Name is null ? 0 : known < 0 ? choices.Length - 1 : known + 1;

        while (true)
        {
            if (Picker.Pick(page, Picker.Head(Label, "The model the tasks are done with by default."), choices, at: at) is not { } picked)
                return null;
            if (picked == 0)
                return ModelChoice.AssistantsOwn;
            if (picked <= offered.Count)
                return new ModelChoice(offered[picked - 1]);

            // From the name Escape comes back to the list, wherever Escape goes from the list.
            var (leaves, keys) = (page.EscapeLeaves, page.Keys);
            (page.EscapeLeaves, page.Keys) = (false, "esc back");
            var typed = Prompt.Ask(page, Label, "The name of the model, as the assistant takes it.", other, text => text.Trim().Length == 0 ? "Type the name of a model." : null);
            (page.EscapeLeaves, page.Keys) = (leaves, keys);
            if (typed is not null)
                return new ModelChoice(typed);
            at = picked;
        }
    }

    // The choice in a few words, as a screen and a message name it.
    public static string Describe(ModelChoice? choice) => choice switch
    {
        null => "not chosen",
        { Name: null } => "the assistant's own",
        _ => choice.Name,
    };
}
