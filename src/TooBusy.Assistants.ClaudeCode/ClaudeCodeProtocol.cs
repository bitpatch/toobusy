using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using TooBusy.Core.Run;

namespace TooBusy.Assistants.ClaudeCode;

// What toobusy recognises in what Claude Code prints and writes, in one place, with the version of Claude Code it was
// read against: the words it answers a start and a resume with, the states of its list of the sessions, where it keeps
// the conversations of a project, and what a line of a conversation holds that is Claude Code's own and not the
// Messages API's: a refusal of the service, what it says in the name of the assistant, the tools of the plan and their
// results, the tools that ask. Nothing tells toobusy that a newer Claude Code prints otherwise, so the doctor warns of
// one. The names of the fields of its JSON are read where that JSON is read.
public static partial class ClaudeCodeProtocol
{
    // The version of Claude Code that all of this was read against.
    public const string Version = "2.1.303";

    // Whether the version Claude Code tells, as `claude --version` prints it, `2.1.303 (Claude Code)`, is newer than
    // the one all of this was read against. A version that cannot be read is not.
    public static bool Newer(string said) =>
        Numbered().Match(said) is { Success: true } match && System.Version.TryParse(match.Value, out var told) && told > System.Version.Parse(Version);

    // The name a session that went to the background is known by from then on, as Claude Code answers a start and a
    // resume with it; null when it does not.
    public static string? Started(string said) => Backgrounded().Match(said) is { Success: true } match ? match.Groups[1].Value : null;

    // Whether Claude Code answered that it does not trust the folder.
    public static bool Untrusted(string said) => said.Contains("not trusted", StringComparison.OrdinalIgnoreCase);

    // Whether Claude Code answered a resume by starting a copy of the session instead of going on with it.
    public static bool Copied(string said) =>
        said.Contains("already running", StringComparison.OrdinalIgnoreCase) || said.Contains("started a copy", StringComparison.OrdinalIgnoreCase);

    // What a state of the list of the sessions says of a session. A state that is none of these is one toobusy does
    // not know, as one that a newer Claude Code may have: Unknown.
    public static Standing StandingOf(string? state) => state switch
    {
        "working" or "starting" or "resuming" => Standing.Works,
        "blocked" => Standing.Asks,
        "done" => Standing.Ended,
        "failed" or "stopped" => Standing.Lost,
        _ => Standing.Unknown,
    };

    // Claude Code keeps the conversations of the projects in this folder of its own folder of the user, in one folder
    // for each project.
    public const string Projects = "projects";

    // The folder of the conversations of a project is named after its path, with everything but ASCII letters and
    // digits turned into dashes. Claude Code names the folder of a very long path otherwise, so a conversation that is
    // not where this says is looked for in the other folders of the projects.
    public static string Folder(string root)
    {
        var name = new StringBuilder(root.Length);
        foreach (var character in root)
            name.Append(char.IsAsciiLetterOrDigit(character) ? character : '-');
        return name.ToString();
    }

    // The file of a conversation, by what the list of the sessions calls it.
    public static string ConversationFile(string conversation) => conversation + ".jsonl";

    // The values of `error` with which Claude Code writes, as a message of the assistant, that the service refused
    // the session for its usage limit.
    public static bool Limited(string? error) => error is "rate_limit" or "billing_error";

    // The model of a message that Claude Code put into the conversation in the name of the assistant.
    public const string Synthetic = "<synthetic>";

    // Tools with which a session asks instead of doing: they are no steps of its own.
    public static readonly string[] Asking = ["AskUserQuestion", "ExitPlanMode"];

    // The tools with which a session keeps the plan of its work, a list of tasks of its own: one adds a step to it,
    // the other says how far a step is.
    public const string Adding = "TaskCreate";
    public const string Marking = "TaskUpdate";

    // The step that a call adding one added, by what Claude Code answered to it: the name the step is known by.
    public static string? Added(JsonElement result) =>
        result.ValueKind == JsonValueKind.Object && result.TryGetProperty("task", out var task) ? JsonFields.Text(task, "id") : null;

    // The step that a call marking one marked, by what Claude Code answered to it: its name, and how far it is from
    // then on; null as how far for a step that was deleted, which is no step any more.
    public static (string Step, PlanStep? State)? Marked(JsonElement result) =>
        JsonFields.Text(result, "taskId") is { } step && result.TryGetProperty("statusChange", out var change) && JsonFields.Text(change, "to") is { } status
            ? (step, status switch
            {
                "deleted" => null,
                "completed" => PlanStep.Done,
                "in_progress" => PlanStep.Active,
                _ => PlanStep.Pending,
            })
            : null;

    [GeneratedRegex(@"\d+(?:\.\d+){1,3}")]
    private static partial Regex Numbered();

    [GeneratedRegex(@"backgrounded · ([0-9a-f]+)")]
    private static partial Regex Backgrounded();
}

// What a state of the list of the sessions says of a session: it works, it waits for an answer, it ended its turn,
// it is gone or ended in a way that is not the end of a turn, or toobusy does not know the state.
public enum Standing
{
    Works,
    Asks,
    Ended,
    Lost,
    Unknown,
}

