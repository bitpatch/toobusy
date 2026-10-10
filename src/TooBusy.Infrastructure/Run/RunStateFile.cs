using System.Text.Json;
using TooBusy.Core.Run;

namespace TooBusy.Infrastructure.Run;

// What a run leaves for the next one, as a file of the local folder of the project: `paused.json`, the task that a
// usage limit stopped and the conversation of its session. A file that cannot be read says nothing.
public sealed class RunStateFile(string folder) : IRunState
{
    string Path => System.IO.Path.Combine(folder, "paused.json");

    public PausedTask? LoadPaused()
    {
        try
        {
            if (!File.Exists(Path))
                return null;

            using var document = JsonDocument.Parse(File.ReadAllBytes(Path));
            var root = document.RootElement;
            return root.ValueKind == JsonValueKind.Object
                && root.TryGetProperty("task", out var task) && task.TryGetInt32(out var number)
                && root.TryGetProperty("conversation", out var conversation) && conversation.GetString() is { Length: > 0 } named
                    ? new PausedTask(number, named)
                    : null;
        }
        catch (Exception unreadable) when (unreadable is IOException or UnauthorizedAccessException or JsonException or InvalidOperationException)
        {
            return null;
        }
    }

    public void SavePaused(PausedTask task)
    {
        Directory.CreateDirectory(folder);
        using var stream = File.Create(Path);
        using var writer = new Utf8JsonWriter(stream);
        writer.WriteStartObject();
        writer.WriteNumber("task", task.Number);
        writer.WriteString("conversation", task.Conversation);
        writer.WriteEndObject();
    }

    public void ClearPaused() => File.Delete(Path);
}
