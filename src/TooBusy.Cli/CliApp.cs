using System.CommandLine;
using TooBusy.Cli.Imitation;
using TooBusy.Cli.Terminal;
using TooBusy.Core.Setup;
using TooBusy.Infrastructure.Git;
using TooBusy.Infrastructure.Settings;
using TooBusy.Trackers.GitHub;

namespace TooBusy.Cli;

public static class CliApp
{
    public static RootCommand CreateRootCommand(CliContext context)
    {
        var root = new RootCommand("Runs the tasks of your tracker one after another with AI coding assistants.");
        root.Subcommands.Add(CreateInitCommand(context));
        root.Subcommands.Add(CreateRunCommand(context));

        // Without a command the tool has nothing to do yet, so in a project that is set up it shows its help.
        root.SetAction(result => FindSettings(context) is null
            ? ExitCode.NotReady
            : root.Parse(["--help"]).Invoke(result.InvocationConfiguration));
        return root;
    }

    public static async Task<int> RunAsync(string[] args, CliContext context, CancellationToken cancellationToken = default)
    {
        var configuration = new InvocationConfiguration { Output = context.Output, Error = context.Error };
        var result = CreateRootCommand(context).Parse(args);
        var exit = await result.InvokeAsync(configuration, cancellationToken);
        return result.Errors.Count > 0 ? ExitCode.NotReady : exit;
    }

    // Only the demo exists so far: the real steps and the real screen over an imitated machine and tracker.
    // What it reads for the question about the project is real, the `origin` remote and the boards of the user:
    // reading them changes nothing. What the setup would make, link and write is imitated.
    static Command CreateInitCommand(CliContext context)
    {
        var demo = new Option<bool>("--demo") { Description = "Go through the setup with an imitated tracker and write nothing." };
        var command = new Command("init", "Sets the project up: asks what it needs and writes the settings.");
        command.Options.Add(demo);
        command.SetAction(async (result, cancellationToken) =>
        {
            if (FindProject(context) is not { } root)
                return ExitCode.NotReady;
            if (!result.GetValue(demo))
                return Fail(context, "toobusy: a real setup is not implemented yet; use `--demo`");
            if (context.Terminal is not { } terminal)
                return Fail(context, "toobusy: `init` asks questions and needs a terminal");

            var origin = context.Processes is null ? "example/project" : await new GitOrigin(context.Processes).ReadAsync(root, cancellationToken);
            var imitated = new ImitatedSetup(origin);
            var boards = new ImitatedBoards(context.Processes is null ? null : new GitHubBoards(context.Processes));

            SetupResult? ended = null;
            using (var page = OpenPage(context, terminal, root, "Setting up this project · demo"))
            {
                try
                {
                    ended = await new ProjectSetup(new SetupScreen(page), imitated, imitated, boards, new UnwrittenSettings(new SettingsFile(root))).RunAsync(cancellationToken);
                }
                catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
                {
                }
            }

            // The screen is gone; what was answered and how it ended is left in the terminal.
            var palette = context.OutputPalette;
            context.Output.WriteLine($"toobusy · Setting up this project · {context.Shorten(root)}");
            foreach (var answer in ended?.Answers ?? [])
                context.Output.WriteLine($"{palette.Success("✔")} {answer.Label,-16} {answer.Value}");
            switch (ended?.Outcome)
            {
                case SetupOutcome.Written:
                    context.Output.WriteLine(palette.Muted($"Demo: nothing was made, linked or written; {SettingsFile.DisplayPath} is left as it was."));
                    return ExitCode.Done;
                case SetupOutcome.NothingToChange:
                    context.Output.WriteLine("Nothing to change: the settings already say this.");
                    return ExitCode.Done;
                case SetupOutcome.Declined:
                    context.Output.WriteLine($"{palette.Error("✘")} The setup was declined: nothing was changed.");
                    return ExitCode.Failed;
                default:
                    context.Output.WriteLine($"{palette.Error("✘")} The setup was left: nothing was changed.");
                    return ExitCode.Failed;
            }
        });
        return command;
    }

    // Only the demo exists so far, and it has nothing to imitate: it is the entry point that the run grows from.
    // In a terminal it is the page of a run with its commands; elsewhere it says the same in two lines.
    static Command CreateRunCommand(CliContext context)
    {
        var demo = new Option<bool>("--demo") { Description = "Imitate the run without changing anything." };
        var command = new Command("run", "Takes the tasks of the queue one after another.");
        command.Options.Add(demo);
        command.SetAction(result =>
        {
            if (FindSettings(context) is not { } file)
                return ExitCode.NotReady;

            var settings = file.Load();
            if (settings is { Settings: null })
            {
                Fail(context, "toobusy: the settings of this project are not valid.");
                foreach (var problem in settings.Errors)
                    context.Error.WriteLine(context.ErrorPalette.Error(problem.Describe(SettingsFile.DisplayPath)));
                return ExitCode.Failed;
            }

            if (!result.GetValue(demo))
                return Fail(context, "toobusy: a real run is not implemented yet; use `--demo`");

            if (context.Terminal is { } terminal)
            {
                var root = ProjectLocator.FindRoot(context.Folder)!;
                using (var page = OpenPage(context, terminal, root, "Running the queue · demo"))
                {
                    try
                    {
                        new RunScreen(page).Run();
                    }
                    catch (OperationCanceledException)
                    {
                    }
                }

                context.Output.WriteLine($"toobusy · Running the queue · {context.Shorten(root)}");
            }

            context.Output.WriteLine("Demo: nothing is changed.");
            context.Output.WriteLine("There is nothing to imitate yet.");
            return ExitCode.Done;
        });
        return command;
    }

    static Page OpenPage(CliContext context, TerminalDevice terminal, string root, string status) =>
        new(context.Output, context.OutputPalette, terminal, $"toobusy · {context.Shorten(root)}") { Status = status };

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
