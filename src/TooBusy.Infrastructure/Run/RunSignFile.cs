using System.ComponentModel;
using System.Diagnostics;
using System.Text.Json;
using TooBusy.Core.Run;

namespace TooBusy.Infrastructure.Run;

// The sign of a run as a file of the local folder of the project: `run.json`, with the number of the process of the
// run, when that process started, when the run did, and what the run says of itself. A sign counts while its process
// lives: what a run that died left behind says nothing, and neither does a file that cannot be read. The number of a
// process is given anew after a while, so the process must have started when the sign says it did. `process` is the
// process of this run, `startOf` says when a process started, null for one that does not live, `now` is the time, and
// `pause` waits while another run holds the sign. A sign that cannot be written or taken down is a LocalStateException.
public sealed class RunSignFile(string folder, int process, Func<int, ProcessStart?> startOf, Func<DateTimeOffset> now, Action<TimeSpan> pause) : IRunSign
{
    // Two readings of the start of one process differ by less than this.
    static readonly TimeSpan Slack = TimeSpan.FromSeconds(2);

    // A run that another one holds the sign against waits a moment, as many times as this, two seconds in all: putting
    // a sign up takes a few milliseconds.
    const int Waits = 100;
    static readonly TimeSpan Moment = TimeSpan.FromMilliseconds(20);

    // When this run and its process started, as they stand on its sign.
    DateTimeOffset since;
    DateTimeOffset? processStarted;

    public RunSignFile(string folder)
        : this(folder, Environment.ProcessId, StartOf, () => DateTimeOffset.UtcNow, Thread.Sleep)
    {
    }

    string Path => System.IO.Path.Combine(folder, "run.json");

    string Lock => System.IO.Path.Combine(folder, "run.lock");

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
            if (living.At is { } started && JsonFields.Time(root, "processStarted") is { } written && (started - written).Duration() > Slack)
                return null;

            return new GoingRun(id, JsonFields.Time(root, "since") ?? DateTimeOffset.MinValue, Sign(root) with
            {
                Beside = root.TryGetProperty("beside", out var beside) && beside.ValueKind == JsonValueKind.Array ? [.. beside.EnumerateArray().Select(Sign)] : [],
            });
        }
        catch (Exception unreadable) when (unreadable is IOException or UnauthorizedAccessException or JsonException or InvalidOperationException)
        {
            return null;
        }
    }

    // What stands on the sign of one task.
    static RunSign Sign(JsonElement element)
    {
        var phase = JsonFields.Text(element, "phase") switch
        {
            "working" => RunPhase.Working,
            "waiting-for-owner" => RunPhase.WaitingForOwner,
            "waiting-for-limit" => RunPhase.WaitingForLimit,
            _ => RunPhase.Preparing,
        };
        return new RunSign(phase)
        {
            Task = element.TryGetProperty("task", out var worked) && worked.TryGetInt32(out var taken) ? taken : null,
            Title = JsonFields.Text(element, "title"),
            Url = JsonFields.Text(element, "url"),
            Open = JsonFields.Text(element, "open"),
            Until = JsonFields.Time(element, "until"),
        };
    }

    // The sign of a run of this very process is its own: a run of the menu comes after another one of the same tool. The
    // run looks at the sign and puts its own up while it holds the lock of the sign, so that two runs started together
    // cannot both find none, or one that died, and both put theirs up: the one that comes second finds the sign of the
    // first.
    public RunSign? Raise(RunSign sign)
    {
        using var held = Hold();
        if (Read() is { } going && going.Process != process)
            return going.Sign;

        since = now();
        processStarted = startOf(process)?.At;
        Write(sign);
        return null;
    }

    public void Change(RunSign sign) => Write(sign);

    // The lock of the sign, `run.lock`, which one process holds at a time and which the operating system takes back from
    // a process that ended, however it ended. The file is never taken down: a lock that is taken down and made anew can
    // be held by two runs at once, one of the old file and one of the new.
    FileStream Hold()
    {
        for (var waited = 0; ; waited++)
        {
            try
            {
                LocalFolder.Make(folder);
                return new FileStream(Lock, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
            }
            catch (IOException) when (waited < Waits)
            {
                pause(Moment);
            }
            catch (Exception unheld) when (unheld is IOException or UnauthorizedAccessException)
            {
                throw new LocalStateException($"the sign of the run cannot be written: {unheld.Message}", unheld);
            }
        }
    }

    // The sign of this process is taken down, and one that says nothing, as of a run that died: only the sign of
    // another run that goes on stays.
    public void Lower()
    {
        try
        {
            if (Read() is not { } going || going.Process == process)
                File.Delete(Path);
        }
        catch (Exception kept) when (kept is IOException or UnauthorizedAccessException)
        {
            throw new LocalStateException($"the sign of the run cannot be taken down: {kept.Message}", kept);
        }
    }

    // The file is put in place whole, never half written: it is read by others while the run goes on.
    void Write(RunSign sign)
    {
        try
        {
            LocalFolder.Make(folder);
            var written = Path + ".tmp";
            using (var stream = File.Create(written))
            using (var writer = new Utf8JsonWriter(stream))
            {
                writer.WriteStartObject();
                writer.WriteNumber("process", process);
                if (processStarted is { } started)
                    writer.WriteString("processStarted", JsonFields.Stamped(started));
                writer.WriteString("since", JsonFields.Stamped(since));
                Put(writer, sign);
                if (sign.Beside.Count > 0)
                {
                    writer.WriteStartArray("beside");
                    foreach (var beside in sign.Beside)
                    {
                        writer.WriteStartObject();
                        Put(writer, beside);
                        writer.WriteEndObject();
                    }

                    writer.WriteEndArray();
                }

                writer.WriteEndObject();
            }

            File.Move(written, Path, overwrite: true);
        }
        catch (Exception unwritten) when (unwritten is IOException or UnauthorizedAccessException)
        {
            throw new LocalStateException($"the sign of the run cannot be written: {unwritten.Message}", unwritten);
        }
    }

    static void Put(Utf8JsonWriter writer, RunSign sign)
    {
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
            writer.WriteString("until", JsonFields.Stamped(until));
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
}

// A process that lives, and when it started; null when that cannot be read.
public sealed record ProcessStart(DateTimeOffset? At);

// A run that goes on: the process it is the run of, when it started, and what it says of itself.
public sealed record GoingRun(int Process, DateTimeOffset Since, RunSign Sign);
