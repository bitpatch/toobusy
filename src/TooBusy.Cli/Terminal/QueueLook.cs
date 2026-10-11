using System.Globalization;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using TooBusy.Core.Queue;

namespace TooBusy.Cli.Terminal;

// What the queue looks like when it is shown without a run: three lists, each under its name, a task a line with
// what is to be said of it after its title, and the same as JSON. `recorded` is the task an earlier run left with a
// session, which a run goes on with first.
public static class QueueLook
{
    // What is said of the task of a record, and of one that has the label of interrupted tasks.
    public const string Left = "left with a session";
    public const string Interrupted = "interrupted";

    // A title that is longer gives way, so that what is said of the task stays near it.
    const int Longest = 60;

    public static IEnumerable<string> Lines(TaskLineup lineup, QueueTask? recorded, QueueRules rules, Palette palette)
    {
        (string Name, List<(QueueTask Task, string Note, Tone Tone)> Tasks)[] lists =
        [
            ("Ready", [.. lineup.Ready.Select(task => (task, Mark(task, recorded, rules) ?? "", Tone.Warning))]),
            ("Later", [.. lineup.Later.Select(later => (later.Task, "waits for " + string.Join(", ", later.After.Select(before => $"#{before}")), Tone.Muted))]),
            ("Held", [.. lineup.Held.Select(held => (held.Task, held.Reason, Tone.Muted))]),
        ];

        // The numbers and the titles of all the lists stand in the same columns.
        var all = lists.SelectMany(list => list.Tasks).ToList();
        var numbers = all.Select(row => Number(row.Task).Length).DefaultIfEmpty().Max();
        var titles = all.Select(row => Title(row.Task).Length).DefaultIfEmpty().Max();
        foreach (var (name, tasks) in lists)
        {
            if (name != lists[0].Name)
                yield return "";

            yield return palette.Paint(Tone.Strong, name);
            if (tasks.Count == 0)
                yield return "  " + palette.Muted("no tasks");
            foreach (var (task, note, tone) in tasks)
            {
                var title = note.Length == 0 ? Title(task) : Title(task).PadRight(titles);
                yield return ("  " + palette.Muted(Number(task).PadRight(numbers)) + " " + title + (note.Length == 0 ? "" : "  " + palette.Paint(tone, note))).TrimEnd();
            }
        }
    }

    // The three lists for an assistant: each task with its number, its title, its address and its labels; a task
    // that is ready with what it is taken first for, when it is, `session` or `interrupted`, one that opens later
    // with the numbers of the tasks it waits for, and one that is held with the reason.
    public static string Json(TaskLineup lineup, QueueTask? recorded, QueueRules rules)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream, new JsonWriterOptions { Indented = true, Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping }))
        {
            writer.WriteStartObject();
            writer.WriteStartArray("ready");
            foreach (var task in lineup.Ready)
            {
                Open(writer, task);
                if (Mark(task, recorded, rules) is { } first)
                    writer.WriteString("first", first == Left ? "session" : "interrupted");
                writer.WriteEndObject();
            }

            writer.WriteEndArray();
            writer.WriteStartArray("later");
            foreach (var later in lineup.Later)
            {
                Open(writer, later.Task);
                writer.WriteStartArray("waitsFor");
                foreach (var before in later.After)
                    writer.WriteNumberValue(before);
                writer.WriteEndArray();
                writer.WriteEndObject();
            }

            writer.WriteEndArray();
            writer.WriteStartArray("held");
            foreach (var held in lineup.Held)
            {
                Open(writer, held.Task);
                writer.WriteString("reason", held.Reason);
                writer.WriteEndObject();
            }

            writer.WriteEndArray();
            writer.WriteEndObject();
        }

        return Encoding.UTF8.GetString(stream.ToArray());
    }

    static void Open(Utf8JsonWriter writer, QueueTask task)
    {
        writer.WriteStartObject();
        writer.WriteNumber("number", task.Number);
        writer.WriteString("title", task.Title);
        writer.WriteString("url", task.Url);
        writer.WriteStartArray("labels");
        foreach (var label in task.Labels)
            writer.WriteStringValue(label);
        writer.WriteEndArray();
    }

    // Why a run takes the task before the others; null for a task it takes in its turn.
    static string? Mark(QueueTask task, QueueTask? recorded, QueueRules rules) =>
        task.Number == recorded?.Number ? Left : task.Has(rules.Interrupted) ? Interrupted : null;

    static string Number(QueueTask task) => string.Create(CultureInfo.InvariantCulture, $"#{task.Number}");

    static string Title(QueueTask task) => task.Title.Length > Longest ? task.Title[..(Longest - 1)] + "…" : task.Title;
}
