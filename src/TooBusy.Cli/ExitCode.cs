namespace TooBusy.Cli;

// The exit codes of docs/SPEC.md.
public static class ExitCode
{
    public const int Done = 0;

    // A check did not pass, a value was refused, the setup was declined.
    public const int Failed = 1;

    // There is no project, the project is not set up, or the command line is wrong.
    public const int NotReady = 2;
}
