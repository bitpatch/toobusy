using System.CommandLine;
using TooBusy.Infrastructure.Settings;

namespace TooBusy.Cli;

public static class CliApp
{
    public static RootCommand CreateRootCommand(string folder)
    {
        var root = new RootCommand("Runs the tasks of your tracker one after another with AI coding assistants.");
        root.Subcommands.Add(CreateRunCommand(folder));

        // Without a command the tool has nothing to do yet, so in a project that is set up it shows its help.
        root.SetAction(result => FindSettings(folder, result) is null
            ? ExitCode.NotReady
            : root.Parse(["--help"]).Invoke(result.InvocationConfiguration));
        return root;
    }

    // The folder is where the tool is started: the project is looked for from there upwards.
    public static async Task<int> RunAsync(string[] args, string folder, TextWriter output, TextWriter error, CancellationToken cancellationToken = default)
    {
        var configuration = new InvocationConfiguration { Output = output, Error = error };
        var result = CreateRootCommand(folder).Parse(args);
        var exit = await result.InvokeAsync(configuration, cancellationToken);
        return result.Errors.Count > 0 ? ExitCode.NotReady : exit;
    }

    // Only the dry run exists so far, and it has nothing to imitate: it is the entry point that the run grows from.
    static Command CreateRunCommand(string folder)
    {
        var dryRun = new Option<bool>("--dry-run") { Description = "Imitate the run without changing anything." };
        var command = new Command("run", "Takes the tasks of the queue one after another.");
        command.Options.Add(dryRun);
        command.SetAction(result =>
        {
            if (FindSettings(folder, result) is not { } file)
                return ExitCode.NotReady;

            var settings = file.Load();
            if (settings.Settings is null)
            {
                result.InvocationConfiguration.Error.WriteLine("toobusy: the settings of this project are not valid.");
                foreach (var problem in settings.Errors)
                    result.InvocationConfiguration.Error.WriteLine(problem.Describe(SettingsFile.DisplayPath));
                return ExitCode.Failed;
            }

            if (!result.GetValue(dryRun))
            {
                result.InvocationConfiguration.Error.WriteLine("toobusy: a real run is not implemented yet; use --dry-run");
                return ExitCode.Failed;
            }

            result.InvocationConfiguration.Output.WriteLine("Dry run: nothing is changed.");
            result.InvocationConfiguration.Output.WriteLine("There is nothing to imitate yet.");
            return ExitCode.Done;
        });
        return command;
    }

    // The settings file of the project around the folder; without a project or without the file it says so and gives null.
    static SettingsFile? FindSettings(string folder, ParseResult result)
    {
        if (ProjectLocator.FindRoot(folder) is not { } root)
        {
            result.InvocationConfiguration.Error.WriteLine("toobusy: not inside a git repository");
            return null;
        }

        var file = new SettingsFile(root);
        if (file.Exists)
            return file;

        result.InvocationConfiguration.Error.WriteLine("toobusy: this project is not set up yet.");
        result.InvocationConfiguration.Error.WriteLine("Run `toobusy init` to set it up.");
        return null;
    }
}
