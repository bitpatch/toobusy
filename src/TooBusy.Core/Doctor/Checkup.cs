using TooBusy.Core.Settings;

namespace TooBusy.Core.Doctor;

// The checks of everything a run needs, in their order. A check whose need is not met is skipped and says what it
// waits for. `origin` is the `owner/name` of the `origin` remote when it is a GitHub one.
public sealed class Checkup(ICheckupMachine machine, ISettingsStore settings, string? origin)
{
    public const string Git = "git";
    public const string TrackerTool = "GitHub CLI";
    public const string TrackerLogin = "GitHub login";
    public const string Assistant = "Claude Code";
    public const string AssistantLogin = "Claude Code login";
    public const string Settings = "settings";
    public const string Repository = "repository";
    public const string Board = "board";
    public const string Labels = "labels";

    // Every check; the results are in the order of the checks.
    public async Task<IReadOnlyList<CheckResult>> RunAsync(ICheckupView view, CancellationToken cancellationToken)
    {
        var results = new List<CheckResult>(await MachineAsync(machine, view, cancellationToken));
        bool Passed(string name) => results.Any(result => result.Name == name && result.State == CheckState.Passed);

        var loaded = settings.Load();
        view.Start(Settings);
        Tell(loaded switch
        {
            null => new CheckResult(Settings, CheckState.Failed, "This project is not set up yet.", Fixes.Setup),
            { Settings: null } => new CheckResult(Settings, CheckState.Failed, $"{settings.DisplayPath} does not validate:")
            {
                Details = [.. loaded.Errors.Select(error => error.Describe(settings.DisplayPath))],
            },
            _ => new CheckResult(Settings, CheckState.Passed, settings.DisplayPath),
        });

        if (!Passed(Git))
            Tell(Waits(Repository, Git));
        else if (!Passed(TrackerLogin))
            Tell(Waits(Repository, "the " + TrackerLogin));
        else if (origin is null)
            Tell(new CheckResult(Repository, CheckState.Failed, "The `origin` remote is not a GitHub repository."));
        else
        {
            view.Start(Repository);
            Tell(await machine.CanReadRepositoryAsync(origin, cancellationToken)
                ? new CheckResult(Repository, CheckState.Passed, origin)
                : new CheckResult(Repository, CheckState.Failed, $"{origin} cannot be read on GitHub: it does not exist, or you have no access to it."));
        }

        if (loaded?.Settings is not { } valid)
            Tell(Waits(Board, "the settings"));
        else if (valid.Tracker.Board is not { } board)
            Tell(new CheckResult(Board, CheckState.Skipped, "the project has no board"));
        else if (!Passed(TrackerLogin))
            Tell(Waits(Board, "the " + TrackerLogin));
        else
        {
            view.Start(Board);
            Tell(await machine.CheckBoardAsync(board, cancellationToken) switch
            {
                BoardAccess.Readable => new CheckResult(Board, CheckState.Passed, board),
                BoardAccess.LacksScope => new CheckResult(Board, CheckState.Failed, "Your GitHub login cannot work with projects.", Fixes.ProjectScope),
                BoardAccess.NoAnswer => new CheckResult(Board, CheckState.Failed, "GitHub did not answer, so the board could not be checked."),
                _ => new CheckResult(Board, CheckState.Failed, $"{board} cannot be read: it does not exist, or you have no access to it."),
            });
        }

        if (loaded?.Settings is not { } read)
            Tell(Waits(Labels, "the settings"));
        else if (!Passed(Repository))
            Tell(Waits(Labels, "the repository"));
        else
        {
            view.Start(Labels);
            Tell(await LabelsAsync(read.Queue.Labels, origin!, cancellationToken));
        }

        return results;

        void Tell(CheckResult result)
        {
            results.Add(result);
            view.Done(result);
        }
    }

    // The checks that need neither the settings nor the repository: the tools and their logins.
    public static async Task<IReadOnlyList<CheckResult>> MachineAsync(ICheckupMachine machine, ICheckupView view, CancellationToken cancellationToken)
    {
        var results = new List<CheckResult>();

        await ToolAsync(Git, Tool.Git, "`git` is not installed.");

        if (await ToolAsync(TrackerTool, Tool.GitHubCli, "The GitHub command-line tool `gh` is not installed."))
        {
            view.Start(TrackerLogin);
            Tell(await machine.TrackerLoggedInAsync(cancellationToken)
                ? new CheckResult(TrackerLogin, CheckState.Passed)
                : new CheckResult(TrackerLogin, CheckState.Failed, "`gh` is not logged in to github.com.", Fixes.TrackerLogin));
        }
        else
        {
            Tell(Waits(TrackerLogin, "the " + TrackerTool));
        }

        if (await ToolAsync(Assistant, Tool.ClaudeCode, "Claude Code is not installed: `claude` is not on the path."))
        {
            view.Start(AssistantLogin);
            Tell(await machine.AssistantLoggedInAsync(cancellationToken)
                ? new CheckResult(AssistantLogin, CheckState.Passed)
                : new CheckResult(AssistantLogin, CheckState.Failed, "Claude Code is not logged in.", Fixes.AssistantLogin));
        }
        else
        {
            Tell(Waits(AssistantLogin, Assistant));
        }

        return results;

        async Task<bool> ToolAsync(string name, Tool tool, string missing)
        {
            view.Start(name);
            var has = await machine.HasAsync(tool, cancellationToken);
            Tell(has
                ? new CheckResult(name, CheckState.Passed)
                : new CheckResult(name, CheckState.Failed, missing, Fixes.Install(
                    tool, machine.Platform, Fixes.AsksManager(tool, machine.Platform) && await machine.HasManagerAsync(cancellationToken))));
            return has;
        }

        void Tell(CheckResult result)
        {
            results.Add(result);
            view.Done(result);
        }
    }

    // Every label the settings name must be in the repository; GitHub tells labels apart whatever their case.
    async Task<CheckResult> LabelsAsync(LabelSettings named, string repository, CancellationToken cancellationToken)
    {
        if (await machine.ReadLabelsAsync(repository, cancellationToken) is not { } there)
            return new CheckResult(Labels, CheckState.Failed, $"The labels of {repository} cannot be read.");

        string[] wanted = [.. named.Blocking, .. named.Take, named.Owner, named.Interrupted];
        var missing = wanted
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Where(label => !there.Contains(label, StringComparer.OrdinalIgnoreCase))
            .ToList();
        return missing.Count == 0
            ? new CheckResult(Labels, CheckState.Passed)
            : new CheckResult(
                Labels,
                CheckState.Failed,
                $"{repository} has no {(missing.Count == 1 ? "label" : "labels")} {string.Join(", ", missing.Select(label => $"“{label}”"))}.",
                string.Join(" && ", missing.Select(label => $"gh label create \"{label}\" --repo {repository}")));
    }

    static CheckResult Waits(string name, string need) => new(name, CheckState.Skipped, $"waits for {need}");
}

// A view for checks that nobody watches.
public sealed class NoCheckupView : ICheckupView
{
    public void Start(string name)
    {
    }

    public void Done(CheckResult result)
    {
    }
}
