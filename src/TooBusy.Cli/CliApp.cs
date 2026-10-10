using System.CommandLine;
using TooBusy.Cli.Terminal;
using TooBusy.Core.Assistant;
using TooBusy.Core.Queue;
using TooBusy.Core.Setup;
using TooBusy.Infrastructure.Settings;

namespace TooBusy.Cli;

public static class CliApp
{
    public static RootCommand CreateRootCommand(CliContext context)
    {
        var demo = new Option<bool>("--demo")
        {
            Description = "Show the tool over made-up data: nothing is made, linked, written or remembered.",
            Recursive = true,
        };
        var root = new RootCommand("Runs the tasks of your tracker one after another with AI coding assistants.");
        root.Options.Add(demo);
        root.Subcommands.Add(CreateInitCommand(context, demo));
        root.Subcommands.Add(CreateMilestoneCommand(context, demo));
        root.Subcommands.Add(CreateModelCommand(context, demo));
        root.Subcommands.Add(CreateEffortCommand(context, demo));
        root.Subcommands.Add(CreateRunCommand(context, demo));

        // Without a command the tool opens its page in a terminal: the setup when the project needs it, the choices
        // of the milestone, the model and the effort when the user has none, and then the menu. Without a terminal there is nobody to ask,
        // so in a project that is set up it shows its help.
        root.SetAction(async (result, cancellationToken) =>
        {
            if (context.Terminal is not { } terminal)
                return FindSettings(context) is null ? ExitCode.NotReady : root.Parse(["--help"]).Invoke(result.InvocationConfiguration);
            if (FindProject(context) is not { } folder || await OpenAsync(context, folder, result.GetValue(demo), ready: true, cancellationToken) is not { } bench)
                return ExitCode.NotReady;

            return await HomeAsync(context, terminal, bench, run: false, cancellationToken);
        });
        return root;
    }

    public static async Task<int> RunAsync(string[] args, CliContext context, CancellationToken cancellationToken = default)
    {
        var configuration = new InvocationConfiguration { Output = context.Output, Error = context.Error };
        var result = CreateRootCommand(context).Parse(args);
        var exit = await result.InvokeAsync(configuration, cancellationToken);
        return result.Errors.Count > 0 ? ExitCode.NotReady : exit;
    }

    static Command CreateInitCommand(CliContext context, Option<bool> demo)
    {
        var command = new Command("init", "Sets the project up: asks what it needs and writes the settings.");
        command.SetAction(async (result, cancellationToken) =>
        {
            if (FindProject(context) is not { } root)
                return ExitCode.NotReady;
            if (context.Terminal is not { } terminal)
                return Fail(context, "toobusy: `init` asks questions and needs a terminal");
            if (await OpenAsync(context, root, result.GetValue(demo), ready: false, cancellationToken) is not { } bench)
                return ExitCode.NotReady;

            Session session;
            using (var page = OpenPage(context, terminal, root))
            {
                session = new Session(page, bench);
                try
                {
                    await session.SetupAsync(leavesPage: true, cancellationToken);
                }
                catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
                {
                }
            }

            // The screen is gone; what was answered and how it ended is left in the terminal.
            context.Output.WriteLine($"toobusy · Setting up this project · {context.Shorten(root)}");
            return ReportSetup(context, session.Setup, bench.Demo) ? ExitCode.Failed : ExitCode.Done;
        });
        return command;
    }

    // The milestone to work on is the choice of the user: it is asked for on a page, or given here for a script.
    static Command CreateMilestoneCommand(CliContext context, Option<bool> demo)
    {
        var title = new Argument<string?>("title") { Description = "The title of the open milestone to work on.", Arity = ArgumentArity.ZeroOrOne };
        var none = new Option<bool>("--none") { Description = "Work without a milestone: tasks are taken whatever their milestone." };
        var command = new Command("milestone", "Chooses the milestone to work on, or shows the one that is chosen.");
        command.Arguments.Add(title);
        command.Options.Add(none);
        command.SetAction(async (result, cancellationToken) =>
        {
            var named = result.GetValue(title);
            if (named is not null && result.GetValue(none))
            {
                Fail(context, "toobusy: give the title of a milestone or `--none`, not both");
                return ExitCode.NotReady;
            }

            if (FindProject(context) is not { } root || await OpenAsync(context, root, result.GetValue(demo), ready: false, cancellationToken) is not { } bench)
                return ExitCode.NotReady;

            if (result.GetValue(none))
            {
                bench.Personal.SaveMilestone(MilestoneChoice.None);
                context.Output.WriteLine("Milestone: none. Tasks are taken whatever their milestone.");
                return ExitCode.Done;
            }

            if (named is not null)
            {
                if (await bench.ReadMilestonesAsync(cancellationToken) is not { } open)
                {
                    return Fail(context, bench.Repository is null
                        ? "toobusy: the `origin` remote is not a GitHub repository, so there are no milestones to choose from"
                        : $"toobusy: the milestones of {bench.Repository} cannot be read");
                }

                if (MilestoneStanding.Find(open, named) is not { } found)
                {
                    Fail(context, $"toobusy: there is no open milestone “{named}”.");
                    return Fail(context, open.Count == 0
                        ? "The repository has no open milestones."
                        : "The open ones: " + string.Join(", ", MilestoneOrder.Sorted(open).Select(milestone => milestone.Title)));
                }

                bench.Personal.SaveMilestone(new MilestoneChoice(found.Title));
                context.Output.WriteLine($"Milestone: {found.Title} · {MilestoneScreen.Describe(found)}");
                return ExitCode.Done;
            }

            if (context.Terminal is not { } terminal)
            {
                var standing = await bench.StandAsync(cancellationToken);
                context.Output.WriteLine($"Milestone: {standing.Name}{(standing.State == MilestoneState.Gone ? " (not open any more)" : "")}");
                if (!standing.Ready)
                    context.Output.WriteLine("Choose it with `toobusy milestone <title>` or `toobusy milestone --none`.");
                return ExitCode.Done;
            }

            return await ChooseOnPageAsync(context, terminal, bench, session => session.ChooseMilestoneAsync(leavesPage: true, cancellationToken), "The milestone is left as it was.", cancellationToken);
        });
        return command;
    }

    // The model the tasks are done with by default is the choice of the user too.
    static Command CreateModelCommand(CliContext context, Option<bool> demo)
    {
        var name = new Argument<string?>("name") { Description = "The name of the model, as the assistant takes it.", Arity = ArgumentArity.ZeroOrOne };
        var own = new Option<bool>("--default") { Description = "Name no model: the assistant takes its own default." };
        var command = new Command("model", "Chooses the model the tasks are done with by default, or shows the one that is chosen.");
        command.Arguments.Add(name);
        command.Options.Add(own);
        command.SetAction(async (result, cancellationToken) =>
        {
            var named = result.GetValue(name);
            if (named is not null && result.GetValue(own))
            {
                Fail(context, "toobusy: give the name of a model or `--default`, not both");
                return ExitCode.NotReady;
            }

            if (FindProject(context) is not { } root || await OpenAsync(context, root, result.GetValue(demo), ready: false, cancellationToken) is not { } bench)
                return ExitCode.NotReady;

            if (result.GetValue(own))
            {
                bench.Personal.SaveModel(ModelChoice.AssistantsOwn);
                context.Output.WriteLine("Model: the assistant's own.");
                return ExitCode.Done;
            }

            if (named is not null)
            {
                if (named.Trim().Length == 0)
                    return Fail(context, "toobusy: the name of the model is empty");

                bench.Personal.SaveModel(new ModelChoice(named.Trim()));
                context.Output.WriteLine($"Model: {named.Trim()}");
                return ExitCode.Done;
            }

            if (context.Terminal is not { } terminal)
            {
                var chosen = bench.Personal.LoadModel();
                context.Output.WriteLine($"Model: {ModelScreen.Describe(chosen)}");
                if (chosen is null)
                    context.Output.WriteLine("Choose it with `toobusy model <name>` or `toobusy model --default`.");
                return ExitCode.Done;
            }

            return await ChooseOnPageAsync(context, terminal, bench, session => Task.FromResult(session.ChooseModel(leavesPage: true)), "The model is left as it was.", cancellationToken);
        });
        return command;
    }

    // And so is the effort.
    static Command CreateEffortCommand(CliContext context, Option<bool> demo)
    {
        var levels = string.Join(", ", ClaudeCodeOptions.Efforts);
        var level = new Argument<string?>("level") { Description = $"The level of effort: {levels}.", Arity = ArgumentArity.ZeroOrOne };
        var command = new Command("effort", "Chooses the effort the tasks are done with by default, or shows the one that is chosen.");
        command.Arguments.Add(level);
        command.SetAction(async (result, cancellationToken) =>
        {
            if (FindProject(context) is not { } root || await OpenAsync(context, root, result.GetValue(demo), ready: false, cancellationToken) is not { } bench)
                return ExitCode.NotReady;

            if (result.GetValue(level) is { } named)
            {
                if (ClaudeCodeOptions.FindEffort(named) is not { } found)
                {
                    Fail(context, $"toobusy: there is no effort “{named}”.");
                    return Fail(context, $"The levels: {levels}");
                }

                bench.Personal.SaveEffort(found);
                context.Output.WriteLine($"Effort: {found}");
                return ExitCode.Done;
            }

            if (context.Terminal is not { } terminal)
            {
                var chosen = bench.Personal.LoadEffort();
                context.Output.WriteLine($"Effort: {chosen ?? "not chosen"}");
                if (chosen is null)
                    context.Output.WriteLine($"Choose it with `toobusy effort <level>`: {levels}.");
                return ExitCode.Done;
            }

            return await ChooseOnPageAsync(context, terminal, bench, session => Task.FromResult(session.ChooseEffort(leavesPage: true)), "The effort is left as it was.", cancellationToken);
        });
        return command;
    }

    // A page that is opened for one question, and what is left in the terminal when it is closed: what was chosen,
    // or that nothing was.
    static async Task<int> ChooseOnPageAsync(CliContext context, TerminalDevice terminal, Workbench bench, Func<Session, Task<bool>> ask, string left, CancellationToken cancellationToken)
    {
        Session session;
        using (var page = OpenPage(context, terminal, bench.Root))
        {
            session = new Session(page, bench);
            try
            {
                await ask(session);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
            }
        }

        context.Output.WriteLine($"toobusy · {context.Shorten(bench.Root)}");
        if (!ReportChoices(context, session, bench.Demo))
            context.Output.WriteLine(left);
        return ExitCode.Done;
    }

    // The tasks are not run yet: `run` is the entry point that the run grows from. It opens the page on the run,
    // and it does not start without a milestone to work on, a model and an effort.
    static Command CreateRunCommand(CliContext context, Option<bool> demo)
    {
        var command = new Command("run", "Takes the tasks of the queue one after another.");
        command.SetAction(async (result, cancellationToken) =>
        {
            // A demo shows the run in any project: where there are no settings it makes them up.
            if ((result.GetValue(demo) ? FindProject(context) : FindSettings(context)?.Path) is null)
                return ExitCode.NotReady;

            var root = ProjectLocator.FindRoot(context.Folder)!;
            if (await OpenAsync(context, root, result.GetValue(demo), ready: true, cancellationToken) is not { } bench)
                return ExitCode.NotReady;

            var settings = bench.Settings.Load();
            if (settings is { Settings: null })
            {
                Fail(context, "toobusy: the settings of this project are not valid.");
                foreach (var problem in settings.Errors)
                    context.Error.WriteLine(context.ErrorPalette.Error(problem.Describe(SettingsFile.DisplayPath)));
                return ExitCode.Failed;
            }

            var standing = await bench.StandAsync(cancellationToken);
            if (!standing.Ready)
            {
                Fail(context, standing.State == MilestoneState.Gone
                    ? $"toobusy: the milestone “{standing.Name}” is not open any more."
                    : "toobusy: the milestone to work on is not chosen.");
                return Fail(context, "Run `toobusy milestone` to choose it.");
            }

            if (bench.Personal.LoadModel() is null)
            {
                Fail(context, "toobusy: the model is not chosen.");
                return Fail(context, "Run `toobusy model` to choose it.");
            }

            if (bench.Personal.LoadEffort() is null)
            {
                Fail(context, "toobusy: the effort is not chosen.");
                return Fail(context, "Run `toobusy effort` to choose it.");
            }

            if (context.Terminal is { } terminal)
                return await HomeAsync(context, terminal, bench, run: true, cancellationToken);
            if (!bench.Demo)
                return Fail(context, "toobusy: running the tasks is not built yet");

            context.Output.WriteLine("Demo: nothing is changed.");
            context.Output.WriteLine("Running the tasks is not built yet.");
            return ExitCode.Done;
        });
        return command;
    }

    // The page of toobusy, from the menu or from the run, and what is left in the terminal when it is closed.
    static async Task<int> HomeAsync(CliContext context, TerminalDevice terminal, Workbench bench, bool run, CancellationToken cancellationToken)
    {
        Session session;
        using (var page = OpenPage(context, terminal, bench.Root))
        {
            session = new Session(page, bench);
            try
            {
                await session.HomeAsync(run, cancellationToken);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
            }
        }

        context.Output.WriteLine($"toobusy · {context.Shorten(bench.Root)}");
        var failed = session.Setup is not null && ReportSetup(context, session.Setup, bench.Demo);
        ReportChoices(context, session, bench.Demo);
        return failed && session.Setup!.Outcome == SetupOutcome.Failed ? ExitCode.Failed : ExitCode.Done;
    }

    // The answers of a setup and how it ended; a setup that never ended was left. Tells whether it failed.
    static bool ReportSetup(CliContext context, SetupResult? setup, bool demo)
    {
        var palette = context.OutputPalette;
        setup ??= new SetupResult(SetupOutcome.Left, []);
        foreach (var answer in setup.Answers)
            context.Output.WriteLine($"{palette.Success("✔")} {answer.Label,-16} {answer.Value}");

        var (failed, text) = Session.Ending(setup, demo);
        context.Output.WriteLine(
            failed ? $"{palette.Error("✘")} {text}"
            : setup.Outcome == SetupOutcome.NothingToChange ? text
            : demo ? palette.Muted(text)
            : $"{palette.Success("✔")} {text}");
        return failed;
    }

    // What was chosen on the page: the milestone, the model, the effort; false when nothing was.
    static bool ReportChoices(CliContext context, Session session, bool demo)
    {
        (string Label, string? Value)[] choices =
        [
            ("Milestone", session.Chosen is { } milestone ? milestone.Title ?? "none" : null),
            ("Model", session.ChosenModel is { } model ? ModelScreen.Describe(model) : null),
            ("Effort", session.ChosenEffort),
        ];
        var chosen = choices.Where(choice => choice.Value is not null).ToList();
        foreach (var (label, value) in chosen)
            context.Output.WriteLine($"{context.OutputPalette.Success("✔")} {label,-16} {value}");
        if (demo && chosen.Count > 0)
            context.Output.WriteLine(context.OutputPalette.Muted("Demo: what was chosen is not remembered."));
        return chosen.Count > 0;
    }

    static Page OpenPage(CliContext context, TerminalDevice terminal, string root) =>
        new(context.Output, context.OutputPalette, terminal, $"toobusy · {context.Shorten(root)}");

    // What the commands work with in the project. Outside a demo the choices of the user need a place on the
    // machine; without one it says so and gives null.
    static async Task<Workbench?> OpenAsync(CliContext context, string root, bool demo, bool ready, CancellationToken cancellationToken)
    {
        if (!demo && context.PersonalFolder is null)
        {
            Fail(context, "toobusy: there is no home folder to keep your choices in");
            return null;
        }

        return await Workbench.OpenAsync(root, demo, context.Processes, context.PersonalFolder ?? "", ready, cancellationToken);
    }

    // The root of the project around the folder; without a project it says so and gives null.
    static string? FindProject(CliContext context)
    {
        var root = ProjectLocator.FindRoot(context.Folder);
        if (root is null)
            Fail(context, "toobusy: not inside a git repository");
        return root;
    }

    // The settings file of the project; without a project or without the file it says so and gives null.
    static SettingsFile? FindSettings(CliContext context)
    {
        if (FindProject(context) is not { } root)
            return null;

        var file = new SettingsFile(root);
        if (file.Exists)
            return file;

        Fail(context, "toobusy: this project is not set up yet.");
        Fail(context, "Run `toobusy init` to set it up.");
        return null;
    }

    static int Fail(CliContext context, string message)
    {
        context.Error.WriteLine(context.ErrorPalette.Message(message));
        return ExitCode.Failed;
    }
}
