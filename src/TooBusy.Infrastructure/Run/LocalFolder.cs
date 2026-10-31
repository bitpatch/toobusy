using TooBusy.Core.Run;

namespace TooBusy.Infrastructure.Run;

// Where a project keeps what a run leaves on this machine: `.toobusy/local`. Nothing of it is committed: the folder
// has a `.gitignore` of its own that leaves everything in it out, itself too, so that the project's own one is not
// touched and the working tree stays clean.
public static class LocalFolder
{
    public const string DisplayPath = ".toobusy/local";

    // Where the folder of the project at the root is, whether it is there or not.
    public static string Of(string root) => Path.Combine(root, ".toobusy", "local");

    // The folder of the project at the root, made when it is not there.
    public static string Ensure(string root) => Make(Of(root));

    // The record a run leaves for the next one in the folder of the project at the root, and the sign of a run that
    // goes on there: what a run writes and `toobusy status` reads.
    public static RunStateFile State(string root) => new(Of(root));

    public static RunSignFile Sign(string root) => new(Of(root));

    // Where the git worktrees of a run of several sessions are, one folder for each task: inside the local folder, out
    // of git like all of it, with a local folder of its own in each that keeps the record of its task.
    public static string Worktrees(string root) => Path.Combine(Of(root), "worktrees");

    // The folders of the worktrees that are there.
    public static IReadOnlyList<string> WorktreeFolders(string root)
    {
        try
        {
            return Directory.Exists(Worktrees(root)) ? [.. Directory.GetDirectories(Worktrees(root)).Order(StringComparer.Ordinal)] : [];
        }
        catch (Exception unreadable) when (unreadable is IOException or UnauthorizedAccessException)
        {
            return [];
        }
    }

    // The records of the project at the root: that of its working copy and those of its worktrees, as a run finds them.
    public static IReadOnlyList<TaskRecord> Records(string root) =>
        [.. new[] { root }.Concat(WorktreeFolders(root)).Select(State).Select(state => state.Load()).OfType<TaskRecord>()];

    // The local folder at this path, made with its `.gitignore` when either is not there. Whatever writes into the
    // folder makes sure of it first: one that was taken away while a run goes on, by a `git clean -fdx` of a session
    // or a script of the project, comes back out of git, not as files of the working tree that keep the next task
    // from starting.
    public static string Make(string folder)
    {
        Directory.CreateDirectory(folder);
        var ignore = Path.Combine(folder, ".gitignore");
        if (!File.Exists(ignore))
            File.WriteAllText(ignore, "*\n");
        return folder;
    }
}
