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
    public static string Ensure(string root)
    {
        var folder = Of(root);
        Directory.CreateDirectory(folder);
        var ignore = Path.Combine(folder, ".gitignore");
        if (!File.Exists(ignore))
            File.WriteAllText(ignore, "*\n");
        return folder;
    }
}
