using System.Diagnostics;

namespace TooBusy.Infrastructure.Processes;

// What every command toobusy starts has in its environment beside what toobusy has. Nobody answers a command, and git
// asks for a login on the terminal, not on the input that is closed: with its prompts off git, and the Git Credential
// Manager it may ask, fail at once instead of waiting. ssh asks for a passphrase or a host key on the terminal too,
// and is left as it is: what would stop it would change what the user's own ssh does.
static class Unattended
{
    static readonly KeyValuePair<string, string>[] Environment =
    [
        new("GIT_TERMINAL_PROMPT", "0"),
        new("GCM_INTERACTIVE", "never"),
    ];

    public static void Apply(ProcessStartInfo start)
    {
        foreach (var (name, value) in Environment)
            start.Environment[name] = value;
    }
}
