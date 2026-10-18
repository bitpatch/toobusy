using System.Globalization;
using System.Text;
using System.Text.Json;
using TooBusy.Core.Run;

namespace TooBusy.Assistants.ClaudeCode;

// What a session did, read from the conversation Claude Code writes as it goes: a line of JSON for everything said
// and done. The file is read on from where the reading stopped, by its bytes, whole lines only, and what was written
// before the run took the session is left out, so that a session that goes on does not count for what it said
// before. Its plan is the one thing that is read from the start: a session that goes on has the plan it had.
//
// The conversation is opened anew for every read; a test gives it what it opens.
public sealed class Transcript(Func<Stream> open, DateTimeOffset since, string root)
{
    // The tools that keep the plan of a session: one adds a step to it, the other says how far a step is.
    const string Adding = ClaudeCodeProtocol.Adding;
    const string Marking = ClaudeCodeProtocol.Marking;

    // A step is said in so many characters at most.
    const int Longest = 80;

    // The calls that keep the plan and are not answered yet, by their names, and the steps of the plan in the
    // order they were added, each known by what Claude Code calls it.
    readonly Dictionary<string, string> planning = [];
    readonly List<(string Id, PlanStep State)> steps = [];

    // Where the reading stopped, in bytes, and the bytes of the line that was not written to its end by then.
    long position;
    byte[] rest = [];

    // The conversation in the file Claude Code writes it to.
    public Transcript(string path, DateTimeOffset since, string root)
        : this(() => new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete), since, root)
    {
    }

    // What the session is doing: its last step.
    public string? Step { get; private set; }

    // How many steps of its own the session has made.
    public int Steps { get; private set; }

    // The size of the conversation, in tokens.
    public long Context { get; private set; }

    // The plan the session keeps of its work, step by step; null when it keeps none.
    public IReadOnlyList<PlanStep>? Plan { get; private set; }

    // The last thing the session said.
    public string? Reply { get; private set; }

    // Whether the session has reported: the reply ends with a line for toobusy, and the session did nothing after it,
    // such as asking with a tool.
    public bool Reported { get; private set; }

    // What the session was refused with when the last thing in its conversation is a usage limit.
    public string? Limit { get; private set; }

    // The session was told something at this moment: what it said before does not count any more.
    public void From(DateTimeOffset told)
    {
        since = told;
        Reply = null;
        Reported = false;
        Limit = null;
    }

    // Reads what was written since the last time. A file that is not there yet, or cannot be read now, has nothing new.
    // The reading goes on from the last byte it read: Claude Code may write on while the file is read, and what it
    // writes then is read the next time.
    public void Read()
    {
        byte[] added;
        try
        {
            using var stream = open();
            if (stream.Length < position)
                (position, rest) = (0, []);

            stream.Seek(position, SeekOrigin.Begin);
            using var read = new MemoryStream();
            stream.CopyTo(read);
            added = read.ToArray();
        }
        catch (Exception unreadable) when (unreadable is IOException or UnauthorizedAccessException)
        {
            return;
        }

        // The last line may be half written, to the middle of a character even: its bytes wait for its end.
        position += added.Length;
        var unread = rest.Length == 0 ? added : [.. rest, .. added];
        var lines = unread.AsSpan();
        for (int end; (end = lines.IndexOf((byte)'\n')) >= 0; lines = lines[(end + 1)..])
            Take(Encoding.UTF8.GetString(lines[..end]));
        rest = lines.ToArray();
    }

    void Take(string line)
    {
        if (line.Trim().Length == 0)
            return;

        try
        {
            using var document = JsonDocument.Parse(line);
            var entry = document.RootElement;
            if (entry.ValueKind != JsonValueKind.Object || Flag(entry, "isSidechain")
                || !entry.TryGetProperty("message", out var message) || message.ValueKind != JsonValueKind.Object)
                return;

            var blocks = message.TryGetProperty("content", out var content) && content.ValueKind == JsonValueKind.Array ? content.EnumerateArray().ToList() : [];
            if (Text(entry, "type") == "user")
            {
                Planned(entry, blocks);
                return;
            }

            if (Text(entry, "type") != "assistant")
                return;

            // A call that keeps the plan is waited for whenever it was made: what Claude Code answers says what
            // became of the plan.
            foreach (var block in blocks)
            {
                if (Text(block, "type") == "tool_use" && Text(block, "name") is Adding or Marking && Text(block, "id") is { } call)
                    planning[call] = Text(block, "name")!;
            }

            if (Text(entry, "timestamp") is not { } written
                || !DateTimeOffset.TryParse(written, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var at) || at < since)
                return;

            if (Flag(entry, "isApiErrorMessage"))
            {
                // Claude Code writes what the service refused with as a message of the assistant.
                if (ClaudeCodeProtocol.Limited(Text(entry, "error")))
                    Limit = string.Join('\n', blocks.Select(block => Text(block, "text")).OfType<string>()) is { Length: > 0 } refusal ? refusal : "the usage limit is reached";
                return;
            }

            // What Claude Code puts into the conversation in the name of the assistant is not said by the session.
            if (Text(message, "model") == ClaudeCodeProtocol.Synthetic)
                return;

            Limit = null;
            if (message.TryGetProperty("usage", out var usage) && usage.ValueKind == JsonValueKind.Object)
            {
                var size = Number(usage, "input_tokens") + Number(usage, "cache_read_input_tokens") + Number(usage, "cache_creation_input_tokens");
                if (size > 0)
                    Context = size;
            }

            foreach (var block in blocks)
            {
                if (Text(block, "type") == "tool_use" && Text(block, "name") is { } tool)
                {
                    // Keeping the plan is a step, but not what the session is doing: the bar of the plan tells of it.
                    if (tool is not (Adding or Marking))
                        Step = Told(tool, block.TryGetProperty("input", out var input) ? input : default);
                    if (!ClaudeCodeProtocol.Asking.Contains(tool))
                        Steps++;
                    Reported = false;
                }
                else if (Text(block, "type") == "text" && Text(block, "text") is { } said && said.Trim().Length > 0)
                {
                    Reply = said;
                    Reported = Outcome.Ends(said);
                }
            }
        }
        catch (JsonException)
        {
            // A line that is not JSON says nothing.
        }
    }

    // What Claude Code answered to the calls that keep the plan: a step that was added is known by the name it was
    // given, and a step that was marked is as far as the answer says. One that was deleted is no step any more.
    void Planned(JsonElement entry, List<JsonElement> blocks)
    {
        if (!entry.TryGetProperty("toolUseResult", out var result) || result.ValueKind != JsonValueKind.Object)
            return;

        foreach (var block in blocks)
        {
            if (Text(block, "type") != "tool_result" || Text(block, "tool_use_id") is not { } call || !planning.Remove(call, out var tool))
                continue;

            if (tool == Adding)
            {
                if (ClaudeCodeProtocol.Added(result) is { } id && !steps.Exists(step => step.Id == id))
                    steps.Add((id, PlanStep.Pending));
            }
            else if (ClaudeCodeProtocol.Marked(result) is ({ } marked, var state))
            {
                var index = steps.FindIndex(step => step.Id == marked);
                if (index < 0)
                    continue;
                if (state is { } far)
                    steps[index] = (marked, far);
                else
                    steps.RemoveAt(index);
            }

            Plan = steps.Count == 0 ? null : [.. steps.Select(step => step.State)];
        }
    }

    // A step in a few words: the tool, and what it is used on.
    string Told(string tool, JsonElement input)
    {
        var on = tool switch
        {
            "Bash" => Text(input, "command")?.Split('\n')[0],
            "Read" or "Edit" or "Write" or "NotebookEdit" => Text(input, "file_path") is { } file ? Relative(file) : null,
            "Grep" or "Glob" => Text(input, "pattern"),
            _ => Text(input, "description"),
        };
        var step = on is null ? tool : tool == "Bash" ? $"Bash: {on.Trim()}" : $"{tool} {on.Trim()}";
        return step.Length <= Longest ? step : step[..(Longest - 1)] + "…";
    }

    // A file of the project is named from its root.
    string Relative(string file) => file.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.Ordinal) ? file[(root.Length + 1)..] : file;

    static string? Text(JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    static bool Flag(JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.True;

    static long Number(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out var number) ? number : 0;
}
