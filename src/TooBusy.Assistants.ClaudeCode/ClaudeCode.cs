using System.Text.Json;
using TooBusy.Core.Processes;
using TooBusy.Core.Run;

namespace TooBusy.Assistants.ClaudeCode;

// Claude Code as the assistant of a run. A task is done by a background session, `claude --bg`: it goes on by itself
// in the working copy, the owner can open it with `claude attach`, and it is kept when the run ends. What a session
// is doing is read from the list of the sessions, `claude agents --json --all`, and from the conversation Claude Code
// writes. The commands must start at the root of the project: the runner is the one of that folder. Every session is
// started in the permission mode of the settings, and goes on in it when it is told something, a session of an
// earlier run too.
//
// The list of the sessions is a command that starts Claude Code, so a look at a session takes it from a list read no
// longer than a while ago, as `SessionList` says; whatever changes a session, and whatever must know how a session is
// now, reads it anew. The working copies of a run of several sessions each have a `ClaudeCode` and share one list.
//
// What Claude Code answers, lists and writes is read as `ClaudeCodeProtocol` says, and no further: a state of the
// list that it does not know is not taken for work, and a conversation that is nowhere to be found is said.
public sealed class ClaudeCode(IProcessRunner processes, IClock clock, ClaudeFolders folders, string root, string permissionMode, SessionList sessions) : IAssistant
{
    readonly IClock clock = clock;
    readonly ClaudeFolders folders = folders;
    readonly string root = root;
    readonly string permissionMode = permissionMode;
    readonly SessionList sessions = sessions;

    // A `ClaudeCode` with a list of its own, in a project with one working copy.
    public ClaudeCode(IProcessRunner processes, IClock clock, ClaudeFolders folders, string root, string permissionMode, TimeSpan? listAge = null)
        : this(processes, clock, folders, root, permissionMode, new SessionList(processes, clock, listAge))
    {
    }

    // How long a command of Claude Code is waited for.
    static readonly TimeSpan Patience = SessionList.Patience;

    // For how long the list of the sessions may be silent, may lack a session or have it in a state that is not
    // known, and for how long the conversation of a session that it has may be nowhere, before that is told of.
    static readonly TimeSpan Tolerance = TimeSpan.FromMinutes(1);

    // How a session that is stopped is waited for: so many looks, so far apart.
    const int StopLooks = 40;
    static readonly TimeSpan StopPause = TimeSpan.FromMilliseconds(500);

    // How a session that a start which was cut off may have left is looked for: so many looks, so far apart.
    const int StartLooks = 3;
    static readonly TimeSpan StartPause = TimeSpan.FromSeconds(1);

    // How many times a session is tried to be gone on with, and for how long a session that was in the way is left
    // to go away.
    const int Tries = 3;
    static readonly TimeSpan Clearing = TimeSpan.FromSeconds(2);

    public async Task<IAssistantSession> StartAsync(SessionStart start, CancellationToken cancellationToken)
    {
        // The session may have said something by the time Claude Code answers: what counts starts now.
        var since = clock.Now;
        Forget();
        sessions.Forget();
        string[] model = start.Model.Name is { } named ? ["--model", named] : [];
        var result = await processes.RunAsync(
            "claude",
            ["--bg", "--permission-mode", permissionMode, "--permission-prompts", "none", "--settings", ClaudeSettings.Json(folders), .. model, "--effort", start.Effort, "-n", start.Name, start.Message],
            Patience,
            cancellationToken);
        var said = result.Output + "\n" + result.Error;
        if (Unrun(result) is { } unrun)
            throw new AssistantException(unrun + ".");
        if (ClaudeCodeProtocol.Untrusted(said))
            throw new AssistantException("Claude Code does not trust this folder yet: run `claude` in it once and accept what it asks.");
        if (ClaudeCodeProtocol.Started(said) is not { } id)
            throw new AssistantException($"Claude Code did not start a session for {start.Name}: {(result.Status == ProcessStatus.TimedOut ? "it did not answer" : First(said))}");

        return new Session(this, id, null, since);
    }

    // A session of an earlier run is the one of the list that has its name or its conversation. Nothing is left
    // of it when no process has it and its conversation is not known or is in no folder of the projects of Claude
    // Code any more. While the list cannot be read nothing is known, and the session says so when it is looked at.
    public async Task<IAssistantSession?> FindAsync(SessionTrace trace, CancellationToken cancellationToken)
    {
        var listed = await sessions.ReadAsync(cancellationToken);
        var found = listed is null ? null : Session.Find(listed, trace.Id, trace.Conversation);
        var conversation = trace.Conversation ?? found?.Conversation;
        if (listed is not null && found is not { Alive: true } && (conversation is null || folders.Find(conversation) is null))
            return null;

        return new Session(this, found?.Id ?? trace.Id, conversation, trace.Since, earlier: true);
    }

    // A start that was cut off may have reached Claude Code all the same: its session is the one of the list that has
    // the name of the start and was started since the moment, to the millisecond the list gives. One that has just
    // started is not always listed at once, so the list is read again a few times.
    public async Task<IAssistantSession?> FindStartedAsync(SessionStart start, DateTimeOffset since, CancellationToken cancellationToken)
    {
        for (var look = 1; ; look++)
        {
            var listed = await sessions.ReadAsync(cancellationToken);
            if (listed?.LastOrDefault(entry => entry.Name == start.Name && entry.Started >= since.ToUnixTimeMilliseconds()) is { } found)
                return new Session(this, found.Id, found.Conversation, since);
            if (look == StartLooks)
                return null;

            await clock.DelayAsync(StartPause, cancellationToken);
        }
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
            ? new UsageWindow(name, used.GetDouble(), window.TryGetProperty("resets_at", out var reset) ? Time(reset) : null)
            : null;

    // The time of a reset is Unix seconds; anything else is a time that is not known.
    static DateTimeOffset? Time(JsonElement reset) =>
        reset.ValueKind == JsonValueKind.Number && reset.TryGetInt64(out var seconds)
        && seconds >= DateTimeOffset.MinValue.ToUnixTimeSeconds() && seconds <= DateTimeOffset.MaxValue.ToUnixTimeSeconds()
            ? DateTimeOffset.FromUnixTimeSeconds(seconds)
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

    Task<ProcessResult> AskAsync(string[] arguments, CancellationToken cancellationToken) => processes.RunAsync("claude", arguments, Patience, cancellationToken);

    static string First(string said) =>
        said.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).FirstOrDefault() ?? "it said nothing";

    // What is said of a command of Claude Code that did not run at all: `claude` is not on the path, or it is and
    // could not be started; null for one that ran.
    static string? Unrun(ProcessResult result) => result.Status switch
    {
        ProcessStatus.NotFound => "Claude Code is not installed: `claude` is not on the path",
        ProcessStatus.NotStarted => $"Claude Code could not be started: {result.Error}",
        _ => null,
    };

    // A background session. It is known by the short name Claude Code gave it; its conversation, which later runs go
    // on with it by, is learnt from the list of the sessions.
    //
    // A session of an earlier run has been listed for long: a list that is silent or lacks it is believed at once. A
    // state that is not known may pass, and a conversation may not be written yet, for it as for any other.
    sealed class Session(ClaudeCode claude, string? id, string? conversation, DateTimeOffset since, bool earlier = false) : IAssistantSession
    {
        string? id = id;
        Transcript? transcript;

        // Since when the list is silent, since when it lacks the session, since when it has the session in a state
        // that is not known, and since when the conversation of the session it has is nowhere; null when none is so.
        DateTimeOffset? silent = earlier ? DateTimeOffset.MinValue : null;
        DateTimeOffset? missing = earlier ? DateTimeOffset.MinValue : null;
        DateTimeOffset? unknown;
        DateTimeOffset? unfound;

        public string Open => $"claude attach {id ?? Conversation}";

        public SessionTrace Trace => new(id, Conversation, since);

        string? Conversation { get; set; } = conversation;

        public async Task<SessionLook> LookAsync(CancellationToken cancellationToken)
        {
            var now = claude.clock.Now;
            var phase = SessionPhase.Working;
            string? asks = null;
            string? odd = null;
            var listed = await claude.sessions.RecentAsync(cancellationToken);
            var found = listed is null ? null : Find(listed, id, Conversation);
            if (listed is null)
            {
                silent ??= now;
                if (now - silent >= Tolerance)
                    phase = SessionPhase.Unseen;
            }
            else if (found is null)
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
                var standing = ClaudeCodeProtocol.StandingOf(found.State);
                unknown = standing == Standing.Unknown ? unknown ?? now : null;
                (phase, asks, odd) = standing switch
                {
                    Standing.Ended => (SessionPhase.Ended, null, null),
                    Standing.Lost => (SessionPhase.Lost, null, null),
                    Standing.Asks => (SessionPhase.Asking, found.WaitingFor, null),
                    Standing.Works => (SessionPhase.Working, null, null),
                    _ => (now - unknown >= Tolerance ? SessionPhase.Lost : SessionPhase.Working, (string?)null, Unknown(found.State)),
                };
            }

            if (transcript is null && Conversation is { } known && claude.folders.Find(known) is { } file)
                transcript = new Transcript(file, since, claude.root);
            transcript?.Read();

            // A session that Claude Code lists has its conversation on the machine: one that is nowhere a while after
            // is where toobusy does not know to look, and nothing the session does or says can be read.
            if (found is not null)
            {
                unfound = transcript is null && Conversation is not null ? unfound ?? now : null;
                if (now - unfound >= Tolerance)
                    throw new AssistantException($"the conversation of the session {found.Id} is not {claude.folders.Transcript(Conversation!)}, nor in any other folder of {claude.folders.Projects}: toobusy cannot read what the session does");
            }

            return new SessionLook(phase, asks, transcript?.Step, transcript?.Steps ?? 0, transcript?.Context ?? 0, transcript?.Reply, transcript?.Limit, transcript?.Plan, transcript?.Reported ?? false, odd);
        }

        // What is said of a session that the list has in a state that is not known.
        static string Unknown(string? state) =>
            state is null ? "the list of the sessions gives no state for its session" : $"its session is in a state toobusy does not know: {state}";

        // A session goes on only when nothing has it: it is stopped first, and waited for. Claude Code may start a
        // copy all the same, a new session with the whole conversation and the message, and says so. A copy of a
        // session that nothing has, by the list, is the session from then on; one of a session that something
        // still has is stopped and forgotten, and the session is tried again.
        public async Task<Telling> TellAsync(string message, CancellationToken cancellationToken)
        {
            // A request that waits for the session was for what it did before the message.
            var told = claude.clock.Now;
            claude.Forget();
            for (var attempt = 0; attempt < Tries; attempt++)
            {
                var listed = await claude.sessions.ReadAsync(cancellationToken);
                var found = listed is null ? null : Find(listed, id, Conversation);
                if (found is not null)
                {
                    Conversation ??= found.Conversation;
                    if (found.Alive)
                        await StopAndWaitAsync(found.Id, cancellationToken);
                }

                if (Conversation is not { } known)
                    return new Telling("its conversation is not known");

                // A session that Claude Code lists keeps the options it was started with and goes on with them:
                // given any again, Claude Code starts a copy instead. One that it does not list any more, whose
                // conversation is on the machine all the same, is given the options of a session once more.
                string[] options = found is null ? ["--permission-mode", claude.permissionMode, "--permission-prompts", "none", "--settings", ClaudeSettings.Json(claude.folders)] : [];
                var result = await claude.AskAsync(["--bg", "--resume", known, .. options, message], cancellationToken);
                var said = result.Output + "\n" + result.Error;
                if (result.Status == ProcessStatus.TimedOut)
                    return new Telling($"Claude Code did not answer in {Spoken.Time(Patience)}");
                if (Unrun(result) is { } unrun)
                    return new Telling(unrun);
                if (ClaudeCodeProtocol.Started(said) is not { } woken)
                    return new Telling(First(said));

                if (ClaudeCodeProtocol.Copied(said))
                {
                    // The list says whether something still has the session; one that cannot be read does not say
                    // that nothing has it.
                    var after = await claude.sessions.ReadAsync(cancellationToken);
                    if (after is null || Find(after, id, known) is { Alive: true })
                    {
                        await claude.AskAsync(["stop", woken], cancellationToken);
                        await claude.AskAsync(["rm", woken], cancellationToken);
                        await claude.clock.DelayAsync(Clearing, cancellationToken);
                        continue;
                    }

                    // The copy has a conversation of its own, which is learnt from the list when it is there.
                    Conversation = after.FirstOrDefault(entry => entry.Id == woken)?.Conversation;
                    transcript = null;
                    GoOn(woken, told);
                    return Telling.Copied;
                }

                GoOn(woken, told);
                return Telling.WentOn;
            }

            return new Telling($"Claude Code started a copy of the session {Tries} times: something still has it");
        }

        // The session is the one that woke from here on, and what it said before the message does not count.
        void GoOn(string woken, DateTimeOffset told)
        {
            claude.sessions.Forget();
            id = woken;
            silent = null;
            missing = null;
            unknown = null;
            unfound = null;
            since = told;
            transcript?.From(told);
        }

        public Task AskAsync(string message, CancellationToken cancellationToken)
        {
            // The hook of the session prints the file after every step: it is put in place whole, never half written.
            try
            {
                claude.folders.MakeLocal();
                var written = claude.folders.Request + ".tmp";
                File.WriteAllBytes(written, ClaudeSettings.Request(message));
                File.Move(written, claude.folders.Request, overwrite: true);
            }
            catch (Exception unwritten) when (unwritten is IOException or UnauthorizedAccessException)
            {
                throw new LocalStateException($"the request for the session cannot be written: {unwritten.Message}", unwritten);
            }

            return Task.CompletedTask;
        }

        // A session that the list still has as working after it was stopped and waited for is said: something may
        // work on in the working copy with nobody watching.
        public async Task StopAsync(CancellationToken cancellationToken)
        {
            claude.Forget();
            claude.sessions.Forget();
            if (id is { } running && !await StopAndWaitAsync(running, cancellationToken))
                throw new AssistantException($"Claude Code still lists the session {running} as working {Spoken.Time(StopPause * StopLooks)} after it was stopped");
        }

        public static Listed? Find(List<Listed> listed, string? id, string? conversation) =>
            listed.FirstOrDefault(entry => id is not null && entry.Id == id) ?? listed.LastOrDefault(entry => conversation is not null && entry.Conversation == conversation);

        // Stops the session and waits until nothing has it: false when the list has it as working all the while. A
        // list that cannot be read does not say that something still has the session.
        async Task<bool> StopAndWaitAsync(string running, CancellationToken cancellationToken)
        {
            await claude.AskAsync(["stop", running], cancellationToken);
            for (var look = 0; look < StopLooks; look++)
            {
                if (await claude.sessions.ReadAsync(cancellationToken) is not { } listed || listed.FirstOrDefault(entry => entry.Id == running) is not { Alive: true })
                    return true;

                await claude.clock.DelayAsync(StopPause, cancellationToken);
            }

            return false;
        }
    }
}
