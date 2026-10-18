using System.Globalization;
using TooBusy.Core.Run;

namespace TooBusy.Infrastructure.Run;

// The log of one run, a file of the local folder: `logs/run-<date>-<time>.log`, named after the moment the run
// opened it, in the time of the machine. Every line is written with the time of day, and a text of several lines
// stands under its first one, indented. It is written line by line, as the run goes, so that a run that dies leaves
// what it had written. The older logs are pruned to Kept when a log opens. Nothing of it stops a run: a file that
// cannot be opened or written is said once, by Unwritable, and ignored from then on, as is what is written after the
// log is closed.
public sealed class RunLogFile : IDisposable
{
    public const int Kept = 10;

    const string Folder = "logs";
    const string Prefix = "run-";
    const string Stamp = "HH:mm:ss.fff";
    static readonly string Indent = new(' ', Stamp.Length + 1);

    readonly object gate = new();
    readonly IClock clock;
    StreamWriter? writer;

    public RunLogFile(string local, IClock clock)
    {
        this.clock = clock;
        var opened = clock.Now.ToLocalTime();
        var folder = Path.Combine(local, Folder);
        FullPath = Path.Combine(folder, $"{Prefix}{opened.ToString("yyyy-MM-dd-HH-mm-ss", CultureInfo.InvariantCulture)}.log");
        DisplayPath = $"{LocalFolder.DisplayPath}/{Folder}/{Path.GetFileName(FullPath)}";
        try
        {
            Directory.CreateDirectory(folder);
            Prune(folder);
            writer = new StreamWriter(FullPath, append: true) { AutoFlush = true };
            writer.WriteLine($"run started {opened.ToString("yyyy-MM-dd HH:mm:ss zzz", CultureInfo.InvariantCulture)}");
        }
        catch (Exception unwritable) when (Failed(unwritable))
        {
            Unwritable = unwritable.Message;
            writer = null;
        }
    }

    public string FullPath { get; }

    // The path as it is said to the owner, from the root of the project.
    public string DisplayPath { get; }

    // What the system said when the log could not be opened or written; null while it can.
    public string? Unwritable { get; private set; }

    // The time of the run, for whoever measures something it writes.
    public DateTimeOffset Now => clock.Now;

    // A text of several lines stands under its first one, each line indented by the width of the time.
    public void Write(string text)
    {
        var at = clock.Now.ToLocalTime().ToString(Stamp, CultureInfo.InvariantCulture);
        lock (gate)
        {
            if (writer is null)
                return;

            try
            {
                var lines = text.Split('\n');
                writer.WriteLine($"{at} {lines[0].TrimEnd('\r')}");
                foreach (var line in lines.Skip(1))
                    writer.WriteLine(Indent + line.TrimEnd('\r'));
            }
            catch (Exception unwritable) when (Failed(unwritable))
            {
                Unwritable ??= unwritable.Message;
                Close();
            }
        }
    }

    public void Dispose()
    {
        lock (gate)
            Close();
    }

    void Close()
    {
        try
        {
            writer?.Dispose();
        }
        catch (Exception failed) when (Failed(failed))
        {
        }

        writer = null;
    }

    // The older logs are deleted, so that Kept remain with the one that opens; one that cannot be deleted is left.
    static void Prune(string folder)
    {
        foreach (var file in Directory.GetFiles(folder, $"{Prefix}*.log").Order(StringComparer.Ordinal).SkipLast(Kept - 1))
        {
            try
            {
                File.Delete(file);
            }
            catch (Exception undeletable) when (Failed(undeletable))
            {
            }
        }
    }

    static bool Failed(Exception failed) => failed is IOException or UnauthorizedAccessException or ObjectDisposedException;
}
