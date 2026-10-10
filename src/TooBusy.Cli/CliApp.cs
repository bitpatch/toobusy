using System.CommandLine;
using TooBusy.Cli.Terminal;
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
        root.Subcommands.Add(CreateRunCommand(context, demo));

        // Without a command the tool opens its page in a terminal: the setup when the project needs it, the choice
        // of the milestone when the user has none, and then the menu. Without a terminal there is nobody to ask,
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

            Session session;
            using (var page = OpenPage(context, terminal, root))
            {
                session = new Session(page, bench);
                try
                {
                    await session.ChooseMilestoneAsync(leavesPage: true, cancellationToken);
                }
                catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
                {
                }
            }

            context.Output.WriteLine($"toobusy · {context.Shorten(root)}");
            if (!ReportMilestone(context, session, bench.Demo))
                context.Output.WriteLine("The milestone is left as it was.");
            return ExitCode.Done;
        });
        return command;
    }

    // The tasks are not run yet: `run` is the entry point that the run grows from. It opens the page on the run,
    // and it does not start without a milestone to work on.
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
        ReportMilestone(context, session, bench.Demo);
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

    // The milestone that was chosen on the page; false when none was.
    static bool ReportMilestone(CliContext context, Session session, bool demo)
    {
        if (session.Chosen is not { } chosen)
            return false;

        context.Output.WriteLine($"{context.OutputPalette.Success("✔")} {"Milestone",-16} {chosen.Title ?? "none"}");
        if (demo)
            context.Output.WriteLine(context.OutputPalette.Muted("Demo: the choice of the milestone is not remembered."));
        return true;
    }

    static Page OpenPage(CliContext context, TerminalDevice terminal, string root) =>
        new(context.Output, context.OutputPalette, terminal, $"toobusy · {context.Shorten(root)}");

    // What the commands work with in the project. Outside a demo the choice of the user needs a place on the
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
