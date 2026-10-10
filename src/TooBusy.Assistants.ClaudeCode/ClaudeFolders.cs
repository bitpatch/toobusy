using System.Text;

namespace TooBusy.Assistants.ClaudeCode;

// The two folders Claude Code is worked with through. Local is the folder of the project where a run keeps what it
// leaves on this machine: the request that a hook passes to the session, and what the status line of a session
// tells of the usage limits. Transcripts is where Claude Code writes the conversations of the project.
public sealed record ClaudeFolders(string Local, string Transcripts)
{
    // Claude Code keeps the conversations of a project under its own folder, in one that is named after the path
    // of the project with everything but letters and digits turned into dashes.
    public static ClaudeFolders Of(string root, string local, string home, Func<string, string?> variable)
    {
        var configuration = variable("CLAUDE_CONFIG_DIR") is { Length: > 0 } set ? set : Path.Combine(home, ".claude");
        var name = new StringBuilder(root.Length);
        foreach (var character in root)
            name.Append(char.IsAsciiLetterOrDigit(character) ? character : '-');
        return new ClaudeFolders(local, Path.Combine(configuration, "projects", name.ToString()));
    }

    // The request a session reads after its next step: a hook of the session prints this file.
    public string Request => Path.Combine(Local, "wrap-up.json");

    // What the status line of the last session said: Claude Code gives it the usage limits among other things.
    public string Usage => Path.Combine(Local, "usage.json");

    public string Transcript(string conversation) => Path.Combine(Transcripts, conversation + ".jsonl");
}
