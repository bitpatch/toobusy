using TooBusy.Cli.Terminal;

namespace TooBusy.Cli.Tests;

public sealed class RunScreenTests : IDisposable
{
    readonly TestTerminal terminal = new(width: 60, height: 10);

    public void Dispose() => terminal.Dispose();

    [Fact]
    public void ThePageSaysHowCommandsStart()
    {
        Run(Keys.ControlC, Keys.ControlC);

        terminal.AssertSaw(" Type / for commands.");
    }

    [Fact]
    public void ASlashListsTheCommandsAndTheOneThatFitsBest()
    {
        terminal.Keys.Type("/e");
        Run(Keys.ControlC, Keys.ControlC);

        terminal.AssertSaw(" /e\n ❯ /exit  leave toobusy\n   /menu  go back to the menu\n tab complete · enter run");
        Assert.Equal((6, 4), terminal.Caret);
    }

    [Fact]
    public void ExitLeavesThePage()
    {
        terminal.Keys.Type("/exit").Press(Keys.Enter);

        using var page = terminal.Open();
        Assert.Equal(RunEnd.Exit, new RunScreen(page).Run());
    }

    [Fact]
    public void MenuGoesBackToTheMenuAndIsTheFirstCommand()
    {
        terminal.Keys.Type("/").Press(Keys.Enter);

        using var page = terminal.Open();
        Assert.Equal(RunEnd.Menu, new RunScreen(page).Run());
        terminal.AssertSaw(" /\n ❯ /menu  go back to the menu\n   /exit  leave toobusy");
    }

    [Fact]
    public void EnterRunsTheCommandThatFitsBestAndTabCompletesIt()
    {
        terminal.Keys.Type("/x").Press(Keys.Tab, Keys.Enter);

        using var page = terminal.Open();
        new RunScreen(page).Run();

        terminal.AssertSaw(" /exit\n ❯ /exit  leave toobusy");
    }

    [Fact]
    public void WhatFitsNoCommandSaysSo()
    {
        terminal.Keys.Type("/stop").Press(Keys.Enter);
        Run(Keys.ControlC, Keys.ControlC);

        terminal.AssertSaw(" /stop\n No command fits.");
    }

    [Fact]
    public void ATextThatIsNotACommandIsNotRunAndEscapeClearsIt()
    {
        terminal.Keys.Type("exit").Press(Keys.Enter, Keys.Escape);
        Run(Keys.ControlC, Keys.ControlC);

        terminal.AssertSaw(" exit\n Only commands work for now: they start with /.");
        terminal.AssertSaw(" Type / for commands.");
    }

    // Runs the page with the keys that are pressed already and then these; they must end in leaving with Ctrl+C.
    void Run(params ConsoleKeyInfo[] keys)
    {
        terminal.Keys.Press(keys);
        using var page = terminal.Open();
        Assert.Throws<OperationCanceledException>(() => new RunScreen(page).Run());
    }
}
