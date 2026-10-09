using TooBusy.Cli;
using TooBusy.Cli.Terminal;

// A prompt hides the cursor while it is on the screen; Ctrl+C in the middle of one must bring it back.
Console.CancelKeyPress += (_, _) => Console.Out.Write(TerminalDialog.ShowCursor);

return await CliApp.RunAsync(args, CliContext.OfProcess());
