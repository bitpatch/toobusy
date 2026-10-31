namespace TooBusy.Assistants.ClaudeCode;

// The two folders Claude Code is worked with through. Local is the folder of the project where a run keeps what it
// leaves on this machine: the request that a hook passes to the session, and what the status line of a session
// tells of the usage limits. MakeLocal makes sure of that folder before anything is written into it, as the run
// keeps it, out of git, however it was taken away. Transcripts is where Claude Code writes the conversations of the
// project, one of the folders of its projects. The limits are those of the account: a git worktree of a run of several
// sessions has a Local of its own, for its request, and what its status line tells of them goes to UsageFolder, the
// Local of the working copy of the run, whichever session told last.
public sealed record ClaudeFolders(string Local, Action MakeLocal, string Transcripts)
{
    public string? UsageFolder { get; init; }

    // Claude Code keeps the conversations of a project under its own folder, in one that is named after the path
    // of the project, as `ClaudeCodeProtocol.Folder` says.
    public static ClaudeFolders Of(string root, string local, Action makeLocal, string home, Func<string, string?> variable) =>
        new(local, makeLocal, Path.Combine(Configuration(home, variable), ClaudeCodeProtocol.Projects, ClaudeCodeProtocol.Folder(root)));

    // Claude Code's own folder of the user, where it keeps the conversations and the skills of the user.
    public static string Configuration(string home, Func<string, string?> variable) =>
        variable("CLAUDE_CONFIG_DIR") is { Length: > 0 } set ? set : Path.Combine(home, ".claude");

    // The folder of the folders of the projects, the conversations of each in one.
    public string Projects => Path.GetDirectoryName(Transcripts) ?? Transcripts;

    // The request a session reads after its next step: a hook of the session prints this file.
    public string Request => Path.Combine(Local, "wrap-up.json");

    // What the status line of the last session said: Claude Code gives it the usage limits among other things.
    public string Usage => Path.Combine(UsageFolder ?? Local, "usage.json");

    // Where the conversation is by the name of the folder of the project.
    public string Transcript(string conversation) => Path.Combine(Transcripts, ClaudeCodeProtocol.ConversationFile(conversation));

    // Where the conversation is on this machine: in the folder of the project, or, where Claude Code named that folder
    // otherwise, in another folder of its projects. Null when no folder has it, or the folders cannot be read.
    public string? Find(string conversation)
    {
        if (Transcript(conversation) is var here && File.Exists(here))
            return here;

        try
        {
            return Directory.EnumerateDirectories(Projects)
                .Select(folder => Path.Combine(folder, ClaudeCodeProtocol.ConversationFile(conversation)))
                .FirstOrDefault(File.Exists);
        }
        catch (Exception unreadable) when (unreadable is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }
}
