using System.Text.Json;
using System.Text.RegularExpressions;
using TooBusy.Core.Processes;
using TooBusy.Core.Run;

namespace TooBusy.Assistants.ClaudeCode;

// Claude Code as the assistant of a run. A task is done by a background session, `claude --bg`: it goes on by itself
// in the working copy, the owner can open it with `claude attach`, and it is kept when the run ends. What a session
// is doing is read from the list of the sessions, `claude agents --json`, and from the conversation Claude Code
// writes. The commands must start at the root of the project: the runner is the one of that folder.
public sealed partial class ClaudeCode(IProcessRunner processes, IClock clock, ClaudeFolders folders, string root) : IAssistant
{
    readonly IClock clock = clock;
    readonly ClaudeFolders folders = folders;
    readonly string root = root;

    // How long a command of Claude Code is waited for.
    static readonly TimeSpan Patience = TimeSpan.FromMinutes(2);

    // For how long the list of the sessions may be silent, or may lack a session, before that is told of.
    static readonly TimeSpan Tolerance = TimeSpan.FromMinutes(1);

    // How a session that is stopped is waited for: so many looks, so far apart.
    const int StopLooks = 40;
    static readonly TimeSpan StopPause = TimeSpan.FromMilliseconds(500);

    // How many times a session is tried to be gone on with, and for how long a session that was in the way is left
    // to go away.
    const int Tries = 3;
    static readonly TimeSpan Clearing = TimeSpan.FromSeconds(2);

    public async Task<IAssistantSession> StartAsync(SessionStart start, CancellationToken cancellationToken)
    {
        // The session may have said something by the time Claude Code answers: what counts starts now.
        var since = clock.Now;
        Forget();
        string[] model = start.Model.Name is { } named ? ["--model", named] : [];
        var result = await processes.RunAsync(
            "claude",
            ["--bg", "--permission-mode", "auto", "--permission-prompts", "none", "--settings", SessionSettings.Json(folders), .. model, "--effort", start.Effort, "-n", start.Name, start.Message],
            Patience,
            cancellationToken);
        var said = result.Output + "\n" + result.Error;
        if (result.Status == ProcessStatus.NotFound)
            throw new AssistantException("Claude Code is not installed: `claude` is not on the path.");
        if (said.Contains("not trusted", StringComparison.OrdinalIgnoreCase))
            throw new AssistantException("Claude Code does not trust this folder yet: run `claude` in it once and accept what it asks.");
        if (Started(said) is not { } id)
            throw new AssistantException($"Claude Code did not start a session for {start.Name}: {(result.Status == ProcessStatus.TimedOut ? "it did not answer" : First(said))}");

        return new Session(this, id, null, since);
    }

    public async Task<IAssistantSession?> ResumeAsync(string conversation, SessionStart start, CancellationToken cancellationToken)
    {
        if (!File.Exists(folders.Transcript(conversation)))
            return null;

        Forget();
        var session = new Session(this, null, conversation, clock.Now);
        return await session.TellAsync(start.Message, cancellationToken) ? session : null;
    }

    // The limits as the status line of the last session told of them; nothing is known before a session has run.
    public UsageLimits ReadLimits()
    {
        try
        {
            if (!File.Exists(folders.Usage))
                return UsageLimits.Unknown;

            using var document = JsonDocument.Parse(File.ReadAllBytes(folders.Usage));
            return document.RootElement.ValueKind == JsonValueKind.Object && document.RootElement.TryGetProperty("rate_limits", out var limits)
                ? new UsageLimits(Window(limits, "five_hour", "5-hour"), Window(limits, "seven_day", "weekly"))
                : UsageLimits.Unknown;
        }
        catch (Exception unreadable) when (unreadable is IOException or UnauthorizedAccessException or JsonException)
        {
            return UsageLimits.Unknown;
        }
    }

    static UsageWindow? Window(JsonElement limits, string key, string name) =>
        limits.ValueKind == JsonValueKind.Object && limits.TryGetProperty(key, out var window) && window.ValueKind == JsonValueKind.Object
        && window.TryGetProperty("used_percentage", out var used) && used.ValueKind == JsonValueKind.Number
            ? new UsageWindow(name, used.GetDouble(), window.TryGetProperty("resets_at", out var reset) && reset.TryGetInt64(out var seconds) ? DateTimeOffset.FromUnixTimeSeconds(seconds) : null)
            : null;

    // A request that waits for a session is for the one before: a new session starts without it.
    void Forget()
    {
        try
        {
            File.Delete(folders.Request);
        }
        catch (Exception unreachable) when (unreachable is IOException or UnauthorizedAccessException)
        {
        }
    }

    // The sessions Claude Code knows, by what is asked of them; null when the list cannot be read.
    async Task<List<Listed>?> ListAsync(CancellationToken cancellationToken)
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
                    Text(entry, "id") ?? "",
                    Text(entry, "sessionId"),
                    Text(entry, "state"),
                    Text(entry, "waitingFor"),
                    entry.TryGetProperty("pid", out var pid) && pid.ValueKind == JsonValueKind.Number || Text(entry, "status") is not null)),
            ];
        }
        catch (JsonException)
        {
            return null;
        }
    }

    Task<ProcessResult> AskAsync(string[] arguments, CancellationToken cancellationToken) => processes.RunAsync("claude", arguments, Patience, cancellationToken);

    static string? Text(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    static string? Started(string said) => Backgrounded().Match(said) is { Success: true } match ? match.Groups[1].Value : null;

    static string First(string said) =>
        said.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).FirstOrDefault() ?? "it said nothing";

    // What Claude Code answers when a session went to the background: the name the session is known by from then on.
    [GeneratedRegex(@"backgrounded · ([0-9a-f]+)")]
    private static partial Regex Backgrounded();

    // A session of the list. Alive tells that a process still has it.
    sealed record Listed(string Id, string? Conversation, string? State, string? WaitingFor, bool Alive);

    // A background session. It is known by the short name Claude Code gave it; its conversation, which later runs go
    // on with it by, is learnt from the list of the sessions.
    sealed class Session(ClaudeCode claude, string? id, string? conversation, DateTimeOffset since) : IAssistantSession
    {
        string? id = id;
        Transcript? transcript;

        // Since when the list is silent, and since when it lacks the session; null when neither is so.
        DateTimeOffset? silent;
        DateTimeOffset? missing;

        public string Open => $"claude attach {id ?? Conversation}";

        public string? Conversation { get; private set; } = conversation;

        public async Task<SessionLook> LookAsync(CancellationToken cancellationToken)
        {
            var now = claude.clock.Now;
            var phase = SessionPhase.Working;
            string? asks = null;
            if (await claude.ListAsync(cancellationToken) is not { } listed)
            {
                silent ??= now;
                if (now - silent >= Tolerance)
                    phase = SessionPhase.Unseen;
            }
            else if (Find(listed) is not { } found)
            {
                // A session that has just started is not always listed at once.
                silent = null;
                missing ??= now;
                if (now - missing >= Tolerance)
                    phase = SessionPhase.Lost;
            }
            else
            {
                silent = null;
                missing = null;
                Conversation ??= found.Conversation;
                (phase, asks) = found.State switch
                {
                    "done" => (SessionPhase.Ended, null),
                    "failed" or "stopped" => (SessionPhase.Lost, null),
                    "blocked" => (SessionPhase.Asking, found.WaitingFor),
                    _ => (SessionPhase.Working, (string?)null),
                };
            }

            if (Conversation is { } known)
                (transcript ??= new Transcript(claude.folders.Transcript(known), since, claude.root)).Read();
            return new SessionLook(phase, asks, transcript?.Step, transcript?.Steps ?? 0, transcript?.Context ?? 0, transcript?.Reply, transcript?.Limit, transcript?.Plan);
        }

        public async Task<bool> TellAsync(string message, CancellationToken cancellationToken)
        {
            for (var attempt = 0; attempt < Tries; attempt++)
            {
                // A session goes on only when nothing has it: it is stopped first, and waited for.
                if (await claude.ListAsync(cancellationToken) is { } listed && Find(listed) is { } found)
                {
                    Conversation ??= found.Conversation;
                    if (found.Alive)
                        await StopAndWaitAsync(found.Id, cancellationToken);
                }

                if (Conversation is not { } known)
                    return false;

                var result = await claude.AskAsync(["--bg", "--resume", known, message], cancellationToken);
                var said = result.Output + "\n" + result.Error;
                if (Started(said) is not { } woken)
                    return false;

                // Claude Code may start a copy of a session that something still has: that copy is stopped and
                // forgotten, and the session is tried again.
                if (said.Contains("already running", StringComparison.OrdinalIgnoreCase) || said.Contains("started a copy", StringComparison.OrdinalIgnoreCase))
                {
                    await claude.AskAsync(["stop", woken], cancellationToken);
                    await claude.AskAsync(["rm", woken], cancellationToken);
                    await claude.clock.DelayAsync(Clearing, cancellationToken);
                    continue;
                }

                id = woken;
                silent = null;
                missing = null;
                return true;
            }

            return false;
        }

        public Task AskAsync(string message, CancellationToken cancellationToken)
        {
            // The hook of the session prints the file after every step: it is put in place whole, never half written.
            Directory.CreateDirectory(claude.folders.Local);
            var written = claude.folders.Request + ".tmp";
            File.WriteAllBytes(written, SessionSettings.Request(message));
            File.Move(written, claude.folders.Request, overwrite: true);
            return Task.CompletedTask;
        }

        public async Task StopAsync(CancellationToken cancellationToken)
        {
            claude.Forget();
            if (id is not null)
                await claude.AskAsync(["stop", id], cancellationToken);
        }

        Listed? Find(List<Listed> listed) =>
            listed.FirstOrDefault(entry => id is not null && entry.Id == id) ?? listed.LastOrDefault(entry => Conversation is not null && entry.Conversation == Conversation);

        async Task StopAndWaitAsync(string running, CancellationToken cancellationToken)
        {
            await claude.AskAsync(["stop", running], cancellationToken);
            for (var look = 0; look < StopLooks; look++)
            {
                if (await claude.ListAsync(cancellationToken) is not { } listed || listed.FirstOrDefault(entry => entry.Id == running) is not { Alive: true })
                    return;

                await claude.clock.DelayAsync(StopPause, cancellationToken);
            }
        }
    }
}
