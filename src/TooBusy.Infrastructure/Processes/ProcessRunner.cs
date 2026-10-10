using System.ComponentModel;
using System.Diagnostics;
using TooBusy.Core.Processes;

namespace TooBusy.Infrastructure.Processes;

// Commands are started directly, without a shell, in the folder the runner is of, or where the tool was started.
// Their input is closed at once, so that none of them waits for an answer.
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
            UseShellExecute = false,
            CreateNoWindow = true,
            WorkingDirectory = folder ?? "",
        };
        foreach (var argument in arguments)
            process.StartInfo.ArgumentList.Add(argument);

        try
        {
            process.Start();
        }
        catch (Win32Exception)
        {
            return new ProcessResult(ProcessStatus.NotFound, 0, "", "");
        }

        using var limit = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        limit.CancelAfter(timeout);
        try
        {
            process.StandardInput.Close();
            var output = process.StandardOutput.ReadToEndAsync(limit.Token);
            var error = process.StandardError.ReadToEndAsync(limit.Token);
            await process.WaitForExitAsync(limit.Token);
            return new ProcessResult(ProcessStatus.Exited, process.ExitCode, await output, await error);
        }
        catch (OperationCanceledException)
        {
            process.Kill(entireProcessTree: true);
            cancellationToken.ThrowIfCancellationRequested();
            return new ProcessResult(ProcessStatus.TimedOut, 0, "", "");
        }
    }
}
