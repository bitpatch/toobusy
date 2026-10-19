using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using TooBusy.Core.Processes;

namespace TooBusy.Infrastructure.Processes;

// Commands are started directly, without a shell, in the folder the runner is of, or where the tool was started.
// Their input is closed at once and git's prompts are off, as `Unattended` has them, so that none of them waits for
// an answer. What they print is read as UTF-8, as git, gh and claude write it.
public sealed class ProcessRunner(string? folder = null) : IProcessRunner
{
    public IProcessRunner Inside(string folder) => new ProcessRunner(folder);

    public async Task<ProcessResult> RunAsync(string command, IReadOnlyList<string> arguments, TimeSpan timeout, CancellationToken cancellationToken)
    {
        using var process = new Process();
        process.StartInfo = new ProcessStartInfo(command)
        {
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
            UseShellExecute = false,
            CreateNoWindow = true,
            WorkingDirectory = folder ?? "",
        };
        foreach (var argument in arguments)
            process.StartInfo.ArgumentList.Add(argument);
        Unattended.Apply(process.StartInfo);

        try
        {
            process.Start();
        }
        catch (Win32Exception refused)
        {
            return Unstarted(refused);
        }

        using var limit = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        limit.CancelAfter(timeout);
        process.StandardInput.Close();
        var output = process.StandardOutput.ReadToEndAsync(limit.Token);
        var error = process.StandardError.ReadToEndAsync(limit.Token);
        try
        {
            await process.WaitForExitAsync(limit.Token);
            return new ProcessResult(ProcessStatus.Exited, process.ExitCode, await output, await error);
        }
        catch (OperationCanceledException)
        {
            // The reads are given up with the command, and are over before the process and its streams are let go,
            // so that nothing reads a stream that is gone.
            Kill(process);
            await Over(Task.WhenAll(output, error));
            cancellationToken.ThrowIfCancellationRequested();
            return new ProcessResult(ProcessStatus.TimedOut, 0, "", "");
        }
    }

    // Stops a command with everything it started. A command that ended by itself between the cancellation and the kill
    // has nothing left to stop, and what of its tree cannot be stopped is left: neither is a failure of the command.
    internal static void Kill(Process process)
    {
        try
        {
            process.Kill(entireProcessTree: true);
        }
        catch (Exception gone) when (gone is InvalidOperationException or Win32Exception or AggregateException)
        {
        }
    }

    // Waits for reads that were given up, whatever they ended with.
    static async Task Over(Task reads)
    {
        try
        {
            await reads;
        }
        catch (Exception given) when (given is OperationCanceledException or IOException or ObjectDisposedException)
        {
        }
    }

    // The system says alike that there is no such file of a command that is not on the path and of one whose folder
    // is not there any more. Every other refusal, as of a file that is not executable or of arguments that are too
    // long, is told in the system's words.
    ProcessResult Unstarted(Win32Exception refused)
    {
        if (folder is { Length: > 0 } && !Directory.Exists(folder))
            return new ProcessResult(ProcessStatus.NotStarted, 0, "", $"the folder {folder} is not there");
        if (refused.NativeErrorCode == NoSuchFile)
            return new ProcessResult(ProcessStatus.NotFound, 0, "", "");

        var said = refused.NativeErrorCode == 0 ? refused.Message : Marshal.GetPInvokeErrorMessage(refused.NativeErrorCode);
        return new ProcessResult(ProcessStatus.NotStarted, 0, "", said.TrimEnd('.'));
    }

    // ENOENT.
    const int NoSuchFile = 2;
}
