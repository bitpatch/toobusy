using System.Text.Json;

namespace TooBusy.Assistants.ClaudeCode;

// What the adapter reads of the JSON of Claude Code in the same way.
static class JsonFields
{
    // The text of the field of an object, an empty one too; null when the element is not an object, or the field is
    // not there or is not a text.
    public static string? Text(JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;
}
