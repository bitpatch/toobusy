using System.ComponentModel;
using System.Diagnostics;
using System.Text;
using TooBusy.Core.Run;

namespace TooBusy.Infrastructure.Processes;

// The commands of a project, run through the shell of the machine in a folder, `sh -c`.
// The input of a command is closed at once and git's prompts are off, as `Unattended` has them, so that it does not
// wait for an answer. What it prints to its output and to where its errors go is kept as one text, as it comes, read
// as UTF-8. It is waited for as `ProcessRunner` waits for a command: one that exits is over, whatever it left running,
// as a server it started.
public sealed class ShellCommands(string folder) : IProjectCommands
{
    public async Task<CommandEnd> RunAsync(string command, IReadOnlyDictionary<string, string> environment, TimeSpan time, CancellationToken cancellationToken)
    {
        using var process = new Process();
        process.StartInfo = Shell(command);
        process.StartInfo.RedirectStandardInput = true;
        process.StartInfo.RedirectStandardOutput = true;
        process.StartInfo.RedirectStandardError = true;
        process.StartInfo.StandardOutputEncoding = Encoding.UTF8;
        process.StartInfo.StandardErrorEncoding = Encoding.UTF8;
        process.StartInfo.UseShellExecute = false;
        process.StartInfo.CreateNoWindow = true;
        process.StartInfo.WorkingDirectory = folder;
        Unattended.Apply(process.StartInfo);
        foreach (var (name, value) in environment)
            process.StartInfo.Environment[name] = value;

        try
        {
            process.Start();
        }
        catch (Win32Exception)
        {
            return new CommandEnd(CommandStatus.NotStarted, 0, "");
        }

        var printed = new StringBuilder();
        if (await ProcessRunner.ExitsInTimeAsync(process, printed, printed, time, cancellationToken))
            return new CommandEnd(CommandStatus.Exited, process.ExitCode, printed.ToString());
        return new CommandEnd(CommandStatus.TimedOut, 0, printed.ToString());
    }

    static ProcessStartInfo Shell(string command)
    {
        var shell = new ProcessStartInfo("sh");
        shell.ArgumentList.Add("-c");
        shell.ArgumentList.Add(command);
        return shell;
    }
}
