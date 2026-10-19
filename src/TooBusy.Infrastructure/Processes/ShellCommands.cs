using System.ComponentModel;
using System.Diagnostics;
using System.Text;
using TooBusy.Core.Run;

namespace TooBusy.Infrastructure.Processes;

// The commands of a project, run through the shell of the machine in a folder, `sh -c`.
// The input of a command is closed at once and git's prompts are off, as `Unattended` has them, so that it does not
// wait for an answer. What it prints to its output and to where its errors go is kept as one text, as it comes, read
// as UTF-8.
public sealed class ShellCommands(string folder) : IProjectCommands
{
    // How long what a command printed is read on after it exited: something it left behind, as a server it started,
    // may keep its output open for as long as it lives.
    static readonly TimeSpan Tail = TimeSpan.FromSeconds(1);

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
        using var reading = new CancellationTokenSource();
        var read = Task.WhenAll(ReadAsync(process.StandardOutput, printed, reading.Token), ReadAsync(process.StandardError, printed, reading.Token));
        using var limit = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        limit.CancelAfter(time);
        try
        {
            process.StandardInput.Close();
            await process.WaitForExitAsync(limit.Token);
            await Task.WhenAny(read, Task.Delay(Tail, CancellationToken.None));
            return new CommandEnd(CommandStatus.Exited, process.ExitCode, Text(printed));
        }
        catch (OperationCanceledException)
        {
            ProcessRunner.Kill(process);
            cancellationToken.ThrowIfCancellationRequested();
            return new CommandEnd(CommandStatus.TimedOut, 0, Text(printed));
        }
        finally
        {
            await reading.CancelAsync();
            await read;
        }
    }

    static ProcessStartInfo Shell(string command)
    {
        var shell = new ProcessStartInfo("sh");
        shell.ArgumentList.Add("-c");
        shell.ArgumentList.Add(command);
        return shell;
    }

    static string Text(StringBuilder printed)
    {
        lock (printed)
            return printed.ToString();
    }

    // Reads until the stream ends or the reading is given up; what was read by then is kept.
    static async Task ReadAsync(StreamReader stream, StringBuilder printed, CancellationToken cancellationToken)
    {
        var buffer = new char[4096];
        try
        {
            while (await stream.ReadAsync(buffer, cancellationToken) is > 0 and var count)
            {
                lock (printed)
                    printed.Append(buffer, 0, count);
            }
        }
        catch (Exception gone) when (gone is OperationCanceledException or IOException or ObjectDisposedException)
        {
        }
    }
}
