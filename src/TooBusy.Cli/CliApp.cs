using System.CommandLine;

namespace TooBusy.Cli;

public static class CliApp
{
    public static RootCommand CreateRootCommand()
    {
        var root = new RootCommand("Runs the tasks of your tracker one after another with AI coding assistants.");
        root.Subcommands.Add(CreateRunCommand());
        return root;
    }

    // Without arguments the tool has nothing to do yet, so it shows its help.
    public static Task<int> RunAsync(string[] args, TextWriter output, TextWriter error, CancellationToken cancellationToken = default)
    {
        var configuration = new InvocationConfiguration { Output = output, Error = error };
        return CreateRootCommand().Parse(args is [] ? ["--help"] : args).InvokeAsync(configuration, cancellationToken);
    }

    // Only the dry run exists so far, and it has nothing to imitate: it is the entry point that the run grows from.
    static Command CreateRunCommand()
    {
        var dryRun = new Option<bool>("--dry-run") { Description = "Imitate the run without changing anything." };
        var command = new Command("run", "Takes the tasks of the queue one after another.");
        command.Options.Add(dryRun);
        command.SetAction(result =>
        {
            if (!result.GetValue(dryRun))
            {
                result.InvocationConfiguration.Error.WriteLine("toobusy: a real run is not implemented yet; use --dry-run");
                return 1;
            }

            result.InvocationConfiguration.Output.WriteLine("Dry run: nothing is changed.");
            result.InvocationConfiguration.Output.WriteLine("There is nothing to imitate yet.");
            return 0;
        });
        return command;
    }
}
