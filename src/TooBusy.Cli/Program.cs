using TooBusy.Cli;
using TooBusy.Cli.Terminal;

// A screen takes the terminal over while it is open; a program that is stopped from outside must still give it back.
Console.CancelKeyPress += (_, _) => Console.Out.Write(Screen.Leave);

return await CliApp.RunAsync(args, CliContext.OfProcess());
