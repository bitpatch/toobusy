using System.Globalization;
using System.Text.Json;
using TooBusy.Core.Run;

namespace TooBusy.Infrastructure.Run;

// What a run leaves for the next one, as a file of the local folder of the project: `session.json`, the task a run
// has a session for, what the session is found by, and how it was left. A file that cannot be read says nothing.
public sealed class RunStateFile(string folder) : IRunState
{
    string Path => System.IO.Path.Combine(folder, "session.json");

    public TaskRecord? Load()
    {
        try
        {
            if (!File.Exists(Path))
                return null;

            using var document = JsonDocument.Parse(File.ReadAllBytes(Path));
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("task", out var task) || !task.TryGetInt32(out var number))
                return null;

            var since = DateTimeOffset.TryParse(Text(root, "since"), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var written) ? written : DateTimeOffset.MinValue;
            var state = Text(root, "state") switch
            {
                "paused" => RecordState.Paused,
                "wrapped-up" => RecordState.WrappedUp,
                _ => RecordState.Working,
            };
            return new TaskRecord(number, new SessionTrace(Text(root, "session"), Text(root, "conversation"), since), state);
        }
        catch (Exception unreadable) when (unreadable is IOException or UnauthorizedAccessException or JsonException or InvalidOperationException)
        {
            return null;
        }
    }

    // The file is put in place whole, never half written: a run may be stopped at any moment.
    public void Save(TaskRecord record)
    {
        Directory.CreateDirectory(folder);
        var written = Path + ".tmp";
        using (var stream = File.Create(written))
        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartObject();
            writer.WriteNumber("task", record.Number);
            if (record.Session.Id is { } id)
                writer.WriteString("session", id);
            if (record.Session.Conversation is { } conversation)
                writer.WriteString("conversation", conversation);
            writer.WriteString("since", record.Session.Since.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss.fffffff'Z'", CultureInfo.InvariantCulture));
            writer.WriteString("state", record.State switch
            {
                RecordState.Paused => "paused",
                RecordState.WrappedUp => "wrapped-up",
                _ => "working",
            });
            writer.WriteEndObject();
        }

        File.Move(written, Path, overwrite: true);
    }

    public void Clear() => File.Delete(Path);

    static string? Text(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String && value.GetString() is { Length: > 0 } text ? text : null;
}
