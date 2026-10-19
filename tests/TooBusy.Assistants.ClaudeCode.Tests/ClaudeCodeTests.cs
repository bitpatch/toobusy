using System.Text;
using System.Text.Encodings.Web;
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
    const string Copy = "aaaa1111";
    const string CopyConversation = "aaaa1111-0c7f-4efb-a0a0-211c72424c97";

    // What Claude Code says when it starts a copy of a session instead of going on with it.
    static string CopyOf(string copy) =>
        $"note: session 15555bc6 is already running in the background, so this started a copy as {copy}. `claude attach 15555bc6` opens the original.\nbackgrounded · {copy}\n  claude attach {copy}    open in this terminal\n";

    static readonly SessionStart Start = new(12, "#12 Export the data", "Do the task.", new ModelChoice("opus"), "high");

    readonly DirectoryInfo folder = Directory.CreateTempSubdirectory("toobusy-claude-");
    readonly FakeClaude claude = new();
    readonly StepClock clock = new();
    readonly ClaudeFolders folders;

    public ClaudeCodeTests()
    {
        var local = folder.CreateSubdirectory("local").FullName;
        folders = new ClaudeFolders(local, () => MakeLocal(local), folder.CreateSubdirectory("transcripts").FullName);
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
        Assert.Equal(new SessionTrace(Id, null, clock.Now), session.Trace);

        using var settings = JsonDocument.Parse(arguments[6]);
        var hook = settings.RootElement.GetProperty("hooks").GetProperty("PostToolUse")[0];
        Assert.Equal("*", hook.GetProperty("matcher").GetString());
        Assert.Equal("command", hook.GetProperty("hooks")[0].GetProperty("type").GetString());
        Assert.Equal($"cat '{folders.Request}' 2>/dev/null || true", hook.GetProperty("hooks")[0].GetProperty("command").GetString());
        Assert.Equal($"cat > '{folders.Usage}.tmp' && mv '{folders.Usage}.tmp' '{folders.Usage}'", settings.RootElement.GetProperty("statusLine").GetProperty("command").GetString());
        Assert.Equal("EnterWorktree", settings.RootElement.GetProperty("permissions").GetProperty("deny")[0].GetString());
        Assert.Equal("none", settings.RootElement.GetProperty("worktree").GetProperty("bgIsolation").GetString());
    }

    // The mode of the settings is what a session is started in, argument for argument as in any other mode.
    [Fact]
    public async Task ASessionIsStartedInThePermissionModeOfTheSettings()
    {
        await Assistant("dontAsk").StartAsync(Start, TestContext.Current.CancellationToken);

        var arguments = Assert.Single(claude.Asked);
        Assert.Equal(["--bg", "--permission-mode", "dontAsk", "--permission-prompts", "none", "--settings"], arguments.Take(6));
        Assert.Equal(["--model", "opus", "--effort", "high", "-n", "#12 Export the data", "Do the task."], arguments.Skip(7));
    }

    // A session that Claude Code lists keeps the options it was started with, and goes on with them: given any
    // again, Claude Code would start a copy instead.
    [Fact]
    public async Task ASessionThatIsListedGoesOnWithTheOptionsItWasStartedWith()
    {
        Write(Said("I was stopped by the limit.\nTOOBUSY: interrupted"));
        claude.Sessions = Listing(Entry(Id, Conversation, "stopped"));
        var session = await Assistant("dontAsk").FindAsync(new SessionTrace(Id, Conversation, clock.Now), TestContext.Current.CancellationToken);
        Assert.NotNull(session);

        Assert.Equal(Telling.WentOn, await session.TellAsync("The limit has reset.", TestContext.Current.CancellationToken));

        Assert.Equal(["--bg", "--resume", Conversation, "The limit has reset."], claude.Asked[^1]);
    }

    // A session that Claude Code does not list any more has no options kept: it is given the mode of the settings as
    // they are now, and the settings of a session, once more.
    [Fact]
    public async Task ASessionThatIsNotListedAnyMoreIsGivenTheOptionsOfASessionAgain()
    {
        Write(Said("I was stopped by the limit.\nTOOBUSY: interrupted"));
        var session = await Assistant("dontAsk").FindAsync(new SessionTrace(Id, Conversation, clock.Now), TestContext.Current.CancellationToken);
        Assert.NotNull(session);

        Assert.Equal(Telling.WentOn, await session.TellAsync("The limit has reset.", TestContext.Current.CancellationToken));

        var arguments = claude.Asked[^1];
        Assert.Equal(["--bg", "--resume", Conversation, "--permission-mode", "dontAsk", "--permission-prompts", "none", "--settings"], arguments.Take(8));
        Assert.Contains("\"hooks\"", arguments[8]);
        Assert.Equal(["The limit has reset."], arguments.Skip(9));
    }

    [Fact]
    public async Task ASessionThatClaudeCodeDoesNotAnswerForDoesNotGoOn()
    {
        claude.Sessions = Listing(Entry(Id, Conversation, "stopped"));
        var session = await Assistant().StartAsync(Start, TestContext.Current.CancellationToken);
        claude.Answer = _ => new ProcessResult(ProcessStatus.TimedOut, 0, "", "");

        var told = await session.TellAsync("Go on alone.", TestContext.Current.CancellationToken);

        Assert.Equal(new Telling("Claude Code did not answer in 2 min"), told);
        Assert.Equal("claude attach 15555bc6", session.Open);
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
        var odd = folders with { Local = "/work/it's here/local" };

        await new ClaudeCode(claude, clock, odd, "/work/rocket", "auto").StartAsync(Start, TestContext.Current.CancellationToken);

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
    public async Task AnAssistantThatCouldNotBeStartedSaysWhy()
    {
        claude.Answer = _ => new ProcessResult(ProcessStatus.NotStarted, 0, "", "Argument list too long");

        var refused = await Assert.ThrowsAsync<AssistantException>(() => Assistant().StartAsync(Start, TestContext.Current.CancellationToken));

        Assert.Equal("Claude Code could not be started: Argument list too long.", refused.Message);
    }

    // Claude Code loads a skill by its command at the head of the message: the message is its last argument, whole.
    [Fact]
    public async Task AMessageThatBeginsWithTheCommandOfASkillIsPassedAsItIs()
    {
        var message = "/task-auto #12 Export the data\n\nYou are doing task #12 of this project without its owner.";

        await Assistant().StartAsync(Start with { Message = message }, TestContext.Current.CancellationToken);

        var arguments = Assert.Single(claude.Asked);
        Assert.Equal("--bg", arguments[0]);
        Assert.Equal(["-n", "#12 Export the data", message], arguments.TakeLast(3));
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

    // An answer without the line of a session in the background started none, though Claude Code exited without an
    // error.
    [Theory]
    [InlineData("Usage: claude [options] [command] [prompt]\n", "Usage: claude [options] [command] [prompt]")]
    [InlineData("", "it said nothing")]
    public async Task AStartThatClaudeCodeAnswersWithoutASessionStartedNone(string said, string why)
    {
        claude.Answer = _ => Exited(said);

        var refused = await Assert.ThrowsAsync<AssistantException>(() => Assistant().StartAsync(Start, TestContext.Current.CancellationToken));

        Assert.Equal($"Claude Code did not start a session for #12 Export the data: {why}", refused.Message);
    }

    [Theory]
    [InlineData("working", "busy", null, SessionPhase.Working, null)]
    [InlineData("starting", "busy", null, SessionPhase.Working, null)]
    [InlineData("resuming", "busy", null, SessionPhase.Working, null)]
    [InlineData("blocked", "waiting", "input needed", SessionPhase.Asking, "input needed")]
    [InlineData("blocked", "waiting", null, SessionPhase.Asking, null)]
    [InlineData("done", "idle", null, SessionPhase.Ended, null)]
    [InlineData("stopped", null, null, SessionPhase.Lost, null)]
    [InlineData("failed", null, null, SessionPhase.Lost, null)]
    [InlineData("paused", null, null, SessionPhase.Working, null)]
    public async Task TheListOfTheSessionsTellsWhereASessionStands(string state, string? status, string? waitingFor, SessionPhase phase, string? asks)
    {
        var session = await Assistant().StartAsync(Start, TestContext.Current.CancellationToken);
        claude.Sessions = Listing(Entry(Id, Conversation, state, status, waitingFor), Entry("ffff0000", "another", "blocked", "waiting", "input needed"));

        var look = await session.LookAsync(TestContext.Current.CancellationToken);

        Assert.Equal((phase, asks), (look.Phase, look.Asks));
        Assert.Equal(state == "paused" ? "its session is in a state toobusy does not know: paused" : null, look.Odd);
        Assert.Equal(Conversation, session.Trace.Conversation);
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

    // A state that toobusy does not know, as a newer Claude Code may list, is no work for ever: it is said, borne for
    // a minute as a list that cannot be read is, and the session is lost after it, with what it said read as ever.
    [Fact]
    public async Task ASessionInAStateThatIsNotKnownIsSaidAndLostAfterAMinute()
    {
        var session = await Assistant().StartAsync(Start, TestContext.Current.CancellationToken);
        Write(Said("All pass.\nTOOBUSY: done"));
        claude.Sessions = Listing(Entry(Id, Conversation, "paused"));

        var first = await session.LookAsync(TestContext.Current.CancellationToken);
        Assert.Equal((SessionPhase.Working, "its session is in a state toobusy does not know: paused"), (first.Phase, first.Odd));
        clock.Pass(TimeSpan.FromSeconds(59));
        Assert.Equal(SessionPhase.Working, (await session.LookAsync(TestContext.Current.CancellationToken)).Phase);
        clock.Pass(TimeSpan.FromSeconds(1));
        var lost = await session.LookAsync(TestContext.Current.CancellationToken);
        Assert.Equal((SessionPhase.Lost, "its session is in a state toobusy does not know: paused", "All pass.\nTOOBUSY: done"), (lost.Phase, lost.Odd, lost.Reply));

        // A state that is known again says nothing odd, and one that is not known after it is borne anew.
        claude.Sessions = Listing(Entry(Id, Conversation, "working", "busy"));
        var known = await session.LookAsync(TestContext.Current.CancellationToken);
        Assert.Equal((SessionPhase.Working, null), (known.Phase, known.Odd));
        claude.Sessions = Listing(Entry(Id, Conversation, "paused"));
        Assert.Equal(SessionPhase.Working, (await session.LookAsync(TestContext.Current.CancellationToken)).Phase);

        // A list that gives no state at all is not known either.
        claude.Sessions = "[{" + $"\"id\":\"{Id}\",\"sessionId\":\"{Conversation}\"" + "}]";
        Assert.Equal("the list of the sessions gives no state for its session", (await session.LookAsync(TestContext.Current.CancellationToken)).Odd);
    }

    [Fact]
    public async Task AListThatCannotBeReadIsBorneForAMinute()
    {
        var session = await Assistant().StartAsync(Start, TestContext.Current.CancellationToken);
        claude.Sessions = Listing(Entry(Id, Conversation, "working", "busy"));
        Write(Said("On it."));
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
    [InlineData("Half of it; the rest is #34.\n\n**TOOBUSY: partial**\n", true)]
    [InlineData("Half of it; the rest is #34.\nTOOBUSY: partial\n\nShall I take #34 too?", false)]
    [InlineData("Shall I go on?", false)]
    public async Task ASessionThatIsMarkedAsWaitingHasReportedWhenItsReplyEndsWithTheLine(string reply, bool reported)
    {
        var session = await Assistant().StartAsync(Start, TestContext.Current.CancellationToken);
        claude.Sessions = Listing(Entry(Id, Conversation, "blocked", "waiting", "input needed"));
        Write(Said(reply));

        var look = await session.LookAsync(TestContext.Current.CancellationToken);

        Assert.Equal((SessionPhase.Asking, reply, reported), (look.Phase, look.Reply, look.Reported));
    }

    [Fact]
    public async Task ASessionThatAsksWithAToolAfterItsReportHasNotReported()
    {
        var session = await Assistant().StartAsync(Start, TestContext.Current.CancellationToken);
        claude.Sessions = Listing(Entry(Id, Conversation, "blocked", "waiting", "input needed"));
        Write(Said("Half of it; the rest is #34.\nTOOBUSY: partial"), Used("AskUserQuestion", """{"questions":[]}"""));

        var asks = await session.LookAsync(TestContext.Current.CancellationToken);
        Assert.Equal(("Half of it; the rest is #34.\nTOOBUSY: partial", false), (asks.Reply, asks.Reported));

        Write(Said("Half of it; the rest is #34.\nTOOBUSY: partial"));
        Assert.True((await session.LookAsync(TestContext.Current.CancellationToken)).Reported);
    }

    [Fact]
    public async Task ThePlanOfASessionIsTheListOfTasksItKeeps()
    {
        var session = await Assistant().StartAsync(Start, TestContext.Current.CancellationToken);
        claude.Sessions = Listing(Entry(Id, Conversation, "working", "busy"));
        Assert.Null((await session.LookAsync(TestContext.Current.CancellationToken)).Plan);

        // A step is known from what Claude Code answers, and one of a helper is not of this plan. What was planned
        // before the run took the session is planned still.
        Write(
            Called("c1", "TaskCreate", """{"subject":"Read the code"}""", at: "2029-12-31T23:00:00Z"),
            Answered("c1", """{"task":{"id":"1","subject":"Read the code"}}"""),
            Called("c2", "TaskCreate", """{"subject":"Write the export"}"""),
            Called("c3", "TaskCreate", """{"subject":"Run the tests"}"""),
            Answered("c2", """{"task":{"id":"2","subject":"Write the export"}}"""),
            Answered("c3", """{"task":{"id":"3","subject":"Run the tests"}}"""),
            Called("h1", "TaskCreate", """{"subject":"Of a helper"}""", extra: ""","isSidechain":true"""),
            Answered("h1", """{"task":{"id":"9","subject":"Of a helper"}}""", extra: ""","isSidechain":true"""),
            Answered("x1", """{"task":{"id":"7","subject":"Not asked for"}}"""),
            Called("c4", "TaskUpdate", """{"taskId":"1","status":"completed"}"""),
            Answered("c4", """{"success":true,"taskId":"1","updatedFields":["status"],"statusChange":{"from":"pending","to":"completed"}}"""),
            Used("Edit", """{"file_path":"/work/rocket/src/a.cs"}"""),
            Called("c5", "TaskUpdate", """{"taskId":"2","status":"in_progress"}"""),
            Answered("c5", """{"success":true,"taskId":"2","updatedFields":["status"],"statusChange":{"from":"pending","to":"in_progress"}}"""));

        var look = await session.LookAsync(TestContext.Current.CancellationToken);

        Assert.Equal([PlanStep.Done, PlanStep.Active, PlanStep.Pending], look.Plan);

        // Keeping the plan is not what the session is doing, though it is a step of its own.
        Assert.Equal("Edit src/a.cs", look.Step);
        Assert.Equal(5, look.Steps);

        // The list changes: a step is added, one is taken out, and one that was renamed is as far as it was.
        Write(
            Called("c6", "TaskCreate", """{"subject":"Describe it"}"""),
            Answered("c6", """{"task":{"id":"4","subject":"Describe it"}}"""),
            Called("c7", "TaskUpdate", """{"taskId":"3","status":"deleted"}"""),
            Answered("c7", """{"success":true,"taskId":"3","updatedFields":["status"],"statusChange":{"from":"pending","to":"deleted"}}"""),
            Called("c8", "TaskUpdate", """{"taskId":"2","subject":"Write the export as CSV"}"""),
            Answered("c8", """{"success":true,"taskId":"2","updatedFields":["subject"]}"""));

        Assert.Equal([PlanStep.Done, PlanStep.Active, PlanStep.Pending], (await session.LookAsync(TestContext.Current.CancellationToken)).Plan);

        Write(
            Called("c9", "TaskUpdate", """{"taskId":"4","status":"deleted"}"""),
            Answered("c9", """{"success":true,"taskId":"4","updatedFields":["status"],"statusChange":{"from":"pending","to":"deleted"}}"""),
            Called("c10", "TaskUpdate", """{"taskId":"2","status":"completed"}"""),
            Answered("c10", """{"success":true,"taskId":"2","updatedFields":["status"],"statusChange":{"from":"in_progress","to":"completed"}}"""));

        Assert.Equal([PlanStep.Done, PlanStep.Done], (await session.LookAsync(TestContext.Current.CancellationToken)).Plan);
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

    // The line is cut in the middle of a character, which is read whole all the same.
    [Fact]
    public async Task ALineThatIsHalfWrittenWaitsForItsEnd()
    {
        var session = await Assistant().StartAsync(Start, TestContext.Current.CancellationToken);
        claude.Sessions = Listing(Entry(Id, Conversation, "working", "busy"));
        var line = Encoding.UTF8.GetBytes(Said("Did it … all of it.") + "\n");
        var cut = line.AsSpan().LastIndexOf("…"u8) + 1;
        File.AppendAllBytes(folders.Transcript(Conversation), line[..cut]);

        Assert.Null((await session.LookAsync(TestContext.Current.CancellationToken)).Reply);

        File.AppendAllBytes(folders.Transcript(Conversation), line[cut..]);

        Assert.Equal("Did it … all of it.", (await session.LookAsync(TestContext.Current.CancellationToken)).Reply);
        Assert.Equal("Did it … all of it.", (await session.LookAsync(TestContext.Current.CancellationToken)).Reply);
    }

    // Claude Code may write on while the conversation is read: what it writes then is read the next time.
    [Fact]
    public void WhatIsWrittenWhileTheConversationIsReadIsReadTheNextTime()
    {
        var (before, during) = (Said("Working on it.") + "\n", Said("Did it.") + "\n");
        var reads = 0;
        var transcript = new Transcript(() => reads++ == 0 ? new WrittenOn(before, during) : new MemoryStream(Encoding.UTF8.GetBytes(before + during)), clock.Now, "/work/rocket");

        transcript.Read();
        Assert.Equal("Working on it.", transcript.Reply);

        transcript.Read();
        Assert.Equal("Did it.", transcript.Reply);
    }

    // A conversation that is shorter than what was read of it was written anew: it is read from its start, and what
    // was half written before is gone.
    [Fact]
    public async Task AConversationThatShrankIsReadFromItsStart()
    {
        var session = await Assistant().StartAsync(Start, TestContext.Current.CancellationToken);
        claude.Sessions = Listing(Entry(Id, Conversation, "working", "busy"));
        File.AppendAllText(folders.Transcript(Conversation), Said("Working on it, step by step.") + "\n" + Said("Half")[..40]);
        Assert.Equal("Working on it, step by step.", (await session.LookAsync(TestContext.Current.CancellationToken)).Reply);

        File.WriteAllText(folders.Transcript(Conversation), Said("On it.") + "\n");

        Assert.Equal("On it.", (await session.LookAsync(TestContext.Current.CancellationToken)).Reply);
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

        Assert.Equal(Telling.WentOn, told);
        Assert.Equal(
            [["agents", "--json", "--all"], ["stop", Id], ["agents", "--json", "--all"], ["agents", "--json", "--all"], ["--bg", "--resume", Conversation, "Go on alone."]],
            claude.Asked.Skip(1));
        Assert.Equal(TimeSpan.FromMilliseconds(500), clock.Passed);
        Assert.Equal(Conversation, session.Trace.Conversation);
    }

    // Claude Code may start a copy of the session all the same, and says so: a copy of a session that nothing has,
    // by the list, is the session from then on, with the conversation of its own and nothing of what the one before
    // said.
    // The list of the sessions starts Claude Code: a look takes it from the last one read while that is younger than
    // the time it is kept, and reads it anew after that.
    [Fact]
    public async Task ALookTakesTheListOfTheSessionsReadLessThanTwentySecondsAgo()
    {
        claude.Sessions = Listing(Entry(Id, Conversation, "working", "running"));
        var session = await Assistant(listAge: TimeSpan.FromSeconds(20)).StartAsync(Start, TestContext.Current.CancellationToken);

        await session.LookAsync(TestContext.Current.CancellationToken);
        clock.Pass(TimeSpan.FromSeconds(10));
        await session.LookAsync(TestContext.Current.CancellationToken);
        clock.Pass(TimeSpan.FromSeconds(10));
        await session.LookAsync(TestContext.Current.CancellationToken);

        Assert.Equal(2, Lists());
    }

    [Fact]
    public async Task ALookAfterASessionWasToldOrStoppedReadsTheListAnew()
    {
        claude.Sessions = Listing(Entry(Id, Conversation, "stopped"));
        claude.Answer = arguments => Exited(arguments[0] == "--bg" && arguments.Contains("--resume") ? "backgrounded · 15555bc6\n" : arguments[0] == "--bg" ? Backgrounded : "");
        var session = await Assistant(listAge: TimeSpan.FromSeconds(20)).StartAsync(Start, TestContext.Current.CancellationToken);
        await session.LookAsync(TestContext.Current.CancellationToken);

        await session.TellAsync("Go on alone.", TestContext.Current.CancellationToken);
        var told = Lists();
        await session.LookAsync(TestContext.Current.CancellationToken);
        await session.StopAsync(TestContext.Current.CancellationToken);
        await session.LookAsync(TestContext.Current.CancellationToken);

        Assert.Equal(told + 2, Lists());
    }

    [Fact]
    public async Task AListThatCannotBeReadIsNotKept()
    {
        claude.List = new ProcessResult(ProcessStatus.Exited, 1, "", "");
        var session = await Assistant(listAge: TimeSpan.FromSeconds(20)).StartAsync(Start, TestContext.Current.CancellationToken);

        await session.LookAsync(TestContext.Current.CancellationToken);
        await session.LookAsync(TestContext.Current.CancellationToken);

        Assert.Equal(2, Lists());
    }

    [Fact]
    public async Task ACopyOfASessionThatNothingHasIsTheSessionFromThenOn()
    {
        var session = await Assistant().StartAsync(Start, TestContext.Current.CancellationToken);
        Write(Said("What I said before."));
        claude.Listings.Enqueue(Listing(Entry(Id, Conversation, "stopped")));
        claude.Sessions = Listing(Entry(Id, Conversation, "stopped"), Entry(Copy, CopyConversation, "working", "running"));
        claude.Answer = arguments => Exited(arguments[0] == "--bg" ? CopyOf(Copy) : "");
        File.WriteAllText(folders.Transcript(CopyConversation), Said("On it again.") + "\n");

        var told = await session.TellAsync("Go on alone.", TestContext.Current.CancellationToken);
        var look = await session.LookAsync(TestContext.Current.CancellationToken);

        Assert.Equal(Telling.Copied, told);
        Assert.Equal("claude attach aaaa1111", session.Open);
        Assert.Equal(new SessionTrace(Copy, CopyConversation, clock.Now), session.Trace);
        Assert.DoesNotContain(claude.Asked, arguments => arguments[0] is "stop" or "rm");
        Assert.Equal((SessionPhase.Working, "On it again."), (look.Phase, look.Reply));
    }

    [Fact]
    public async Task ACopyThatIsNotListedYetIsTheSessionAllTheSame()
    {
        var session = await Assistant().StartAsync(Start, TestContext.Current.CancellationToken);
        claude.Sessions = Listing(Entry(Id, Conversation, "stopped"));
        claude.Answer = arguments => Exited(arguments[0] == "--bg" ? CopyOf(Copy) : "");

        var told = await session.TellAsync("Go on alone.", TestContext.Current.CancellationToken);

        Assert.Equal(Telling.Copied, told);
        Assert.Equal(new SessionTrace(Copy, null, clock.Now), session.Trace);
        Assert.Equal("claude attach aaaa1111", session.Open);
    }

    [Fact]
    public async Task ACopyOfASessionThatSomethingStillHasIsStoppedAndTheSessionIsTriedAgain()
    {
        var session = await Assistant().StartAsync(Start, TestContext.Current.CancellationToken);
        claude.Sessions = Listing(Entry(Id, Conversation, "working", "running"));
        string[] copies = ["aaaa1111", "bbbb2222", "cccc3333"];
        var resumes = 0;
        claude.Answer = arguments => Exited(arguments[0] == "--bg" ? CopyOf(copies[resumes++]) : "");

        var told = await session.TellAsync("Go on alone.", TestContext.Current.CancellationToken);

        Assert.Equal(new Telling("Claude Code started a copy of the session 3 times: something still has it"), told);
        Assert.Equal(3, resumes);
        foreach (var copy in copies)
        {
            Assert.Contains(claude.Asked, arguments => arguments.SequenceEqual(["stop", copy]));
            Assert.Contains(claude.Asked, arguments => arguments.SequenceEqual(["rm", copy]));
        }

        Assert.Equal("claude attach 15555bc6", session.Open);
        Assert.Equal(new SessionTrace(Id, Conversation, clock.Now - clock.Passed), session.Trace);
    }

    // A session that the list still has as working after it was stopped is waited for twenty seconds, and is gone on
    // with after that all the same: where something still has it, Claude Code says so with a copy.
    [Fact]
    public async Task ASessionThatDoesNotStopIsWaitedForAWhileAndGoneOnWithAllTheSame()
    {
        var session = await Assistant().StartAsync(Start, TestContext.Current.CancellationToken);
        claude.Sessions = Listing(Entry(Id, Conversation, "working", "busy"));
        claude.Answer = arguments => Exited(arguments[0] == "stop" ? "" : Backgrounded);

        var told = await session.TellAsync("Go on alone.", TestContext.Current.CancellationToken);

        Assert.Equal(Telling.WentOn, told);
        Assert.Equal(TimeSpan.FromSeconds(20), clock.Passed);
        Assert.Equal([["stop", Id]], claude.Asked.Where(arguments => arguments[0] == "stop"));
        Assert.Equal(["--bg", "--resume", Conversation, "Go on alone."], claude.Asked[^1]);
    }

    // A session that is stopped is waited for until the list has it as one that no process has.
    [Fact]
    public async Task AStoppedSessionIsWaitedForUntilNothingHasIt()
    {
        var session = await Assistant().StartAsync(Start, TestContext.Current.CancellationToken);
        claude.Listings.Enqueue(Listing(Entry(Id, Conversation, "working", "busy")));
        claude.Sessions = Listing(Entry(Id, Conversation, "stopped"));
        claude.Answer = _ => Exited($"stopped {Id}\n");

        await session.StopAsync(TestContext.Current.CancellationToken);

        Assert.Equal([["stop", Id], ["agents", "--json", "--all"], ["agents", "--json", "--all"]], claude.Asked.Skip(1));
        Assert.Equal(TimeSpan.FromMilliseconds(500), clock.Passed);
    }

    // A session that the list has as working twenty seconds after it was stopped is said: it may work on in the
    // working copy with nobody watching. A list that cannot be read does not say so.
    [Fact]
    public async Task ASessionThatDoesNotStopIsSaid()
    {
        var session = await Assistant().StartAsync(Start, TestContext.Current.CancellationToken);
        claude.Sessions = Listing(Entry(Id, Conversation, "working", "busy"));
        claude.Answer = _ => Exited("");

        var working = await Assert.ThrowsAsync<AssistantException>(() => session.StopAsync(TestContext.Current.CancellationToken));

        Assert.Equal("Claude Code still lists the session 15555bc6 as working 20 s after it was stopped", working.Message);
        Assert.Equal(TimeSpan.FromSeconds(20), clock.Passed);

        claude.List = new ProcessResult(ProcessStatus.Exited, 1, "", "");
        await session.StopAsync(TestContext.Current.CancellationToken);
        Assert.Equal(TimeSpan.FromSeconds(20), clock.Passed);
    }

    // A list that cannot be read does not say that nothing has the session: the copy is not taken on its word.
    [Fact]
    public async Task AListThatCannotBeReadAfterACopyMeansATryAgain()
    {
        var session = await Assistant().StartAsync(Start, TestContext.Current.CancellationToken);
        claude.Sessions = Listing(Entry(Id, Conversation, "stopped"));
        claude.Answer = arguments =>
        {
            if (arguments[0] == "--bg")
                claude.List = new ProcessResult(ProcessStatus.Exited, 1, "", "");
            return Exited(arguments[0] == "--bg" ? CopyOf(Copy) : "");
        };

        var told = await session.TellAsync("Go on alone.", TestContext.Current.CancellationToken);

        Assert.Equal(new Telling("Claude Code started a copy of the session 3 times: something still has it"), told);
        Assert.Equal(3, claude.Asked.Count(arguments => arguments.SequenceEqual(["rm", Copy])));
        Assert.Equal("claude attach 15555bc6", session.Open);
    }

    [Fact]
    public async Task ASessionThatDoesNotGoOnSaysWhatClaudeCodeSaid()
    {
        var session = await Assistant().StartAsync(Start, TestContext.Current.CancellationToken);
        claude.Sessions = Listing(Entry(Id, Conversation, "stopped"));
        claude.Answer = _ => new ProcessResult(ProcessStatus.Exited, 1, "", "error: no conversation found\nmore of it");

        Assert.Equal(new Telling("error: no conversation found"), await session.TellAsync("Go on alone.", TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task ASessionThatClaudeCodeCouldNotBeStartedForDoesNotGoOnAndSaysWhy()
    {
        var session = await Assistant().StartAsync(Start, TestContext.Current.CancellationToken);
        claude.Sessions = Listing(Entry(Id, Conversation, "stopped"));
        claude.Answer = _ => new ProcessResult(ProcessStatus.NotStarted, 0, "", "Permission denied");

        Assert.Equal(new Telling("Claude Code could not be started: Permission denied"), await session.TellAsync("Go on alone.", TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task ASessionWhoseConversationIsNotKnownCannotBeTold()
    {
        var session = await Assistant().StartAsync(Start, TestContext.Current.CancellationToken);

        Assert.Equal(new Telling("its conversation is not known"), await session.TellAsync("Go on alone.", TestContext.Current.CancellationToken));
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
        Assert.Equal(["stop", Id], claude.Asked[^2]);
    }

    // A session or a script of the project may take the local folder away while the run goes on, as `git clean -fdx`
    // does: the request puts it back as the run keeps it, out of git.
    [Fact]
    public async Task ARequestPutsTheLocalFolderBackAsTheRunKeepsItWhenItWasTakenAway()
    {
        var session = await Assistant().StartAsync(Start, TestContext.Current.CancellationToken);
        await session.AskAsync("Wrap up.", TestContext.Current.CancellationToken);

        Directory.Delete(folders.Local, recursive: true);
        await session.AskAsync("Wrap up now.", TestContext.Current.CancellationToken);

        Assert.Equal("*\n", File.ReadAllText(Path.Combine(folders.Local, ".gitignore")));
        using var request = JsonDocument.Parse(File.ReadAllText(folders.Request));
        Assert.Equal("Wrap up now.", request.RootElement.GetProperty("hookSpecificOutput").GetProperty("additionalContext").GetString());
    }

    [Fact]
    public async Task ARequestThatCannotBeWrittenIsAFailureOfTheLocalFiles()
    {
        var session = await Assistant().StartAsync(Start, TestContext.Current.CancellationToken);

        // A folder stands where the request should be.
        Directory.CreateDirectory(Path.Combine(folders.Request, "inside"));

        var unwritten = await Assert.ThrowsAsync<LocalStateException>(() => session.AskAsync("Wrap up.", TestContext.Current.CancellationToken));
        Assert.StartsWith("the request for the session cannot be written: ", unwritten.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ASessionOfAnEarlierRunThatStillWorksIsFoundAndReadFromWhereItWasStarted()
    {
        var since = clock.Now;
        Write(Said("An old one.", at: "2029-12-31T23:00:00Z"), Used("Bash", """{"command":"dotnet test"}"""), Said("All pass.\nTOOBUSY: done"));
        claude.Sessions = Listing(Entry(Id, Conversation, "done", "idle"));
        clock.Pass(TimeSpan.FromHours(3));

        // The conversation was not known yet when the run before was stopped: the list has it.
        var session = await Assistant().FindAsync(new SessionTrace(Id, null, since), TestContext.Current.CancellationToken);

        Assert.NotNull(session);
        Assert.Equal(("claude attach 15555bc6", new SessionTrace(Id, Conversation, since)), (session.Open, session.Trace));
        var look = await session.LookAsync(TestContext.Current.CancellationToken);
        Assert.Equal((SessionPhase.Ended, "All pass.\nTOOBUSY: done", 1), (look.Phase, look.Reply, look.Steps));
        Assert.All(claude.Asked, arguments => Assert.Equal("agents", arguments[0]));
    }

    [Fact]
    public async Task ASessionOfAnEarlierRunThatIsNotListedAnyMoreIsLostAtOnce()
    {
        Write(Said("I was about to."));

        var session = await Assistant().FindAsync(new SessionTrace(Id, Conversation, clock.Now), TestContext.Current.CancellationToken);

        Assert.NotNull(session);
        Assert.Equal(SessionPhase.Lost, (await session.LookAsync(TestContext.Current.CancellationToken)).Phase);
    }

    [Fact]
    public async Task ASessionOfAnEarlierRunIsNotKnownWhileTheListCannotBeRead()
    {
        claude.List = new ProcessResult(ProcessStatus.TimedOut, 0, "", "");

        var session = await Assistant().FindAsync(new SessionTrace(Id, Conversation, clock.Now), TestContext.Current.CancellationToken);

        Assert.NotNull(session);
        Assert.Equal(SessionPhase.Unseen, (await session.LookAsync(TestContext.Current.CancellationToken)).Phase);
    }

    [Fact]
    public async Task ASessionOfAnEarlierRunGoesOnByItsConversation()
    {
        var since = clock.Now;
        File.WriteAllText(folders.Request, "{}");
        Write(Said("I was stopped by the limit.\nTOOBUSY: interrupted"));
        claude.Sessions = Listing(Entry(Id, Conversation, "stopped"));
        clock.Pass(TimeSpan.FromHours(3));

        var session = await Assistant().FindAsync(new SessionTrace(Id, Conversation, since), TestContext.Current.CancellationToken);
        Assert.NotNull(session);
        var before = await session.LookAsync(TestContext.Current.CancellationToken);
        var told = await session.TellAsync("The limit has reset.", TestContext.Current.CancellationToken);

        Assert.Equal((SessionPhase.Lost, "I was stopped by the limit.\nTOOBUSY: interrupted", true), (before.Phase, before.Reply, before.Reported));
        Assert.Equal(Telling.WentOn, told);
        Assert.Equal(["--bg", "--resume", Conversation, "The limit has reset."], claude.Asked[^1]);
        Assert.Equal(new SessionTrace(Id, Conversation, clock.Now), session.Trace);

        // What it said before it was told does not count any more, and a request that waited for it is forgotten.
        var after = await session.LookAsync(TestContext.Current.CancellationToken);
        Assert.Equal((null, false), (after.Reply, after.Reported));
        Assert.False(File.Exists(folders.Request));

        Write(Said("On it again.", at: "2030-01-01T12:00:05Z"));
        Assert.Equal("On it again.", (await session.LookAsync(TestContext.Current.CancellationToken)).Reply);
    }

    [Fact]
    public async Task NothingIsLeftOfASessionThatNoProcessHasAndWhoseConversationIsGone()
    {
        var trace = new SessionTrace(Id, Conversation, clock.Now);

        // Not listed, and no conversation on this machine.
        Assert.Null(await Assistant().FindAsync(trace, TestContext.Current.CancellationToken));

        // Listed as one that was stopped, with its conversation deleted.
        claude.Sessions = Listing(Entry(Id, Conversation, "stopped"));
        Assert.Null(await Assistant().FindAsync(trace, TestContext.Current.CancellationToken));

        // Neither its name nor its conversation was ever known.
        Assert.Null(await Assistant().FindAsync(new SessionTrace(null, null, clock.Now), TestContext.Current.CancellationToken));

        // A session that a process has is there, whatever is written of it yet.
        claude.Sessions = Listing(Entry(Id, Conversation, "working", "busy"));
        Assert.NotNull(await Assistant().FindAsync(trace with { Conversation = null }, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task TheSessionOfAStartThatWasCutOffIsFoundByItsNameFromWhenTheStartWasMade()
    {
        var since = clock.Now;
        var after = since.ToUnixTimeMilliseconds();
        claude.Sessions = Listing(
            Entry("aaaa1111", "conversation-old", "stopped", startedAt: after - 60_000),
            Entry("bbbb2222", "conversation-other", "working", "busy", startedAt: after + 300, name: "#13 Import the data"),
            Entry("cccc3333", "conversation-new", "working", "busy", startedAt: after + 500));

        var session = await Assistant().FindStartedAsync(Start, since, TestContext.Current.CancellationToken);

        // An earlier session of the same task, started before, is not the one, and neither is another task's.
        Assert.NotNull(session);
        Assert.Equal("claude attach cccc3333", session.Open);
        claude.Answer = _ =>
        {
            claude.Sessions = Listing(Entry("cccc3333", "conversation-new", "stopped", startedAt: after + 500));
            return Exited("stopped cccc3333\n");
        };
        await session.StopAsync(TestContext.Current.CancellationToken);
        Assert.Equal(["stop", "cccc3333"], claude.Asked[^2]);
    }

    [Fact]
    public async Task TheSessionOfAStartThatWasCutOffIsLookedForAFewTimes()
    {
        var since = clock.Now;
        claude.Listings.Enqueue(Listing());
        claude.Sessions = Listing(Entry(Id, Conversation, "working", "busy", startedAt: since.ToUnixTimeMilliseconds()));

        var session = await Assistant().FindStartedAsync(Start, since, TestContext.Current.CancellationToken);

        Assert.Equal("claude attach 15555bc6", session?.Open);
        Assert.Equal(2, claude.Asked.Count);
    }

    [Fact]
    public async Task AStartThatWasCutOffBeforeClaudeCodeHadItLeftNoSession()
    {
        claude.Sessions = Listing(Entry(Id, Conversation, "stopped"));

        Assert.Null(await Assistant().FindStartedAsync(Start, clock.Now, TestContext.Current.CancellationToken));
        Assert.Equal(3, claude.Asked.Count);
        Assert.Equal(TimeSpan.FromSeconds(2), clock.Passed);

        // A list that cannot be read shows no session either.
        claude.List = new ProcessResult(ProcessStatus.TimedOut, 0, "", "");
        Assert.Null(await Assistant().FindStartedAsync(Start, clock.Now, TestContext.Current.CancellationToken));
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
    [InlineData("null")]
    [InlineData("\"2026-10-11T12:00:00Z\"")]
    [InlineData("1791675600.5")]
    [InlineData("99999999999999999")]
    [InlineData("{}")]
    public void AResetThatIsNotATimeLeavesTheLimitWithoutOne(string reset)
    {
        File.WriteAllText(folders.Usage, """{"rate_limits":{"five_hour":{"used_percentage":97,"resets_at":RESET}}}""".Replace("RESET", reset, StringComparison.Ordinal));

        var limits = Assistant().ReadLimits();
        Assert.Equal(new UsageWindow("5-hour", 97, null), limits.Near);
        Assert.Null(limits.Far);
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
        var plain = ClaudeFolders.Of("/Users/ann/My Projects/rocket_2", "/local", () => { }, "/Users/ann", _ => null);
        var moved = ClaudeFolders.Of("/work/rocket", "/local", () => { }, "/Users/ann", name => name == "CLAUDE_CONFIG_DIR" ? "/etc/claude" : null);

        Assert.Equal(Path.Combine("/Users/ann", ".claude", "projects", "-Users-ann-My-Projects-rocket-2"), plain.Transcripts);
        Assert.Equal(Path.Combine("/Users/ann", ".claude", "projects"), plain.Projects);
        Assert.Equal(Path.Combine("/etc/claude", "projects", "-work-rocket"), moved.Transcripts);
        Assert.Equal(Path.Combine(plain.Transcripts, "abc.jsonl"), plain.Transcript("abc"));
        Assert.Equal((Path.Combine("/local", "wrap-up.json"), Path.Combine("/local", "usage.json")), (plain.Request, plain.Usage));
    }

    // Claude Code names the folder of a long path otherwise than by its rule: a conversation that is not where the
    // rule says is found in another folder of its projects, for a look and for a session of an earlier run alike.
    [Fact]
    public async Task AConversationInAFolderWithAnotherNameIsFound()
    {
        var elsewhere = folder.CreateSubdirectory("-work-rocket-1a2b3c").FullName;
        File.WriteAllText(Path.Combine(elsewhere, Conversation + ".jsonl"), Said("All pass.\nTOOBUSY: done") + "\n");
        claude.Sessions = Listing(Entry(Id, Conversation, "done", "idle"));
        var session = await Assistant().StartAsync(Start, TestContext.Current.CancellationToken);

        var look = await session.LookAsync(TestContext.Current.CancellationToken);

        Assert.Equal((SessionPhase.Ended, "All pass.\nTOOBUSY: done"), (look.Phase, look.Reply));
        claude.Sessions = Listing(Entry(Id, Conversation, "stopped"));
        Assert.NotNull(await Assistant().FindAsync(new SessionTrace(Id, Conversation, clock.Now), TestContext.Current.CancellationToken));
    }

    // A session that Claude Code lists has its conversation on the machine. One that is in no folder of its projects
    // is borne for a minute, as one that is not written yet, and then said: nothing the session does can be read.
    [Fact]
    public async Task AConversationThatIsNowhereIsSaidAfterAMinute()
    {
        var session = await Assistant().StartAsync(Start, TestContext.Current.CancellationToken);
        claude.Sessions = Listing(Entry(Id, Conversation, "done", "idle"));

        Assert.Equal(SessionPhase.Ended, (await session.LookAsync(TestContext.Current.CancellationToken)).Phase);
        clock.Pass(TimeSpan.FromSeconds(59));
        await session.LookAsync(TestContext.Current.CancellationToken);
        clock.Pass(TimeSpan.FromSeconds(1));
        var unfound = await Assert.ThrowsAsync<AssistantException>(() => session.LookAsync(TestContext.Current.CancellationToken));

        Assert.Equal(
            $"the conversation of the session 15555bc6 is not {folders.Transcript(Conversation)}, nor in any other folder of {folder.FullName}: toobusy cannot read what the session does",
            unfound.Message);

        // One that is written after all is read.
        Write(Said("All pass.\nTOOBUSY: done"));
        Assert.Equal("All pass.\nTOOBUSY: done", (await session.LookAsync(TestContext.Current.CancellationToken)).Reply);
    }

    // The local folder as a run makes it: with a `.gitignore` of its own that leaves everything in it out of git.
    static void MakeLocal(string local)
    {
        Directory.CreateDirectory(local);
        if (!File.Exists(Path.Combine(local, ".gitignore")))
            File.WriteAllText(Path.Combine(local, ".gitignore"), "*\n");
    }

    // An assistant that reads the list of the sessions anew at every look, unless a test gives it a time to keep it.
    ClaudeCode Assistant(string permissionMode = "auto", TimeSpan? listAge = null) => new(claude, clock, folders, "/work/rocket", permissionMode, listAge ?? TimeSpan.Zero);

    void Write(params string[] lines) => File.AppendAllText(folders.Transcript(Conversation), string.Join('\n', lines) + "\n");

    static ProcessResult Exited(string output) => new(ProcessStatus.Exited, 0, output, "");

    // How many times the list of the sessions was read.
    int Lists() => claude.Asked.Count(arguments => arguments[0] == "agents");

    static string Listing(params string[] entries) => "[" + string.Join(',', entries) + "]";

    // A session as the list of Claude Code has it: one that a process has says so with its `pid` and its `status`.
    static string Entry(string id, string conversation, string state, string? status = null, string? waitingFor = null, long startedAt = 1791661881643, string name = "#12 Export the data") =>
        "{" + $"\"id\":\"{id}\",\"cwd\":\"/work/rocket\",\"kind\":\"background\",\"startedAt\":{startedAt},\"sessionId\":\"{conversation}\",\"name\":\"{name}\",\"state\":\"{state}\""
        + (status is null ? "" : $",\"pid\":56109,\"status\":\"{status}\"")
        + (waitingFor is null ? "" : $",\"waitingFor\":\"{waitingFor}\"")
        + "}";

    // Claude Code writes a character that is not ASCII as it is, not as an escape.
    static readonly JsonSerializerOptions AsWritten = new() { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    static string Said(string text, string at = "2030-01-01T09:00:05Z", string model = "claude-opus", string extra = "", int tokens = 10) =>
        "{" + $"\"type\":\"assistant\",\"timestamp\":\"{at}\"{extra},\"message\":" + "{" + $"\"model\":\"{model}\",\"content\":["
        + "{\"type\":\"thinking\",\"thinking\":\"…\"},{\"type\":\"text\",\"text\":" + JsonSerializer.Serialize(text, AsWritten) + "}],"
        + "\"usage\":{" + $"\"input_tokens\":{tokens},\"cache_read_input_tokens\":20000,\"cache_creation_input_tokens\":1500" + "}}}";

    // A call of a tool that is known by a name, and what Claude Code answered to it.
    static string Called(string id, string tool, string input, string at = "2030-01-01T09:00:03Z", string extra = "") =>
        "{" + $"\"type\":\"assistant\",\"timestamp\":\"{at}\"{extra},\"message\":" + "{\"model\":\"claude-opus\",\"content\":[{\"type\":\"tool_use\",\"id\":\"" + id + "\",\"name\":\"" + tool + "\",\"input\":" + input + "}]}}";

    static string Answered(string id, string result, string extra = "") =>
        "{" + $"\"type\":\"user\",\"timestamp\":\"2030-01-01T09:00:04Z\"{extra},\"message\":" + "{\"role\":\"user\",\"content\":[{\"type\":\"tool_result\",\"tool_use_id\":\"" + id + "\",\"content\":\"ok\"}]},\"toolUseResult\":" + result + "}";

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

    // What any other command answers; one the test gives no answer for fails it, with the command named.
    public Func<IReadOnlyList<string>, ProcessResult> Answer { get; set; } = arguments => throw new InvalidOperationException($"The test has no answer for claude {string.Join(' ', arguments)}");

    public Task<ProcessResult> RunAsync(string command, IReadOnlyList<string> arguments, TimeSpan timeout, CancellationToken cancellationToken)
    {
        Assert.Equal("claude", command);
        Asked.Add(arguments);
        return Task.FromResult(arguments[0] != "agents" ? Answer(arguments)
            : List ?? new ProcessResult(ProcessStatus.Exited, 0, Listings.Count > 0 ? Listings.Dequeue() : Sessions, ""));
    }

    public IProcessRunner Inside(string folder) => this;
}

// A conversation that Claude Code writes on while it is read: what it writes is added once a read has come to the end
// of what was written before. Whatever way the stream is read, it is read through here.
sealed class WrittenOn : MemoryStream
{
    readonly byte[] during;
    bool written;

    public WrittenOn(string before, string during)
    {
        Write(Encoding.UTF8.GetBytes(before));
        Position = 0;
        this.during = Encoding.UTF8.GetBytes(during);
    }

    public override int Read(byte[] buffer, int offset, int count)
    {
        var read = base.Read(buffer, offset, count);
        if (read == 0 && !written)
        {
            written = true;
            var reached = Position;
            Write(during);
            Position = reached;
        }

        return read;
    }
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
