using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using TooBusy.Core.Run;

namespace TooBusy.Infrastructure.Run;

// The sign of a run as a file of the local folder of the project: `run.json`, with the number of the process of the
// run, when that process started, when the run did, and what the run says of itself. A sign counts while its process
// lives: what a run that died left behind says nothing, and neither does a file that cannot be read. The number of a
// process is given anew after a while, so the process must have started when the sign says it did. `process` is the
// process of this run, `startOf` says when a process started, null for one that does not live, and `now` is the time.
public sealed class RunSignFile(string folder, int process, Func<int, ProcessStart?> startOf, Func<DateTimeOffset> now) : IRunSign
{
    const string Stamp = "yyyy-MM-dd'T'HH:mm:ss.fffffff'Z'";

    // Two readings of the start of one process differ by less than this.
    static readonly TimeSpan Slack = TimeSpan.FromSeconds(2);

    // When this run and its process started, as they stand on its sign.
    DateTimeOffset since;
    DateTimeOffset? processStarted;

    public RunSignFile(string folder)
        : this(folder, Environment.ProcessId, StartOf, () => DateTimeOffset.UtcNow)
    {
    }

    string Path => System.IO.Path.Combine(folder, "run.json");

    // The run that goes on in the working copy; null when none does.
    public GoingRun? Read()
    {
        try
        {
            if (!File.Exists(Path))
                return null;

            using var document = JsonDocument.Parse(File.ReadAllBytes(Path));
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("process", out var number) || !number.TryGetInt32(out var id))
                return null;
            if (id <= 0 || startOf(id) is not { } living)
                return null;
            if (living.At is { } started && Time(root, "processStarted") is { } written && (started - written).Duration() > Slack)
                return null;

            var phase = Text(root, "phase") switch
            {
                "working" => RunPhase.Working,
                "waiting-for-owner" => RunPhase.WaitingForOwner,
                "waiting-for-limit" => RunPhase.WaitingForLimit,
                _ => RunPhase.Preparing,
            };
            var task = root.TryGetProperty("task", out var worked) && worked.TryGetInt32(out var taken) ? taken : (int?)null;
            return new GoingRun(id, Time(root, "since") ?? DateTimeOffset.MinValue, new RunSign(phase)
            {
                Task = task,
                Title = Text(root, "title"),
                Url = Text(root, "url"),
                Open = Text(root, "open"),
                Until = Time(root, "until"),
            });
        }
        catch (Exception unreadable) when (unreadable is IOException or UnauthorizedAccessException or JsonException or InvalidOperationException)
        {
            return null;
        }
    }

    // The sign of a run of this very process is its own: a run of the menu comes after another one of the same tool.
    public RunSign? Raise(RunSign sign)
    {
        if (Read() is { } going && going.Process != process)
            return going.Sign;

        since = now();
        processStarted = startOf(process)?.At;
        Write(sign);
        return null;
    }

    public void Change(RunSign sign) => Write(sign);

    // Only the sign of this process is taken down.
    public void Lower()
    {
        if (Read() is not { } going || going.Process == process)
            File.Delete(Path);
    }

    // The file is put in place whole, never half written: it is read by others while the run goes on.
    void Write(RunSign sign)
    {
        Directory.CreateDirectory(folder);
        var written = Path + ".tmp";
        using (var stream = File.Create(written))
        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartObject();
            writer.WriteNumber("process", process);
            if (processStarted is { } started)
                writer.WriteString("processStarted", started.UtcDateTime.ToString(Stamp, CultureInfo.InvariantCulture));
            writer.WriteString("since", since.UtcDateTime.ToString(Stamp, CultureInfo.InvariantCulture));
            writer.WriteString("phase", Name(sign.Phase));
            if (sign.Task is { } task)
                writer.WriteNumber("task", task);
            if (sign.Title is { } title)
                writer.WriteString("title", title);
            if (sign.Url is { } url)
                writer.WriteString("url", url);
            if (sign.Open is { } open)
                writer.WriteString("open", open);
            if (sign.Until is { } until)
                writer.WriteString("until", until.UtcDateTime.ToString(Stamp, CultureInfo.InvariantCulture));
            writer.WriteEndObject();
        }

        File.Move(written, Path, overwrite: true);
    }

    // The phase of a run by the name it has in the file, and in what `toobusy status --json` prints.
    public static string Name(RunPhase phase) => phase switch
    {
        RunPhase.Working => "working",
        RunPhase.WaitingForOwner => "waiting-for-owner",
        RunPhase.WaitingForLimit => "waiting-for-limit",
        _ => "preparing",
    };

    // When a process of this machine started; null for a number no process has. A process whose start cannot be
    // read, as one of another user, lives all the same.
    static ProcessStart? StartOf(int id)
    {
        try
        {
            using var found = Process.GetProcessById(id);
            try
            {
                return found.HasExited ? null : new ProcessStart(found.StartTime.ToUniversalTime());
            }
            catch (Exception unread) when (unread is Win32Exception or NotSupportedException or InvalidOperationException)
            {
                return new ProcessStart(null);
            }
        }
        catch (ArgumentException)
        {
            return null;
        }
    }

    static DateTimeOffset? Time(JsonElement element, string name) =>
        DateTimeOffset.TryParse(Text(element, name), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var written) ? written : null;

    static string? Text(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String && value.GetString() is { Length: > 0 } text ? text : null;
}

// A process that lives, and when it started; null when that cannot be read.
public sealed record ProcessStart(DateTimeOffset? At);

// A run that goes on: the process it is the run of, when it started, and what it says of itself.
public sealed record GoingRun(int Process, DateTimeOffset Since, RunSign Sign);
