using System.Runtime.InteropServices;
using TooBusy.Cli;
using TooBusy.Cli.Terminal;

// A screen takes the terminal over while it is open; a program that is stopped from outside, or that falls, must
// still give it back. Where there is no terminal there is no screen: the lines that are printed are left alone.
// The terminal may go away meanwhile, as when its window is closed or the user is logged out: nothing written to it
// throws then, so that the tool still ends as it should.
TerminalWriter? terminal = null;
if (!Console.IsOutputRedirected)
{
    Console.SetOut(terminal = new TerminalWriter(Console.Out));
    if (!Console.IsErrorRedirected)
        Console.SetError(new TerminalWriter(Console.Error));
    Console.CancelKeyPress += (_, _) => Console.Out.Write(Screen.Rescue());
    AppDomain.CurrentDomain.ProcessExit += (_, _) => Console.Out.Write(Screen.Rescue());
    AppDomain.CurrentDomain.UnhandledException += (_, _) => Console.Out.Write(Screen.Rescue());
}

// A terminal that closes stops the tool as a signal from outside does, and not at once: a run is killed first, as
// its session is never left to go on with nobody watching it.
using var hangUp = new CancellationTokenSource();
using var hangingUp = PosixSignalRegistration.Create(PosixSignal.SIGHUP, signal =>
{
    signal.Cancel = true;
    hangUp.Cancel();
});

return await CliApp.RunAsync(args, CliContext.OfProcess(terminal), hangUp.Token);
