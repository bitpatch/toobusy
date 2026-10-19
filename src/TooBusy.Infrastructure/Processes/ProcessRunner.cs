using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using TooBusy.Core.Processes;

namespace TooBusy.Infrastructure.Processes;

// Commands are started directly, without a shell, in the folder the runner is of, or where the tool was started.
// Their input is closed at once and git's prompts are off, as `Unattended` has them, so that none of them waits for
// an answer. What they print is read as UTF-8, as git, gh and claude write it. A command that exits in its time is
// over then, with what it printed: what it left running with its output open, as a service it started in the
// background, is neither waited for nor stopped.
public sealed class ProcessRunner(string? folder = null) : IProcessRunner
{
    // How long what a command printed is read on after it exited: something it left running may keep its output open
    // for as long as it lives. What a command prints is read while it runs, so what is left in the pipes when it exits,
    // as the end of a long answer, is read well within it.
    static readonly TimeSpan Tail = TimeSpan.FromSeconds(1);

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

        var output = new StringBuilder();
        var error = new StringBuilder();
        if (await ExitsInTimeAsync(process, output, error, timeout, cancellationToken))
            return new ProcessResult(ProcessStatus.Exited, process.ExitCode, output.ToString(), error.ToString());
        return new ProcessResult(ProcessStatus.TimedOut, 0, "", "");
    }

    // Closes the input of a started command and waits for it to exit in its time, while what it prints to its output
    // and to where its errors go is read into the two texts as it comes, one and the same text where both are kept
    // together. After the exit the reads go on until the streams end, or for the Tail at most. A command that does not
    // exit in its time is stopped, and so is one that is cancelled, which throws. The reads are over before this ends,
    // so that nothing is added to the texts after it and nothing reads a stream of a process that is let go.
    internal static async Task<bool> ExitsInTimeAsync(Process process, StringBuilder output, StringBuilder error, TimeSpan time, CancellationToken cancellationToken)
    {
        using var reading = new CancellationTokenSource();
        var read = Task.WhenAll(ReadAsync(process.StandardOutput, output, reading.Token), ReadAsync(process.StandardError, error, reading.Token));
        using var limit = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        limit.CancelAfter(time);
        try
        {
            process.StandardInput.Close();
            await process.WaitForExitAsync(limit.Token);
            await Task.WhenAny(read, Task.Delay(Tail, CancellationToken.None));
            return true;
        }
        catch (OperationCanceledException)
        {
            Kill(process);
            cancellationToken.ThrowIfCancellationRequested();
            return false;
        }
        finally
        {
            await reading.CancelAsync();
            await read;
        }
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

    // Stops a command with everything it started. A command that ended by itself between the cancellation and the kill
    // has nothing left to stop, and what of its tree cannot be stopped is left: neither is a failure of the command.
    static void Kill(Process process)
    {
        try
        {
            process.Kill(entireProcessTree: true);
        }
        catch (Exception gone) when (gone is InvalidOperationException or Win32Exception or AggregateException)
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
