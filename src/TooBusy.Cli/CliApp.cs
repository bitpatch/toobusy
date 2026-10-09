using System.CommandLine;
using TooBusy.Cli.Imitation;
using TooBusy.Cli.Terminal;
using TooBusy.Core.Setup;
using TooBusy.Infrastructure.Settings;

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

    // Only the dry run exists so far: the real steps and the real terminal over an imitated machine and tracker.
    static Command CreateInitCommand(CliContext context)
    {
        var dryRun = new Option<bool>("--dry-run") { Description = "Go through the setup with an imitated tracker and write nothing." };
        var command = new Command("init", "Sets the project up: asks what it needs and writes the settings.");
        command.Options.Add(dryRun);
        command.SetAction(async (result, cancellationToken) =>
        {
            if (FindProject(context) is not { } root)
                return ExitCode.NotReady;
            if (!result.GetValue(dryRun))
                return Fail(context, "toobusy: a real setup is not implemented yet; use `--dry-run`");
            if (context.ReadKey is null)
                return Fail(context, "toobusy: `init` asks questions and needs a terminal");

            context.Output.WriteLine(context.OutputPalette.Muted("Dry run: the machine and the tracker are imitated, and nothing is written."));
            var imitated = new ImitatedSetup();
            var setup = new ProjectSetup(
                new TerminalDialog(context.Output, context.ReadKey, context.OutputPalette), imitated, imitated, new UnwrittenSettings(new SettingsFile(root)));
            var outcome = await setup.RunAsync(cancellationToken);
            if (outcome == SetupOutcome.Written)
                context.Output.WriteLine(context.OutputPalette.Muted($"Dry run: {SettingsFile.DisplayPath} is left as it was."));
            return outcome == SetupOutcome.Declined ? ExitCode.Failed : ExitCode.Done;
        });
        return command;
    }

    // Only the dry run exists so far, and it has nothing to imitate: it is the entry point that the run grows from.
    static Command CreateRunCommand(CliContext context)
    {
        var dryRun = new Option<bool>("--dry-run") { Description = "Imitate the run without changing anything." };
        var command = new Command("run", "Takes the tasks of the queue one after another.");
        command.Options.Add(dryRun);
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

            if (!result.GetValue(dryRun))
                return Fail(context, "toobusy: a real run is not implemented yet; use `--dry-run`");

            context.Output.WriteLine("Dry run: nothing is changed.");
            context.Output.WriteLine("There is nothing to imitate yet.");
            return ExitCode.Done;
        });
        return command;
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
