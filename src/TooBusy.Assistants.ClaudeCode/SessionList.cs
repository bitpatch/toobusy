using System.Text.Json;
using TooBusy.Core.Processes;
using TooBusy.Core.Run;

namespace TooBusy.Assistants.ClaudeCode;

// A session of the list. Alive tells that a process still has it; Name is what its start called it, and Started when it
// was started, in Unix milliseconds.
sealed record Listed(string Id, string? Conversation, string? State, string? WaitingFor, bool Alive, string? Name, long? Started);

// The sessions Claude Code knows, `claude agents --json --all`, which are those of the whole machine whatever folder it
// is asked in. It is a command that starts Claude Code, so a look at a session takes the last list that was read, while
// it is younger than `age`, twenty seconds unless a test says otherwise. Those who work with one account share this,
// and one list is read at a time: whoever comes while it is read gets it.
public sealed class SessionList(IProcessRunner processes, IClock clock, TimeSpan? age = null)
{
    // How long a command of Claude Code is waited for.
    internal static readonly TimeSpan Patience = TimeSpan.FromMinutes(2);

    readonly Turns reading = new();
    readonly TimeSpan age = age ?? TimeSpan.FromSeconds(20);

    // The last list that was read, and when; null when there is none to go by.
    List<Listed>? cached;
    DateTimeOffset cachedAt;

    // The list is not gone by from here on: something changed a session.
    internal void Forget() => cached = null;

    // The list of a look: the last one that was read, while it is young enough, and a new one otherwise.
    internal Task<List<Listed>?> RecentAsync(CancellationToken cancellationToken) =>
        reading.TakeAsync(async () => cached is { } recent && clock.Now - cachedAt < age ? recent : await ListAsync(cancellationToken), cancellationToken);

    // The sessions by what is asked of them, read anew; null when the list cannot be read, and such a list is not kept.
    internal Task<List<Listed>?> ReadAsync(CancellationToken cancellationToken) => reading.TakeAsync(() => ListAsync(cancellationToken), cancellationToken);

    async Task<List<Listed>?> ListAsync(CancellationToken cancellationToken)
    {
        var listed = await ReadListAsync(cancellationToken);
        (cached, cachedAt) = (listed, clock.Now);
        return listed;
    }

    async Task<List<Listed>?> ReadListAsync(CancellationToken cancellationToken)
    {
        var result = await processes.RunAsync("claude", ["agents", "--json", "--all"], Patience, cancellationToken);
        if (result is not { Status: ProcessStatus.Exited, ExitCode: 0 })
            return null;

        try
        {
            using var document = JsonDocument.Parse(result.Output);
            if (document.RootElement.ValueKind != JsonValueKind.Array)
                return null;

            return
            [
                .. document.RootElement.EnumerateArray().Where(entry => entry.ValueKind == JsonValueKind.Object).Select(entry => new Listed(
                    JsonFields.Text(entry, "id") ?? "",
                    JsonFields.Text(entry, "sessionId"),
                    JsonFields.Text(entry, "state"),
                    JsonFields.Text(entry, "waitingFor"),
                    entry.TryGetProperty("pid", out var pid) && pid.ValueKind == JsonValueKind.Number || JsonFields.Text(entry, "status") is not null,
                    JsonFields.Text(entry, "name"),
                    entry.TryGetProperty("startedAt", out var started) && started.ValueKind == JsonValueKind.Number && started.TryGetInt64(out var at) ? at : null)),
            ];
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
