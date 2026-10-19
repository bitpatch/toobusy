using System.Globalization;
using System.Text.Json;

namespace TooBusy.Infrastructure.Run;

// What the files of a run read and write of their JSON in the same way: a text, and a moment, which is written in UTC
// to the tenth of a microsecond.
static class JsonFields
{
    const string Stamp = "yyyy-MM-dd'T'HH:mm:ss.fffffff'Z'";

    // The text of the field; null when it is not there, is not a text or is empty.
    public static string? Text(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String && value.GetString() is { Length: > 0 } text ? text : null;

    // The moment of the field; null when it is not one.
    public static DateTimeOffset? Time(JsonElement element, string name) =>
        DateTimeOffset.TryParse(Text(element, name), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var written) ? written : null;

    public static string Stamped(DateTimeOffset moment) => moment.UtcDateTime.ToString(Stamp, CultureInfo.InvariantCulture);
}
