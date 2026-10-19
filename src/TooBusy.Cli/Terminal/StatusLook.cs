using System.Globalization;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using TooBusy.Cli.Imitation;
using TooBusy.Core.Run;
using TooBusy.Infrastructure.Run;

namespace TooBusy.Cli.Terminal;

// What `toobusy status` prints: whether a run is going on in the working copy, as a line or two, and the same as
// JSON. `going` is the run that goes on, null when none does, and `left` the record an earlier run left, which the
// next run goes on with.
public static class StatusLook
{
    // The run of a demo, in its made-up project: it goes on nowhere.
    public static GoingRun MadeUp { get; } = new(4242, new DateTimeOffset(2030, 1, 1, 9, 0, 0, TimeSpan.Zero), new RunSign(RunPhase.Working)
    {
        Task = 102,
        Title = "Export the orders as a CSV file",
        Url = DemoProject.Issue(102),
        Open = "claude attach 1a2b3c4d",
    });

    public static IEnumerable<string> Lines(GoingRun? going, TaskRecord? left)
    {
        if (going is not null)
        {
            yield return $"A run is going on: {going.Sign.Told}";
            yield break;
        }

        yield return "No run is going on.";
        if (left is not null)
            yield return string.Create(CultureInfo.InvariantCulture, $"An earlier run left #{left.Number} with a session: the next run goes on with it.");
    }

    // For an assistant: `running`, and of a run that goes on its `process`, `since` when it goes on, its `phase`,
    // the `task` it works on, with its number, its title and its address, the `session` as the command that opens
    // it, and `until` when it waits for a usage limit. Without a run, `left` is the task an earlier run left with
    // a session, with how the session was left.
    public static string Json(GoingRun? going, TaskRecord? left)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream, new JsonWriterOptions { Indented = true, Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping }))
        {
            writer.WriteStartObject();
            writer.WriteBoolean("running", going is not null);
            if (going is { Sign: var sign })
            {
                writer.WriteNumber("process", going.Process);
                writer.WriteString("since", Stamp(going.Since));
                writer.WriteString("phase", sign.Task is null && sign.Phase == RunPhase.Preparing ? "between-tasks" : RunSignFile.Name(sign.Phase));
                if (sign.Task is { } number)
                {
                    writer.WriteStartObject("task");
                    writer.WriteNumber("number", number);
                    writer.WriteString("title", sign.Title);
                    writer.WriteString("url", sign.Url);
                    writer.WriteEndObject();
                }

                if (sign.Open is { } open)
                    writer.WriteString("session", open);
                if (sign.Until is { } until)
                    writer.WriteString("until", Stamp(until));
            }
            else if (left is not null)
            {
                writer.WriteStartObject("left");
                writer.WriteNumber("task", left.Number);
                writer.WriteString("state", left.State switch
                {
                    RecordState.Paused => "paused",
                    RecordState.WrappedUp => "wrapped-up",
                    _ => "working",
                });
                writer.WriteEndObject();
            }

            writer.WriteEndObject();
        }

        return Encoding.UTF8.GetString(stream.ToArray());
    }

    static string Stamp(DateTimeOffset time) => time.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture);
}
