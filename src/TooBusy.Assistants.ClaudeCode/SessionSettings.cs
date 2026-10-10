using System.Text;
using System.Text.Json;

namespace TooBusy.Assistants.ClaudeCode;

// The settings a session is started with, beside those of the user and of the project. A hook prints the request of
// the run, when there is one, after every step, so that it reaches a session that works without cutting its turn.
// The status line, which Claude Code feeds with the state of the session, writes that state to a file, where the run
// reads the usage limits. And the session works in the working copy itself: a run takes one task at a time there.
public static class SessionSettings
{
    public static string Json(ClaudeFolders folders)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartObject();

            writer.WriteStartObject("hooks");
            writer.WriteStartArray("PostToolUse");
            writer.WriteStartObject();
            writer.WriteString("matcher", "*");
            writer.WriteStartArray("hooks");
            writer.WriteStartObject();
            writer.WriteString("type", "command");
            writer.WriteString("command", $"cat {Quoted(folders.Request)} 2>/dev/null || true");
            writer.WriteEndObject();
            writer.WriteEndArray();
            writer.WriteEndObject();
            writer.WriteEndArray();
            writer.WriteEndObject();

            writer.WriteStartObject("statusLine");
            writer.WriteString("type", "command");
            writer.WriteString("command", $"cat > {Quoted(folders.Usage + ".tmp")} && mv {Quoted(folders.Usage + ".tmp")} {Quoted(folders.Usage)}");
            writer.WriteEndObject();

            writer.WriteStartObject("permissions");
            writer.WriteStartArray("deny");
            writer.WriteStringValue("EnterWorktree");
            writer.WriteEndArray();
            writer.WriteEndObject();

            writer.WriteStartObject("worktree");
            writer.WriteString("bgIsolation", "none");
            writer.WriteEndObject();

            writer.WriteEndObject();
        }

        return Encoding.UTF8.GetString(stream.ToArray());
    }

    // The request as the hook prints it: Claude Code takes what a hook prints in this form as something to tell the
    // session.
    public static byte[] Request(string message)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartObject();
            writer.WriteStartObject("hookSpecificOutput");
            writer.WriteString("hookEventName", "PostToolUse");
            writer.WriteString("additionalContext", message);
            writer.WriteEndObject();
            writer.WriteEndObject();
        }

        return stream.ToArray();
    }

    // A path as a shell takes one, whatever is in it.
    static string Quoted(string path) => "'" + path.Replace("'", "'\\''", StringComparison.Ordinal) + "'";
}
