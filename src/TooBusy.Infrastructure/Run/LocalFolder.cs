namespace TooBusy.Infrastructure.Run;

// Where a project keeps what a run leaves on this machine: `.toobusy/local`. Nothing of it is committed: the folder
// has a `.gitignore` of its own that leaves everything in it out, itself too, so that the project's own one is not
// touched and the working tree stays clean.
public static class LocalFolder
{
    public const string DisplayPath = ".toobusy/local";

    // The folder of the project at the root, made when it is not there.
    public static string Ensure(string root)
    {
        var folder = Path.Combine(root, ".toobusy", "local");
        Directory.CreateDirectory(folder);
        var ignore = Path.Combine(folder, ".gitignore");
        if (!File.Exists(ignore))
            File.WriteAllText(ignore, "*\n");
        return folder;
    }
}
