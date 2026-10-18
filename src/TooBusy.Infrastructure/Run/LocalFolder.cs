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
