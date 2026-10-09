namespace TooBusy.Infrastructure.Settings;

public static class ProjectLocator
{
    // The root of the git working copy that holds the folder, or null outside a working copy.
    // `.git` is a folder in an ordinary clone and a file in a worktree or a submodule.
    public static string? FindRoot(string folder)
    {
        for (var current = new DirectoryInfo(folder); current is not null; current = current.Parent)
        {
            var git = Path.Combine(current.FullName, ".git");
            if (Directory.Exists(git) || File.Exists(git))
                return current.FullName;
        }

        return null;
    }
}
