using System.Text.Json;
using TooBusy.Core.Assistant;
using TooBusy.Core.Processes;
using TooBusy.Core.Run;

namespace TooBusy.Assistants.ClaudeCode.Tests;

public sealed class ClaudeCodeTests : IDisposable
{
    const string Id = "15555bc6";
    const string Conversation = "15555bc6-0c7f-4efb-a0a0-211c72424c97";
    const string Backgrounded = "Starting background service…\nbackgrounded · 15555bc6 · #12 Export the data\n  claude attach 15555bc6    open in this terminal\n";

    static readonly SessionStart Start = new(12, "#12 Export the data", "Do the task.", new ModelChoice("opus"), "high");

    readonly DirectoryInfo folder = Directory.CreateTempSubdirectory("toobusy-claude-");
    readonly FakeClaude claude = new();
    readonly StepClock clock = new();
    readonly ClaudeFolders folders;

    public ClaudeCodeTests()
    {
        folders = new ClaudeFolders(folder.CreateSubdirectory("local").FullName, folder.CreateSubdirectory("transcripts").FullName);
        claude.Answer = _ => Exited(Backgrounded);
    }

    public void Dispose() => folder.Delete(recursive: true);

    [Fact]
    public async Task ASessionIsStartedInTheBackgroundWithItsMessageAndItsSettings()
    {
        var session = await Assistant().StartAsync(Start, TestContext.Current.CancellationToken);

        var arguments = Assert.Single(claude.Asked);
        Assert.Equal(["--bg", "--permission-mode", "auto", "--permission-prompts", "none", "--settings"], arguments.Take(6));
        Assert.Equal(["--model", "opus", "--effort", "high", "-n", "#12 Export the data", "Do the task."], arguments.Skip(7));
        Assert.Equal("claude attach 15555bc6", session.Open);
        Assert.Null(session.Conversation);

        using var settings = JsonDocument.Parse(arguments[6]);
        var hook = settings.RootElement.GetProperty("hooks").GetProperty("PostToolUse")[0];
        Assert.Equal("*", hook.GetProperty("matcher").GetString());
        Assert.Equal("command", hook.GetProperty("hooks")[0].GetProperty("type").GetString());
        Assert.Equal($"cat '{folders.Request}' 2>/dev/null || true", hook.GetProperty("hooks")[0].GetProperty("command").GetString());
        Assert.Equal($"cat > '{folders.Usage}.tmp' && mv '{folders.Usage}.tmp' '{folders.Usage}'", settings.RootElement.GetProperty("statusLine").GetProperty("command").GetString());
        Assert.Equal("EnterWorktree", settings.RootElement.GetProperty("permissions").GetProperty("deny")[0].GetString());
        Assert.Equal("none", settings.RootElement.GetProperty("worktree").GetProperty("bgIsolation").GetString());
    }

    [Fact]
    public async Task TheAssistantsOwnModelIsNotNamed()
    {
        await Assistant().StartAsync(Start with { Model = ModelChoice.AssistantsOwn }, TestContext.Current.CancellationToken);

        Assert.DoesNotContain("--model", claude.Asked[0]);
        Assert.Equal(["--effort", "high", "-n", "#12 Export the data", "Do the task."], claude.Asked[0].Skip(7));
    }

    [Fact]
    public async Task APathWithAQuoteIsStillOneWordOfTheShell()
    {
        var odd = new ClaudeFolders("/work/it's here/local", folders.Transcripts);

        await new ClaudeCode(claude, clock, odd, "/work/rocket").StartAsync(Start, TestContext.Current.CancellationToken);

        using var settings = JsonDocument.Parse(claude.Asked[0][6]);
        Assert.Equal(
            "cat '/work/it'\\''s here/local/wrap-up.json' 2>/dev/null || true",
            settings.RootElement.GetProperty("hooks").GetProperty("PostToolUse")[0].GetProperty("hooks")[0].GetProperty("command").GetString());
    }

    [Fact]
    public async Task ARequestThatWaitedForTheSessionBeforeIsForgottenAtTheStart()
    {
        File.WriteAllText(folders.Request, "{}");

        await Assistant().StartAsync(Start, TestContext.Current.CancellationToken);

        Assert.False(File.Exists(folders.Request));
    }

    [Fact]
    public async Task AnAssistantThatIsNotInstalledSaysSo()
    {
        claude.Answer = _ => new ProcessResult(ProcessStatus.NotFound, 0, "", "");

        var refused = await Assert.ThrowsAsync<AssistantException>(() => Assistant().StartAsync(Start, TestContext.Current.CancellationToken));

        Assert.Equal("Claude Code is not installed: `claude` is not on the path.", refused.Message);
    }

    [Fact]
    public async Task AFolderThatIsNotTrustedYetSaysWhatToDo()
    {
        claude.Answer = _ => Exited("Workspace not trusted. Run `claude` in /work/rocket once and accept the trust prompt, then retry.\n");

        var refused = await Assert.ThrowsAsync<AssistantException>(() => Assistant().StartAsync(Start, TestContext.Current.CancellationToken));

        Assert.Equal("Claude Code does not trust this folder yet: run `claude` in it once and accept what it asks.", refused.Message);
    }

    [Fact]
    public async Task ASessionThatDoesNotStartSaysWhatClaudeCodeSaid()
    {
        claude.Answer = _ => new ProcessResult(ProcessStatus.Exited, 1, "", "\nerror: unknown option '--bg'\nUsage: claude\n");

        var refused = await Assert.ThrowsAsync<AssistantException>(() => Assistant().StartAsync(Start, TestContext.Current.CancellationToken));

        Assert.Equal("Claude Code did not start a session for #12 Export the data: error: unknown option '--bg'", refused.Message);

        claude.Answer = _ => new ProcessResult(ProcessStatus.TimedOut, 0, "", "");
        var silent = await Assert.ThrowsAsync<AssistantException>(() => Assistant().StartAsync(Start, TestContext.Current.CancellationToken));
        Assert.Equal("Claude Code did not start a session for #12 Export the data: it did not answer", silent.Message);
    }

    [Theory]
    [InlineData("working", "busy", null, SessionPhase.Working, null)]
    [InlineData("blocked", "waiting", "input needed", SessionPhase.Asking, "input needed")]
    [InlineData("blocked", "waiting", null, SessionPhase.Asking, null)]
    [InlineData("done", "idle", null, SessionPhase.Ended, null)]
    [InlineData("stopped", null, null, SessionPhase.Lost, null)]
    [InlineData("failed", null, null, SessionPhase.Lost, null)]
    public async Task TheListOfTheSessionsTellsWhereASessionStands(string state, string? status, string? waitingFor, SessionPhase phase, string? asks)
    {
        var session = await Assistant().StartAsync(Start, TestContext.Current.CancellationToken);
        claude.Sessions = Listing(Entry(Id, Conversation, state, status, waitingFor), Entry("ffff0000", "another", "blocked", "waiting", "input needed"));

        var look = await session.LookAsync(TestContext.Current.CancellationToken);

        Assert.Equal((phase, asks), (look.Phase, look.Asks));
        Assert.Equal(Conversation, session.Conversation);
        Assert.Equal(["agents", "--json", "--all"], claude.Asked[^1]);
    }

    [Fact]
    public async Task ASessionThatIsNotListedYetWorksAndIsLostAfterAMinute()
    {
        var session = await Assistant().StartAsync(Start, TestContext.Current.CancellationToken);

        Assert.Equal(SessionPhase.Working, (await session.LookAsync(TestContext.Current.CancellationToken)).Phase);
        clock.Pass(TimeSpan.FromSeconds(59));
        Assert.Equal(SessionPhase.Working, (await session.LookAsync(TestContext.Current.CancellationToken)).Phase);
        clock.Pass(TimeSpan.FromSeconds(1));
        Assert.Equal(SessionPhase.Lost, (await session.LookAsync(TestContext.Current.CancellationToken)).Phase);
    }

    [Fact]
    public async Task AListThatCannotBeReadIsBorneForAMinute()
    {
        var session = await Assistant().StartAsync(Start, TestContext.Current.CancellationToken);
        claude.Sessions = Listing(Entry(Id, Conversation, "working", "busy"));
        await session.LookAsync(TestContext.Current.CancellationToken);

        claude.List = new ProcessResult(ProcessStatus.TimedOut, 0, "", "");
        Assert.Equal(SessionPhase.Working, (await session.LookAsync(TestContext.Current.CancellationToken)).Phase);
        clock.Pass(TimeSpan.FromMinutes(1));
        Assert.Equal(SessionPhase.Unseen, (await session.LookAsync(TestContext.Current.CancellationToken)).Phase);

        claude.List = Exited("not json");
        Assert.Equal(SessionPhase.Unseen, (await session.LookAsync(TestContext.Current.CancellationToken)).Phase);

        claude.List = null;
        Assert.Equal(SessionPhase.Working, (await session.LookAsync(TestContext.Current.CancellationToken)).Phase);
    }

    [Fact]
    public async Task WhatASessionDidIsReadFromItsConversation()
    {
        var session = await Assistant().StartAsync(Start, TestContext.Current.CancellationToken);
        claude.Sessions = Listing(Entry(Id, Conversation, "done", "idle"));
        Write(
            Said("An old end.\nTOOBUSY: done", at: "2029-12-31T23:00:00Z"),
            """{"type":"user","timestamp":"2030-01-01T09:00:01Z","message":{"role":"user","content":"Do the task."}}""",
            Used("Read", """{"file_path":"/work/rocket/src/a.cs"}"""),
            Used("Bash", """{"command":"dotnet test\n--no-build"}"""),
            Said("Half way."),
            Said("From a helper.", extra: ""","isSidechain":true"""),
            Used("AskUserQuestion", """{"questions":[]}"""),
            Said("No response requested.", model: "<synthetic>"),
            "not json at all",
            Said("Did it.\nTOOBUSY: done", tokens: 120));

        var look = await session.LookAsync(TestContext.Current.CancellationToken);

        Assert.Equal(SessionPhase.Ended, look.Phase);
        Assert.Equal("AskUserQuestion", look.Step);
        Assert.Equal(2, look.Steps);
        Assert.Equal(120 + 20_000 + 1_500, look.Context);
        Assert.Equal("Did it.\nTOOBUSY: done", look.Reply);
        Assert.Null(look.Limit);
    }

    [Theory]
    [InlineData("Bash", """{"command":"dotnet test"}""", "Bash: dotnet test")]
    [InlineData("Edit", """{"file_path":"/work/rocket/src/a.cs"}""", "Edit src/a.cs")]
    [InlineData("Read", """{"file_path":"/etc/hosts"}""", "Read /etc/hosts")]
    [InlineData("Grep", """{"pattern":"TODO"}""", "Grep TODO")]
    [InlineData("Task", """{"description":"Explore the code"}""", "Task Explore the code")]
    [InlineData("WebSearch", "{}", "WebSearch")]
    [InlineData("Bash", """{"command":"0123456789 0123456789 0123456789 0123456789 0123456789 0123456789 0123456789 0123456789"}""", "Bash: 0123456789 0123456789 0123456789 0123456789 0123456789 0123456789 0123456…")]
    public async Task AStepNamesTheToolAndWhatItIsUsedOn(string tool, string input, string step)
    {
        var session = await Assistant().StartAsync(Start, TestContext.Current.CancellationToken);
        claude.Sessions = Listing(Entry(Id, Conversation, "working", "busy"));
        Write(Used(tool, input));

        Assert.Equal(step, (await session.LookAsync(TestContext.Current.CancellationToken)).Step);
    }

    [Fact]
    public async Task ALineThatIsHalfWrittenWaitsForItsEnd()
    {
        var session = await Assistant().StartAsync(Start, TestContext.Current.CancellationToken);
        claude.Sessions = Listing(Entry(Id, Conversation, "working", "busy"));
        var line = Said("Did it.");
        File.AppendAllText(folders.Transcript(Conversation), line[..40]);

        Assert.Null((await session.LookAsync(TestContext.Current.CancellationToken)).Reply);

        File.AppendAllText(folders.Transcript(Conversation), line[40..] + "\n");

        Assert.Equal("Did it.", (await session.LookAsync(TestContext.Current.CancellationToken)).Reply);
        Assert.Equal("Did it.", (await session.LookAsync(TestContext.Current.CancellationToken)).Reply);
    }

    [Fact]
    public async Task AUsageLimitIsWhatTheSessionWasRefusedWithUntilItGoesOn()
    {
        var session = await Assistant().StartAsync(Start, TestContext.Current.CancellationToken);
        claude.Sessions = Listing(Entry(Id, Conversation, "done", "idle"));
        Write(Said("Working on it."), Said("Overloaded.", model: "<synthetic>", extra: ""","isApiErrorMessage":true,"error":"overloaded_error" """));
        Assert.Null((await session.LookAsync(TestContext.Current.CancellationToken)).Limit);

        Write(Said("You have hit your limit · resets 3pm", model: "<synthetic>", extra: ""","isApiErrorMessage":true,"error":"rate_limit" """));
        var refused = await session.LookAsync(TestContext.Current.CancellationToken);
        Assert.Equal("You have hit your limit · resets 3pm", refused.Limit);
        Assert.Equal("Working on it.", refused.Reply);

        Write(Said("On it again."));
        Assert.Null((await session.LookAsync(TestContext.Current.CancellationToken)).Limit);
    }

    [Fact]
    public async Task TellingASessionStopsItAndGoesOnWithItsConversation()
    {
        var session = await Assistant().StartAsync(Start, TestContext.Current.CancellationToken);
        claude.Listings.Enqueue(Listing(Entry(Id, Conversation, "blocked", "waiting", "input needed")));
        claude.Listings.Enqueue(Listing(Entry(Id, Conversation, "blocked", "waiting", "input needed")));
        claude.Sessions = Listing(Entry(Id, Conversation, "stopped"));
        claude.Answer = arguments => Exited(arguments[0] == "stop" ? "stopped 15555bc6\n" : "note: woke session 15555bc6 with its saved options (--settings, -n).\n" + Backgrounded);

        var told = await session.TellAsync("Go on alone.", TestContext.Current.CancellationToken);

        Assert.True(told);
        Assert.Equal(
            [["agents", "--json", "--all"], ["stop", Id], ["agents", "--json", "--all"], ["agents", "--json", "--all"], ["--bg", "--resume", Conversation, "Go on alone."]],
            claude.Asked.Skip(1));
        Assert.Equal(TimeSpan.FromMilliseconds(500), clock.Passed);
        Assert.Equal(Conversation, session.Conversation);
    }

    [Fact]
    public async Task ACopyOfASessionIsStoppedAndForgottenAndTheSessionIsTriedAgain()
    {
        var session = await Assistant().StartAsync(Start, TestContext.Current.CancellationToken);
        claude.Sessions = Listing(Entry(Id, Conversation, "stopped"));
        var resumes = 0;
        claude.Answer = arguments => Exited(arguments[0] != "--bg" ? ""
            : ++resumes == 1 ? "note: session 15555bc6 is already running; started a copy.\nbackgrounded · aaaa1111 · #12 Export the data\n"
            : Backgrounded);

        var told = await session.TellAsync("Go on alone.", TestContext.Current.CancellationToken);

        Assert.True(told);
        Assert.Equal(2, resumes);
        Assert.Contains(claude.Asked, arguments => arguments.SequenceEqual(["stop", "aaaa1111"]));
        Assert.Contains(claude.Asked, arguments => arguments.SequenceEqual(["rm", "aaaa1111"]));
        Assert.Equal("claude attach 15555bc6", session.Open);
    }

    [Fact]
    public async Task ASessionThatDoesNotGoOnSaysSo()
    {
        var session = await Assistant().StartAsync(Start, TestContext.Current.CancellationToken);
        claude.Sessions = Listing(Entry(Id, Conversation, "stopped"));
        claude.Answer = _ => new ProcessResult(ProcessStatus.Exited, 1, "", "error: no conversation found");

        Assert.False(await session.TellAsync("Go on alone.", TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task ASessionWhoseConversationIsNotKnownCannotBeTold()
    {
        var session = await Assistant().StartAsync(Start, TestContext.Current.CancellationToken);

        Assert.False(await session.TellAsync("Go on alone.", TestContext.Current.CancellationToken));
        Assert.DoesNotContain(claude.Asked, arguments => arguments.Contains("--resume"));
    }

    [Fact]
    public async Task ARequestIsPutWhereTheHookOfTheSessionPrintsItAndTakenAwayWhenTheSessionIsStopped()
    {
        var session = await Assistant().StartAsync(Start, TestContext.Current.CancellationToken);

        await session.AskAsync("Wrap up: \"now\".", TestContext.Current.CancellationToken);

        using (var request = JsonDocument.Parse(File.ReadAllText(folders.Request)))
        {
            var told = request.RootElement.GetProperty("hookSpecificOutput");
            Assert.Equal("PostToolUse", told.GetProperty("hookEventName").GetString());
            Assert.Equal("Wrap up: \"now\".", told.GetProperty("additionalContext").GetString());
        }

        Assert.False(File.Exists(folders.Request + ".tmp"));

        await session.StopAsync(TestContext.Current.CancellationToken);

        Assert.False(File.Exists(folders.Request));
        Assert.Equal(["stop", Id], claude.Asked[^1]);
    }

    [Fact]
    public async Task ASessionOfAnEarlierRunGoesOnByItsConversation()
    {
        Write(Said("I was stopped by the limit.", at: "2029-12-31T23:00:00Z"));
        claude.Sessions = Listing(Entry(Id, Conversation, "stopped"));

        var session = await Assistant().ResumeAsync(Conversation, Start with { Message = "The limit has reset." }, TestContext.Current.CancellationToken);

        Assert.NotNull(session);
        Assert.Equal(["--bg", "--resume", Conversation, "The limit has reset."], claude.Asked[^1]);
        Assert.Equal(("claude attach 15555bc6", Conversation), (session.Open, session.Conversation));

        // What it said before this run took it does not count.
        Assert.Null((await session.LookAsync(TestContext.Current.CancellationToken)).Reply);
    }

    [Fact]
    public async Task ASessionWhoseConversationIsGoneCannotGoOn()
    {
        Assert.Null(await Assistant().ResumeAsync(Conversation, Start, TestContext.Current.CancellationToken));
        Assert.Empty(claude.Asked);

        Write(Said("Here."));
        claude.Answer = _ => new ProcessResult(ProcessStatus.Exited, 1, "", "error");
        Assert.Null(await Assistant().ResumeAsync(Conversation, Start, TestContext.Current.CancellationToken));
    }

    [Fact]
    public void TheUsageLimitsAreWhatTheStatusLineOfTheLastSessionTold()
    {
        Assert.Equal(UsageLimits.Unknown, Assistant().ReadLimits());

        File.WriteAllText(folders.Usage, """{"session_id":"s","rate_limits":{"five_hour":{"used_percentage":2.5,"resets_at":1791675600},"seven_day":{"used_percentage":94}}}""");

        var limits = Assistant().ReadLimits();
        Assert.Equal(new UsageWindow("5-hour", 2.5, DateTimeOffset.FromUnixTimeSeconds(1791675600)), limits.Near);
        Assert.Equal(new UsageWindow("weekly", 94, null), limits.Far);
    }

    [Theory]
    [InlineData("")]
    [InlineData("not json")]
    [InlineData("""{"model":"opus"}""")]
    [InlineData("""{"rate_limits":{"five_hour":{"resets_at":1}}}""")]
    public void AStatusThatTellsNothingOfTheLimitsLeavesThemUnknown(string status)
    {
        File.WriteAllText(folders.Usage, status);

        Assert.Equal(UsageLimits.Unknown, Assistant().ReadLimits());
    }

    [Fact]
    public void TheConversationsOfAProjectLieInAFolderNamedAfterItsPath()
    {
        var plain = ClaudeFolders.Of("/Users/ann/My Projects/rocket_2", "/local", "/Users/ann", _ => null);
        var moved = ClaudeFolders.Of("/work/rocket", "/local", "/Users/ann", name => name == "CLAUDE_CONFIG_DIR" ? "/etc/claude" : null);

        Assert.Equal(Path.Combine("/Users/ann", ".claude", "projects", "-Users-ann-My-Projects-rocket-2"), plain.Transcripts);
        Assert.Equal(Path.Combine("/etc/claude", "projects", "-work-rocket"), moved.Transcripts);
        Assert.Equal(Path.Combine(plain.Transcripts, "abc.jsonl"), plain.Transcript("abc"));
        Assert.Equal((Path.Combine("/local", "wrap-up.json"), Path.Combine("/local", "usage.json")), (plain.Request, plain.Usage));
    }

    ClaudeCode Assistant() => new(claude, clock, folders, "/work/rocket");

    void Write(params string[] lines) => File.AppendAllText(folders.Transcript(Conversation), string.Join('\n', lines) + "\n");

    static ProcessResult Exited(string output) => new(ProcessStatus.Exited, 0, output, "");

    static string Listing(params string[] entries) => "[" + string.Join(',', entries) + "]";

    // A session as the list of Claude Code has it: one that a process has says so with its `pid` and its `status`.
    static string Entry(string id, string conversation, string state, string? status = null, string? waitingFor = null) =>
        "{" + $"\"id\":\"{id}\",\"cwd\":\"/work/rocket\",\"kind\":\"background\",\"startedAt\":1791661881643,\"sessionId\":\"{conversation}\",\"name\":\"#12 Export the data\",\"state\":\"{state}\""
        + (status is null ? "" : $",\"pid\":56109,\"status\":\"{status}\"")
        + (waitingFor is null ? "" : $",\"waitingFor\":\"{waitingFor}\"")
        + "}";

    static string Said(string text, string at = "2030-01-01T09:00:05Z", string model = "claude-opus", string extra = "", int tokens = 10) =>
        "{" + $"\"type\":\"assistant\",\"timestamp\":\"{at}\"{extra},\"message\":" + "{" + $"\"model\":\"{model}\",\"content\":["
        + "{\"type\":\"thinking\",\"thinking\":\"…\"},{\"type\":\"text\",\"text\":" + JsonSerializer.Serialize(text) + "}],"
        + "\"usage\":{" + $"\"input_tokens\":{tokens},\"cache_read_input_tokens\":20000,\"cache_creation_input_tokens\":1500" + "}}}";

    static string Used(string tool, string input) =>
        "{\"type\":\"assistant\",\"timestamp\":\"2030-01-01T09:00:03Z\",\"message\":{\"model\":\"claude-opus\",\"content\":[{\"type\":\"tool_use\",\"name\":\"" + tool + "\",\"input\":" + input + "}]}}";
}

// The commands of Claude Code, answered as the test says: the list of the sessions on its own, everything else by
// what is asked. It remembers the arguments of every command.
sealed class FakeClaude : IProcessRunner
{
    public List<IReadOnlyList<string>> Asked { get; } = [];

    // What `claude agents` prints: the listings that wait, one after another, and then the one that stands.
    public Queue<string> Listings { get; } = new();

    public string Sessions { get; set; } = "[]";

    // What `claude agents` answers instead, when it is to fail.
    public ProcessResult? List { get; set; }

    public Func<IReadOnlyList<string>, ProcessResult> Answer { get; set; } = _ => new ProcessResult(ProcessStatus.Exited, 0, "", "");

    public Task<ProcessResult> RunAsync(string command, IReadOnlyList<string> arguments, TimeSpan timeout, CancellationToken cancellationToken)
    {
        Assert.Equal("claude", command);
        Asked.Add(arguments);
        return Task.FromResult(arguments[0] != "agents" ? Answer(arguments)
            : List ?? new ProcessResult(ProcessStatus.Exited, 0, Listings.Count > 0 ? Listings.Dequeue() : Sessions, ""));
    }

    public IProcessRunner Inside(string folder) => this;
}

// A clock that goes on only when it is waited on or told to.
sealed class StepClock : IClock
{
    static readonly DateTimeOffset Start = new(2030, 1, 1, 9, 0, 0, TimeSpan.Zero);

    public DateTimeOffset Now { get; private set; } = Start;

    // How much time went by in waiting.
    public TimeSpan Passed { get; private set; }

    public void Pass(TimeSpan time) => Now += time;

    public Task DelayAsync(TimeSpan time, CancellationToken cancellationToken)
    {
        Now += time;
        Passed += time;
        return Task.CompletedTask;
    }
}
