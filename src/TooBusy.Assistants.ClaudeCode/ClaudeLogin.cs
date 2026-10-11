using System.Text.Json;
using TooBusy.Core.Processes;

namespace TooBusy.Assistants.ClaudeCode;

// Whether Claude Code is logged in, asked without starting a session: `claude auth status` answers with JSON that
// has `loggedIn`, and exits with 0 only when it is.
public sealed class ClaudeLogin(IProcessRunner processes)
{
    static readonly TimeSpan Patience = TimeSpan.FromSeconds(10);

    public async Task<bool> CheckAsync(CancellationToken cancellationToken)
    {
        var result = await processes.RunAsync("claude", ["auth", "status"], Patience, cancellationToken);
        return result is { Status: ProcessStatus.Exited, ExitCode: 0 } && !SaysLoggedOut(result.Output);
    }

    static bool SaysLoggedOut(string answer)
    {
        try
        {
            using var document = JsonDocument.Parse(answer);
            return document.RootElement.ValueKind == JsonValueKind.Object
                && document.RootElement.TryGetProperty("loggedIn", out var logged)
                && logged.ValueKind == JsonValueKind.False;
        }
        catch (JsonException)
        {
            return false;
        }
    }
}
