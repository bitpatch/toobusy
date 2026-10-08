using System.CommandLine;

namespace TooBusy.Cli;

public static class CliApp
{
    public static RootCommand CreateRootCommand() =>
        new("Runs the tasks of your tracker one after another with AI coding assistants.");

    // Without arguments the tool has nothing to do yet, so it shows its help.
    public static Task<int> RunAsync(string[] args, TextWriter output, TextWriter error, CancellationToken cancellationToken = default)
    {
        var configuration = new InvocationConfiguration { Output = output, Error = error };
        return CreateRootCommand().Parse(args is [] ? ["--help"] : args).InvokeAsync(configuration, cancellationToken);
    }
}
