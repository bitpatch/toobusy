using System.CommandLine;
using System.CommandLine.Help;
using System.CommandLine.Invocation;
using System.Globalization;
using System.Reflection;
using TooBusy.Cli.Terminal;
using TooBusy.Core.Assistant;
using TooBusy.Core.Doctor;
using TooBusy.Core.Queue;
using TooBusy.Core.Run;
using TooBusy.Core.Settings;
using TooBusy.Core.Setup;
using TooBusy.Infrastructure.Run;
using TooBusy.Infrastructure.Settings;

namespace TooBusy.Cli;

public static class CliApp
{
    // The version of the tool, the one place it is read from: the menu shows it, and `--version` prints it with the
    // commit of a build in development after a plus, `1.0.0.dev+c56f6c2`. A release has no commit.
    static readonly (string Version, string? Commit) Build =
        SplitVersion(typeof(CliApp).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "");

    public static string Version => Build.Version;

    public static string? Commit => Build.Commit;

    // As git and GitHub shorten a commit.
    const int CommitLength = 7;

    // The informational version of the assembly, split into the version and the commit after its plus, which the
    // SDK puts there whole.
    public static (string Version, string? Commit) SplitVersion(string informational)
    {
        var parts = informational.Split('+', 2);
        if (parts.Length < 2 || parts[1].Length == 0)
            return (parts[0], null);
        return (parts[0], parts[1][..Math.Min(parts[1].Length, CommitLength)]);
    }

    public static RootCommand CreateRootCommand(CliContext context)
    {
        var demo = new Option<bool>("--demo")
        {
            Description = "Show the tool over made-up data: nothing is made, linked, written or remembered.",
        };
        var root = new RootCommand($"Runs the tasks of your tracker one after another with AI coding assistants. {ForAnAssistant}");
        root.Options.OfType<VersionOption>().Single().Action = new PrintVersion();
        root.Options.Add(demo);
        var init = CreateInitCommand(context, demo);
        var help = root.Options.OfType<HelpOption>().Single();
        help.Action = new HelpWithExamples((SynchronousCommandLineAction)help.Action!, init, InitExamples);
        root.Subcommands.Add(init);
        root.Subcommands.Add(CreateMilestoneCommand(context, demo));
        root.Subcommands.Add(CreateModelCommand(context, demo));
        root.Subcommands.Add(CreateEffortCommand(context, demo));
        root.Subcommands.Add(CreateRunCommand(context, demo));
        root.Subcommands.Add(CreateQueueCommand(context, demo));
        root.Subcommands.Add(CreateStatusCommand(context, demo));
        root.Subcommands.Add(CreateDoctorCommand(context, demo));

        // `--demo` is an option of every command but `about`, which reads nothing but the settings and changes
        // nothing: there is nothing of it to imitate.
        foreach (var command in root.Subcommands)
            command.Options.Add(demo);
        root.Subcommands.Add(CreateAboutCommand(context));

        // Without a command the tool opens its page in a terminal: the setup when the project needs it, the choices
        // of the milestone, the model and the effort when the user has none, and then the menu. Without a terminal there is nobody to ask,
        // so in a project that is set up it shows its help.
        root.SetAction(async (result, cancellationToken) =>
        {
            if (context.Terminal is not { } terminal)
                return FindSettings(context) is null ? ExitCode.NotReady : root.Parse(["--help"]).Invoke(result.InvocationConfiguration);
            if (FindProject(context) is not { } folder || await OpenAsync(context, folder, result.GetValue(demo), ready: true, watched: true, cancellationToken) is not { } bench)
                return ExitCode.NotReady;

            return await OnPageAsync(context, terminal, bench, session => session.HomeAsync(cancellationToken), cancellationToken);
        });
        return root;
    }

    public static async Task<int> RunAsync(string[] args, CliContext context, CancellationToken cancellationToken = default)
    {
        // A run that is stopped from outside stops its session before it leaves, and that takes a moment.
        var configuration = new InvocationConfiguration
        {
            Output = context.Output,
            Error = context.Error,
            ProcessTerminationTimeout = TimeSpan.FromSeconds(30),
            EnableDefaultExceptionHandler = false,
        };
        var root = CreateRootCommand(context);
        Guard(root, context);
        var result = root.Parse(args);
        int exit;
        try
        {
            exit = await result.InvokeAsync(configuration, cancellationToken);
        }
        catch (OperationCanceledException)
        {
            // The user stopped the tool: a command lets no other cancellation through.
            return ExitCode.Failed;
        }
        catch (Exception unforeseen)
        {
            return Unforeseen(context, unforeseen);
        }

        return result.Errors.Count > 0 ? ExitCode.NotReady : exit;
    }

    // Every command, with what it did not foresee said in words where errors go instead of a stack trace.
    static void Guard(Command command, CliContext context)
    {
        if (command.Action is AsynchronousCommandLineAction action)
            command.Action = new Guarded(action, context);
        foreach (var inner in command.Subcommands)
            Guard(inner, context);
    }

    // What a command does, and an error it did not foresee: a line where errors go and exit code 1, as for any
    // failure. The cancellation of the user is not such an error, and goes on to where the tool stops.
    sealed class Guarded(AsynchronousCommandLineAction action, CliContext context) : AsynchronousCommandLineAction
    {
        public override async Task<int> InvokeAsync(ParseResult parseResult, CancellationToken cancellationToken)
        {
            try
            {
                return await action.InvokeAsync(parseResult, cancellationToken);
            }
            catch (Exception unforeseen) when (unforeseen is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
            {
                return Unforeseen(context, unforeseen);
            }
        }
    }

    // An error the tool did not foresee: its words, and where it happened only when `TOOBUSY_TRACE=1` asks for it.
    static int Unforeseen(CliContext context, Exception unforeseen)
    {
        Say(context, $"toobusy: {Words(unforeseen)}");
        if (context.Trace)
            context.Error.WriteLine(unforeseen.ToString());
        else
            Say(context, $"This is an error toobusy did not foresee ({unforeseen.GetType().Name}): run the command again with TOOBUSY_TRACE=1 to see where it happened.");
        return ExitCode.Failed;
    }

    // The words of an error on one line.
    public static string Words(Exception failed) =>
        string.Join(' ', failed.Message.Split('\n', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries));

    // What `--version` does: prints the version of the tool, and the commit of a build in development after it.
    sealed class PrintVersion : SynchronousCommandLineAction
    {
        public override int Invoke(ParseResult parseResult)
        {
            parseResult.InvocationConfiguration.Output.WriteLine(Commit is null ? Version : $"{Version}+{Commit}");
            return ExitCode.Done;
        }
    }

    // What `--help` does: the help of System.CommandLine, and after the help of one command the examples of it.
    sealed class HelpWithExamples(SynchronousCommandLineAction help, Command command, string examples) : SynchronousCommandLineAction
    {
        public override int Invoke(ParseResult parseResult)
        {
            var exit = help.Invoke(parseResult);
            if (parseResult.CommandResult.Command == command)
                parseResult.InvocationConfiguration.Output.Write(examples);
            return exit;
        }
    }

    // How a setup that exists is changed from the command line, as the list of the options does not show it.
    static readonly string InitExamples = string.Join(Environment.NewLine,
        "Examples:",
        "  Set the command before a task; the other settings stay as they are:",
        "    toobusy init --before \"scripts/dev.sh ports free\" -y",
        "  Take the command after a task away:",
        "    toobusy init --no-after -y",
        "",
        "");

    static Command CreateInitCommand(CliContext context, Option<bool> demo)
    {
        var board = new Option<string?>("--board") { Description = "The GitHub project of the tasks, by the address of its board or of any page of it.", HelpName = "URL" };
        var noBoard = new Option<bool>("--no-board") { Description = "No project: no board keeps the statuses of the tasks." };
        var newBoard = new Option<string?>("--new-board") { Description = "A new GitHub project with this title; it is made when the setup is saved.", HelpName = "title" };
        var boardOwner = new Option<string?>("--board-owner") { Description = "Whose the new project will be: you or one of your organisations. The owner of the repository when it is not given.", HelpName = "login" };
        var todoStatus = new Option<string?>("--todo-status") { Description = "What the board calls the status a task is taken from; `Todo` when it is not given.", HelpName = "name" };
        var inProgressStatus = new Option<string?>("--in-progress-status") { Description = "What the board calls the status a run moves a task to before its session; `In Progress` when it is not given.", HelpName = "name" };
        var doneStatus = new Option<string?>("--done-status") { Description = "What the board calls the status a run moves a task to when it is done; `Done` when it is not given.", HelpName = "name" };
        var blocking = new Option<string[]>("--blocking-label") { Description = "A label that keeps a task out; give it again for every such label, or several names separated by commas.", HelpName = "name" };
        var noBlocking = new Option<bool>("--no-blocking-labels") { Description = "No label keeps a task out." };
        var take = new Option<string[]>("--take-label") { Description = "A label that lets a task in; give it again for every such label, or several names separated by commas.", HelpName = "name" };
        var noTake = new Option<bool>("--no-take-labels") { Description = "Any task is taken, whatever its labels." };
        var manual = new Option<string?>("--manual-label") { Description = "The label of a task that waits for the owner; it is made when the repository does not have it.", HelpName = "name" };
        var interrupt = new Option<string?>("--interrupt-label") { Description = "The label of a task that a run had to stop; it is made when the repository does not have it.", HelpName = "name" };
        var skill = new Option<string?>("--skill") { Description = "The skill of the project a session starts with, by its name; it says how a task is done here without you.", HelpName = "name" };
        var noSkill = new Option<bool>("--no-skill") { Description = "No skill: a session starts with the message of toobusy alone." };
        var before = new Option<string?>("--before") { Description = "The command a run runs before a task is taken: a line for the shell.", HelpName = "command" };
        var noBefore = new Option<bool>("--no-before") { Description = "No command before a task." };
        var after = new Option<string?>("--after") { Description = "The command a run runs after the session of a task, however the task went: a line for the shell.", HelpName = "command" };
        var noAfter = new Option<bool>("--no-after") { Description = "No command after a task." };
        var branch = new Option<string?>("--branch") { Description = "The branch the tasks are done on: before a task a run checks that the working copy is on it and brings it up to date with its upstream.", HelpName = "name" };
        var noBranch = new Option<bool>("--no-branch") { Description = "No branch: a run does the tasks on whatever is checked out, as it stands." };
        var permissionMode = new Option<string?>("--permission-mode")
        {
            Description = "The permission mode of Claude Code a session is started in: `auto`, where a model of Claude Code looks at every action, or `dontAsk`, where whatever the settings of Claude Code do not allow is refused. `auto` while the settings name none.",
            HelpName = "mode",
        };
        var yes = new Option<bool>("--yes", "-y") { Description = "Ask nothing: take what the options say, keep every other setting as it is, or in a first setup what it proposes, and save." };
        var assist = new Option<string?>("--assist")
        {
            Description = "Tell an assistant how to set the project up, one step at a time: the introduction with the first step, or the step of this number. It changes nothing, and goes with no other option.",
            HelpName = "step",
            Arity = ArgumentArity.ZeroOrOne,
        };
        var command = new Command("init", $"Sets the project up or changes its settings: asks what it needs and writes them. `--before`, `--after`, `--branch` and `--permission-mode` are not asked: give them as options. {ForAnAssistant}")
        {
            board, noBoard, newBoard, boardOwner, todoStatus, inProgressStatus, doneStatus, blocking, noBlocking, take, noTake, manual, interrupt, skill, noSkill, before, noBefore, after, noAfter, branch, noBranch, permissionMode, yes, assist,
        };
        command.SetAction(async (result, cancellationToken) =>
        {
            // The walk for an assistant sets nothing, so no option that sets something goes with it. A demo changes
            // nothing of it: it changes nothing as it is.
            if (result.GetResult(assist) is { Implicit: false })
            {
                Option[] others = [board, noBoard, newBoard, boardOwner, todoStatus, inProgressStatus, doneStatus, blocking, noBlocking, take, noTake, manual, interrupt, skill, noSkill, before, noBefore, after, noAfter, branch, noBranch, permissionMode, yes];
                if (others.Any(other => result.GetResult(other) is { Implicit: false }))
                {
                    Say(context, "toobusy: `--assist` goes with no other option of `init`: it tells the steps and sets nothing");
                    return ExitCode.NotReady;
                }

                return await AssistAsync(context, result.GetValue(assist), cancellationToken);
            }

            // Each answer is given once: options that answer the same question differently are a wrong command line.
            (bool Wrong, string Message)[] clashes =
            [
                (new[] { result.GetValue(board) is not null, result.GetValue(noBoard), result.GetValue(newBoard) is not null }.Count(given => given) > 1,
                    "toobusy: give one of `--board`, `--new-board` and `--no-board`, not several"),
                (result.GetValue(boardOwner) is not null && result.GetValue(newBoard) is null, "toobusy: `--board-owner` goes with `--new-board`"),
                (result.GetValue(noBoard) && new[] { todoStatus, inProgressStatus, doneStatus }.Any(status => result.GetValue(status) is not null),
                    "toobusy: `--todo-status`, `--in-progress-status` and `--done-status` name the statuses of a board, and do not go with `--no-board`"),
                (result.GetValue(blocking) is { Length: > 0 } && result.GetValue(noBlocking), "toobusy: give `--blocking-label` or `--no-blocking-labels`, not both"),
                (result.GetValue(take) is { Length: > 0 } && result.GetValue(noTake), "toobusy: give `--take-label` or `--no-take-labels`, not both"),
                (result.GetValue(skill) is not null && result.GetValue(noSkill), "toobusy: give `--skill` or `--no-skill`, not both"),
                (result.GetValue(before) is not null && result.GetValue(noBefore), "toobusy: give `--before` or `--no-before`, not both"),
                (result.GetValue(after) is not null && result.GetValue(noAfter), "toobusy: give `--after` or `--no-after`, not both"),
                (result.GetValue(branch) is not null && result.GetValue(noBranch), "toobusy: give `--branch` or `--no-branch`, not both"),
            ];
            if (clashes.FirstOrDefault(clash => clash.Wrong) is { Wrong: true } clash)
            {
                Say(context, clash.Message);
                return ExitCode.NotReady;
            }

            var proposals = new SetupProposals(
                result.GetValue(board) is { } address ? new BoardProposal.Existing(address)
                    : result.GetValue(newBoard) is { } title ? new BoardProposal.Created(title, result.GetValue(boardOwner))
                    : result.GetValue(noBoard) ? new BoardProposal.None()
                    : null,
                result.GetValue(noBlocking) ? [] : result.GetValue(blocking) is { Length: > 0 } blocks ? blocks : null,
                result.GetValue(noTake) ? [] : result.GetValue(take) is { Length: > 0 } takes ? takes : null,
                result.GetValue(manual),
                result.GetValue(interrupt),
                result.GetValue(noSkill) ? "" : result.GetValue(skill),
                result.GetValue(noBefore) ? new CommandProposal(null) : result.GetValue(before) is { } first ? new CommandProposal(first) : null,
                result.GetValue(noAfter) ? new CommandProposal(null) : result.GetValue(after) is { } last ? new CommandProposal(last) : null)
            {
                TodoStatus = result.GetValue(todoStatus),
                InProgressStatus = result.GetValue(inProgressStatus),
                DoneStatus = result.GetValue(doneStatus),
                Branch = result.GetValue(noBranch) ? new BranchProposal(null) : result.GetValue(branch) is { } name ? new BranchProposal(name) : null,
                PermissionMode = result.GetValue(permissionMode),
            };

            if (FindProject(context) is not { } root)
                return ExitCode.NotReady;

            // Without questions there is no screen: what was answered and done is printed as plain lines.
            if (result.GetValue(yes))
            {
                if (await OpenAsync(context, root, result.GetValue(demo), ready: false, watched: false, cancellationToken) is not { } unasked)
                    return ExitCode.NotReady;

                var setup = await new ProjectSetup(null, unasked.Environment, unasked.Tracker, unasked.Boards, unasked.Settings, proposals).RunAsync(cancellationToken);
                context.Output.WriteLine($"toobusy · Setting up this project · {context.Shorten(root)}");
                foreach (var note in setup.Notes)
                {
                    context.Output.WriteLine(note.Tone switch
                    {
                        SetupTone.Failure => context.OutputPalette.Error($"✘ {note.Text}"),
                        SetupTone.Warning => context.OutputPalette.Warning($"! {note.Text}"),
                        _ => context.OutputPalette.Muted(note.Text),
                    });
                }

                var refused = ReportSetup(context, setup, unasked.Demo, unasked: true);
                NameAbout(context, setup, unasked.Demo);
                return refused ? ExitCode.Failed : ExitCode.Done;
            }

            if (context.Terminal is not { } terminal)
            {
                Say(context, "toobusy: `init` asks questions and needs a terminal.");
                Say(context, "Run `toobusy init --yes` to ask nothing: it takes what the options say and what the setup proposes. `toobusy init --help` lists the options.");
                Say(context, ForAnAssistant);
                return ExitCode.Failed;
            }

            if (await OpenAsync(context, root, result.GetValue(demo), ready: false, watched: true, cancellationToken) is not { } bench)
                return ExitCode.NotReady;

            var session = await WithPageAsync(context, terminal, bench, session => session.SetupAsync(leavesPage: true, cancellationToken, proposals), cancellationToken);

            // The screen is gone; what was answered and how it ended is left in the terminal.
            context.Output.WriteLine($"toobusy · Setting up this project · {context.Shorten(root)}");
            var failed = ReportSetup(context, session.Setup, bench.Demo);
            NameAbout(context, session.Setup, bench.Demo);
            return failed ? ExitCode.Failed : ExitCode.Done;
        });
        return command;
    }

    // Where an assistant that is told to set a project up finds the walk through the setup: the help says it, and
    // so do the messages of a project that is not set up and of `init` where there is no terminal.
    const string ForAnAssistant = "An assistant that sets a project up for its owner starts with `toobusy init --assist`.";

    // Tells the assistant of the owner how to set the project up: the introduction with the list of the steps and
    // the first step, or the step of the number alone. It needs a project and no terminal, reads what the setup
    // reads, and changes nothing.
    static async Task<int> AssistAsync(CliContext context, string? number, CancellationToken cancellationToken)
    {
        var step = 0;
        if (number is not null && (!int.TryParse(number, NumberStyles.None, CultureInfo.InvariantCulture, out step) || step < 1))
        {
            Say(context, "toobusy: `--assist` takes the number of a step, which is 1 or more");
            return ExitCode.NotReady;
        }

        if (FindProject(context) is not { } root)
            return ExitCode.NotReady;
        if (step > Assist.Steps.Count)
        {
            context.Output.Write(Assist.Complete);
            return ExitCode.Done;
        }

        if (await OpenAsync(context, root, demo: false, ready: false, watched: false, cancellationToken) is not { } bench)
            return ExitCode.NotReady;

        var ground = await bench.AssistAsync(number is null ? AssistReading.Nothing : Assist.Reads(step), cancellationToken);
        context.Output.Write(number is null ? Assist.Introduction(ground) : Assist.Step(step, ground));
        return ExitCode.Done;
    }

    // The milestone to work on is the choice of the user: it is asked for on a page, or given here for a script.
    static Command CreateMilestoneCommand(CliContext context, Option<bool> demo)
    {
        var title = new Argument<string?>("title") { Description = "The title of the open milestone to work on.", Arity = ArgumentArity.ZeroOrOne };
        var none = new Option<bool>("--none") { Description = "Work without a milestone: tasks are taken whatever their milestone." };
        var command = new Command("milestone", "Chooses the milestone to work on, or shows the one that is chosen.");
        command.Arguments.Add(title);
        command.Options.Add(none);
        command.SetAction(async (result, cancellationToken) =>
        {
            var named = result.GetValue(title);
            if (named is not null && result.GetValue(none))
            {
                Say(context, "toobusy: give the title of a milestone or `--none`, not both");
                return ExitCode.NotReady;
            }

            if (FindProject(context) is not { } root || await OpenAsync(context, root, result.GetValue(demo), ready: false, watched: context.Terminal is not null, cancellationToken) is not { } bench)
                return ExitCode.NotReady;

            if (result.GetValue(none))
            {
                bench.Personal.SaveMilestone(MilestoneChoice.None);
                context.Output.WriteLine("Milestone: none. Tasks are taken whatever their milestone.");
                return ExitCode.Done;
            }

            if (named is not null)
            {
                if (await bench.ReadMilestonesAsync(cancellationToken) is not { } open)
                {
                    Say(context, bench.Repository is null
                        ? "toobusy: the `origin` remote is not a GitHub repository, so there are no milestones to choose from"
                        : $"toobusy: the milestones of {bench.Repository} cannot be read");
                    return ExitCode.Failed;
                }

                if (MilestoneStanding.Find(open, named) is not { } found)
                {
                    Say(context, $"toobusy: there is no open milestone “{named}”.");
                    Say(context, open.Count == 0
                        ? "The repository has no open milestones."
                        : "The open ones: " + string.Join(", ", MilestoneOrder.Sorted(open).Select(milestone => milestone.Title)));
                    return ExitCode.Failed;
                }

                bench.Personal.SaveMilestone(new MilestoneChoice(found.Title));
                context.Output.WriteLine($"Milestone: {found.Title} · {MilestoneScreen.Describe(found)}");
                return ExitCode.Done;
            }

            if (context.Terminal is not { } terminal)
            {
                var standing = await bench.StandAsync(cancellationToken);
                context.Output.WriteLine($"Milestone: {standing.Name}{(standing.State == MilestoneState.Gone ? " (not open any more)" : "")}");
                if (!standing.Ready)
                    context.Output.WriteLine("Choose it with `toobusy milestone <title>` or `toobusy milestone --none`.");
                return ExitCode.Done;
            }

            return await ChooseOnPageAsync(context, terminal, bench, session => session.ChooseMilestoneAsync(leavesPage: true, cancellationToken), "The milestone is left as it was.", cancellationToken);
        });
        return command;
    }

    // The model the tasks are done with by default is the choice of the user too.
    static Command CreateModelCommand(CliContext context, Option<bool> demo)
    {
        var name = new Argument<string?>("name") { Description = "The name of the model, as the assistant takes it.", Arity = ArgumentArity.ZeroOrOne };
        var own = new Option<bool>("--default") { Description = "Name no model: the assistant takes its own default." };
        var command = new Command("model", "Chooses the model the tasks are done with by default, or shows the one that is chosen.");
        command.Arguments.Add(name);
        command.Options.Add(own);
        command.SetAction(async (result, cancellationToken) =>
        {
            var named = result.GetValue(name);
            if (named is not null && result.GetValue(own))
            {
                Say(context, "toobusy: give the name of a model or `--default`, not both");
                return ExitCode.NotReady;
            }

            if (FindProject(context) is not { } root || await OpenAsync(context, root, result.GetValue(demo), ready: false, watched: context.Terminal is not null, cancellationToken) is not { } bench)
                return ExitCode.NotReady;

            if (result.GetValue(own))
            {
                bench.Personal.SaveModel(ModelChoice.AssistantsOwn);
                context.Output.WriteLine("Model: the assistant's own.");
                return ExitCode.Done;
            }

            if (named is not null)
            {
                if (named.Trim().Length == 0)
                {
                    Say(context, "toobusy: the name of the model is empty");
                    return ExitCode.Failed;
                }

                bench.Personal.SaveModel(new ModelChoice(named.Trim()));
                context.Output.WriteLine($"Model: {named.Trim()}");
                return ExitCode.Done;
            }

            if (context.Terminal is not { } terminal)
            {
                var chosen = bench.Personal.LoadModel();
                context.Output.WriteLine($"Model: {ModelScreen.Describe(chosen)}");
                if (chosen is null)
                    context.Output.WriteLine("Choose it with `toobusy model <name>` or `toobusy model --default`.");
                return ExitCode.Done;
            }

            return await ChooseOnPageAsync(context, terminal, bench, session => Task.FromResult(session.ChooseModel(leavesPage: true)), "The model is left as it was.", cancellationToken);
        });
        return command;
    }

    // And so is the effort.
    static Command CreateEffortCommand(CliContext context, Option<bool> demo)
    {
        var levels = string.Join(", ", ClaudeCodeOptions.Efforts);
        var level = new Argument<string?>("level") { Description = $"The level of effort: {levels}.", Arity = ArgumentArity.ZeroOrOne };
        var command = new Command("effort", "Chooses the effort the tasks are done with by default, or shows the one that is chosen.");
        command.Arguments.Add(level);
        command.SetAction(async (result, cancellationToken) =>
        {
            if (FindProject(context) is not { } root || await OpenAsync(context, root, result.GetValue(demo), ready: false, watched: context.Terminal is not null, cancellationToken) is not { } bench)
                return ExitCode.NotReady;

            if (result.GetValue(level) is { } named)
            {
                if (ClaudeCodeOptions.FindEffort(named) is not { } found)
                {
                    Say(context, $"toobusy: there is no effort “{named}”.");
                    Say(context, $"The levels: {levels}");
                    return ExitCode.Failed;
                }

                bench.Personal.SaveEffort(found);
                context.Output.WriteLine($"Effort: {found}");
                return ExitCode.Done;
            }

            if (context.Terminal is not { } terminal)
            {
                var chosen = bench.Personal.LoadEffort();
                context.Output.WriteLine($"Effort: {chosen ?? "not chosen"}");
                if (chosen is null)
                    context.Output.WriteLine($"Choose it with `toobusy effort <level>`: {levels}.");
                return ExitCode.Done;
            }

            return await ChooseOnPageAsync(context, terminal, bench, session => Task.FromResult(session.ChooseEffort(leavesPage: true)), "The effort is left as it was.", cancellationToken);
        });
        return command;
    }

    // A page that is opened for one question, and what is left in the terminal when it is closed: what was chosen,
    // or that nothing was.
    static async Task<int> ChooseOnPageAsync(CliContext context, TerminalDevice terminal, Workbench bench, Func<Session, Task<bool>> ask, string left, CancellationToken cancellationToken)
    {
        var session = await WithPageAsync(context, terminal, bench, ask, cancellationToken);
        context.Output.WriteLine($"toobusy · {context.Shorten(bench.Root)}");
        if (!ReportChoices(context, session, bench.Demo))
            context.Output.WriteLine(left);
        return ExitCode.Done;
    }

    // Takes the tasks of the queue one after another: on a tape in a terminal, which the tool leaves when the run is
    // over, and as plain lines without one. It does not start without a milestone to work on, a model and an effort.
    // `--task` makes it a run of one task, and `--max` one of so many tasks at most.
    static Command CreateRunCommand(CliContext context, Option<bool> demo)
    {
        var task = new Option<int?>("--task") { Description = "Do this task and no other: the number of an open task that a run would take now.", HelpName = "number" };
        var max = new Option<int?>("--max") { Description = "Take so many tasks at most, then stop.", HelpName = "count" };
        var skip = SkipOption("Leave out the tasks with this label for this run, and the tasks that wait for them; give it again for every such label, or several names separated by commas.");
        var command = new Command("run", "Takes the tasks of the queue one after another.") { task, max, skip };
        command.SetAction(async (result, cancellationToken) =>
        {
            var (only, most) = (result.GetValue(task), result.GetValue(max));
            if ((only is not null && most is not null ? "toobusy: give `--task` or `--max`, not both"
                : only <= 0 ? "toobusy: `--task` takes the number of a task, which is 1 or more"
                : most <= 0 ? "toobusy: `--max` takes a number of tasks, which is 1 or more"
                : null) is { } wrong)
            {
                Say(context, wrong);
                return ExitCode.NotReady;
            }

            // A demo shows the run in any project: where there are no settings it makes them up.
            if ((result.GetValue(demo) ? FindProject(context) : FindSettings(context)?.Path) is null)
                return ExitCode.NotReady;

            var root = ProjectLocator.FindRoot(context.Folder)!;
            if (await OpenAsync(context, root, result.GetValue(demo), ready: true, watched: context.Terminal is not null, cancellationToken) is not { } bench)
                return ExitCode.NotReady;

            if (ValidSettings(context, bench) is not { } settings)
                return ExitCode.Failed;

            // One run goes on in a working copy. A demo changes nothing there and is not held.
            if (bench.Going() is { } going)
            {
                Say(context, $"toobusy: a run is going on in this working copy already: {going.Sign.Told}");
                Say(context, "The run did not start. `toobusy status` says whether that one still goes on.");
                return ExitCode.Busy;
            }

            // Everything a run needs is checked before anything else; a demo has it all.
            if (!bench.Demo && !await ChecksPassAsync(context, bench, cancellationToken))
            {
                Say(context, "toobusy: the run did not start. `toobusy doctor` checks all of it again.");
                return ExitCode.Failed;
            }

            if (await ChosenMilestoneAsync(context, bench, cancellationToken) is not { } milestone)
                return ExitCode.Failed;

            if (bench.Personal.LoadModel() is null)
            {
                Say(context, "toobusy: the model is not chosen.");
                Say(context, "Run `toobusy model` to choose it.");
                return ExitCode.Failed;
            }

            if (bench.Personal.LoadEffort() is null)
            {
                Say(context, "toobusy: the effort is not chosen.");
                Say(context, "Run `toobusy effort` to choose it.");
                return ExitCode.Failed;
            }

            if (bench.OpenRun(settings, milestone.Title, bench.Personal.LoadModel()!, bench.Personal.LoadEffort()!, only, most, Labels(result.GetValue(skip))) is not { } run)
            {
                Say(context, NoTasks);
                return ExitCode.Failed;
            }

            if (context.Terminal is { } terminal)
                return await OnPageAsync(context, terminal, bench, session => session.RunAsync(run, cancellationToken), cancellationToken);

            // Without a terminal the log of the run is printed line by line, and stopping the tool kills the run.
            if (bench.Demo)
                context.Output.WriteLine(context.OutputPalette.Muted("Demo: nothing is changed."));
            return Code(await run.RunAsync(new PlainRun(context.Output, context.OutputPalette), cancellationToken));
        });
        return command;
    }

    // Shows the queue as a run reads it before a task, and changes nothing: the tasks a run takes now, those that
    // open later and those that are held. It needs what a run needs to read the queue, and says what is missing as
    // a run does; the model, the effort and the checks of `doctor` are not among it.
    static Command CreateQueueCommand(CliContext context, Option<bool> demo)
    {
        var json = new Option<bool>("--json") { Description = "Print the queue as JSON, for an assistant or a script." };
        var skip = SkipOption("Show the queue of a run that leaves out the tasks with this label; give it again for every such label, or several names separated by commas.");
        var command = new Command("queue", "Shows the tasks a run would take, those that open later and those that are held, and changes nothing.");
        command.Options.Add(json);
        command.Options.Add(skip);
        command.SetAction(async (result, cancellationToken) =>
        {
            // A demo shows the queue in any project: where there are no settings it makes them up.
            if ((result.GetValue(demo) ? FindProject(context) : FindSettings(context)?.Path) is null)
                return ExitCode.NotReady;

            var root = ProjectLocator.FindRoot(context.Folder)!;
            if (await OpenAsync(context, root, result.GetValue(demo), ready: true, watched: false, cancellationToken) is not { } bench)
                return ExitCode.NotReady;

            if (ValidSettings(context, bench) is not { } settings)
                return ExitCode.Failed;

            if (await ChosenMilestoneAsync(context, bench, cancellationToken) is not { } milestone)
                return ExitCode.Failed;

            try
            {
                if (await bench.ReadQueueAsync(settings, milestone.Title, Labels(result.GetValue(skip)), cancellationToken) is not var (lineup, recorded))
                {
                    Say(context, NoTasks);
                    return ExitCode.Failed;
                }

                var rules = QueueRules.Of(settings);
                if (result.GetValue(json))
                {
                    context.Output.WriteLine(QueueLook.Json(lineup, recorded, rules));
                    return ExitCode.Done;
                }

                foreach (var line in QueueLook.Lines(lineup, recorded, rules, context.OutputPalette))
                    context.Output.WriteLine(line);
                if (bench.Demo)
                    context.Output.WriteLine(context.OutputPalette.Muted("Demo: the tasks are made up."));
                return ExitCode.Done;
            }
            catch (TrackerException refused)
            {
                Say(context, $"toobusy: {refused.Message}");
                return ExitCode.Failed;
            }
        });
        return command;
    }

    // The labels a run leaves out, as `run` and `queue` take them: the option given again for every label, or with
    // several names separated by commas, as `--blocking-label` of `init` takes them.
    static Option<string[]> SkipOption(string description) => new("--skip-label") { Description = description, HelpName = "name" };

    // The names the options give, each once whatever its case.
    static List<string> Labels(string[]? given) =>
        [.. (given ?? []).SelectMany(names => names.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)).Distinct(StringComparer.OrdinalIgnoreCase)];

    // Says whether a run is going on in the working copy, and changes nothing: the task it works on and how its
    // session is opened, or, without a run, the task an earlier run left with a session. It needs a project and
    // nothing else, and reads only what a run wrote down on the machine. The exit code tells a run from none.
    static Command CreateStatusCommand(CliContext context, Option<bool> demo)
    {
        var json = new Option<bool>("--json") { Description = "Print it as JSON, for an assistant or a script." };
        var command = new Command("status", "Says whether a run is going on in this working copy, and changes nothing.");
        command.Options.Add(json);
        command.SetAction(result =>
        {
            if (FindProject(context) is not { } root)
                return ExitCode.NotReady;

            // A demo shows a made-up run that goes on, whatever the project has.
            var going = result.GetValue(demo) ? StatusLook.MadeUp : LocalFolder.Sign(root).Read();
            var left = going is null ? LocalFolder.State(root).Load() : null;
            if (result.GetValue(json))
            {
                context.Output.WriteLine(StatusLook.Json(going, left));
            }
            else
            {
                foreach (var line in StatusLook.Lines(going, left))
                    context.Output.WriteLine(line);
                if (result.GetValue(demo))
                    context.Output.WriteLine(context.OutputPalette.Muted("Demo: the run is made up."));
            }

            return going is null ? ExitCode.Done : ExitCode.Busy;
        });
        return command;
    }

    const string NoTasks = "toobusy: the `origin` remote is not a GitHub repository, so there are no tasks to take";

    // The settings of the project when they validate; otherwise it says what is wrong with them and gives null.
    static ProjectSettings? ValidSettings(CliContext context, Workbench bench)
    {
        var settings = bench.Settings.Load();
        if (settings is not { Settings: null })
            return settings!.Settings;

        Say(context, "toobusy: the settings of this project are not valid.");
        foreach (var problem in settings.Errors)
            context.Error.WriteLine(context.ErrorPalette.Error(problem.Describe(SettingsFile.DisplayPath)));
        return null;
    }

    // The milestone to work on, when the user has chosen one that is open, or to work without one; otherwise it
    // says how to choose it and gives null.
    static async Task<MilestoneChoice?> ChosenMilestoneAsync(CliContext context, Workbench bench, CancellationToken cancellationToken)
    {
        var standing = await bench.StandAsync(cancellationToken);
        if (standing.Ready)
            return standing.Choice;

        Say(context, standing.State == MilestoneState.Gone
            ? $"toobusy: the milestone “{standing.Name}” is not open any more."
            : "toobusy: the milestone to work on is not chosen.");
        Say(context, "Run `toobusy milestone` to choose it.");
        return null;
    }

    // Checks everything a run needs, a line for each check, and changes nothing. A demo checks a made-up machine
    // that has it all.
    static Command CreateDoctorCommand(CliContext context, Option<bool> demo)
    {
        var command = new Command("doctor", "Checks everything a run needs, and says how to fix what is missing.");
        command.SetAction(async (result, cancellationToken) =>
        {
            if (FindProject(context) is not { } root || await OpenAsync(context, root, result.GetValue(demo), ready: true, watched: false, cancellationToken) is not { } bench)
                return ExitCode.NotReady;

            IReadOnlyList<CheckResult> checks;
            using (var list = new CheckList(context.Output, context.OutputPalette, context.Output, terminal: context.Terminal))
                checks = await bench.Checkup.RunAsync(list, cancellationToken);

            if (bench.Demo)
                context.Output.WriteLine(context.OutputPalette.Muted("Demo: the checks were made up."));
            return checks.Any(check => check.State == CheckState.Failed) ? ExitCode.Failed : ExitCode.Done;
        });
        return command;
    }

    // The checks before a run: only those that failed are told, with their fixes, as the errors they are. In a
    // terminal the check that is running is shown while it runs.
    static async Task<bool> ChecksPassAsync(CliContext context, Workbench bench, CancellationToken cancellationToken)
    {
        using var list = new CheckList(context.Error, context.ErrorPalette, context.Output, context.OutputPalette, context.Terminal, failuresOnly: true);
        return (await bench.Checkup.RunAsync(list, cancellationToken)).All(check => check.State != CheckState.Failed);
    }

    // Tells how a run works, for the owner of a project and for the assistant that helps them. It reads nothing but
    // the settings, so it works everywhere `--help` does; where there are none to read, the labels have no names.
    static Command CreateAboutCommand(CliContext context)
    {
        var command = new Command("about", "Tells how a run works: what a session is told, and what toobusy does around it.");
        command.SetAction(_ =>
        {
            var settings = ProjectLocator.FindRoot(context.Folder) is { } root ? new SettingsFile(root).Load()?.Settings : null;
            context.Output.Write(About.Text(settings is null ? null : QueueRules.Of(settings), RunPolicy.Default, settings?.Skill, settings?.Commands, settings?.Branch, settings?.Assistant.PermissionMode));
            return ExitCode.Done;
        });
        return command;
    }

    // The page of toobusy, for the menu or for a run alone, and what is left in the terminal when it is closed. A
    // run has left its tape there already, with its bar, its tasks and how it went: only what else was done on the
    // page is reported under it.
    static async Task<int> OnPageAsync(CliContext context, TerminalDevice terminal, Workbench bench, Func<Session, Task> open, CancellationToken cancellationToken)
    {
        var session = await WithPageAsync(context, terminal, bench, open, cancellationToken);
        var failed = false;
        if (session.Runs.Count == 0 || session.Setup is not null || session.Chose)
        {
            context.Output.WriteLine($"toobusy · {context.Shorten(bench.Root)}");
            failed = session.Setup is not null && ReportSetup(context, session.Setup, bench.Demo);
            ReportChoices(context, session, bench.Demo);
            NameAbout(context, session.Setup, bench.Demo);
        }

        if (bench.Demo && session.Runs.Count > 0)
            context.Output.WriteLine(context.OutputPalette.Muted("Demo: the tasks and the sessions were made up, and nothing was changed."));

        if (session.Runs is [.., var last])
            return last is { } result ? Code(result) : ExitCode.Killed;
        return failed && session.Setup!.Outcome == SetupOutcome.Failed ? ExitCode.Failed : ExitCode.Done;
    }

    // The exit code of a run: a run that stopped because something was wrong failed, and so did one that was killed.
    static int Code(RunResult result) => result.End switch
    {
        RunEnd.Problem => ExitCode.Failed,
        RunEnd.Killed => ExitCode.Killed,
        _ => ExitCode.Done,
    };

    // The answers of a setup and how it ended; a setup that never ended was left. Tells whether it failed.
    static bool ReportSetup(CliContext context, SetupResult? setup, bool demo, bool unasked = false)
    {
        var palette = context.OutputPalette;
        setup ??= new SetupResult(SetupOutcome.Left, []);
        foreach (var answer in setup.Answers)
            context.Output.WriteLine($"{palette.Success("✔")} {answer.Label,-16} {answer.Value}");

        // No question asks for the commands around a task, the branch and the permission mode: what the options made of
        // them is said after the answers.
        foreach (var setting in setup.Unasked)
            context.Output.WriteLine($"{palette.Success("✔")} {setting.Label,-16} {setting.Value}");

        // A setup that asked nothing showed nobody what it was about to do on GitHub: it says what it did. A demo
        // did none of it.
        foreach (var done in unasked && !demo ? setup.Done : [])
            context.Output.WriteLine(done);

        var (failed, text) = Session.Ending(setup, demo);
        context.Output.WriteLine(
            failed ? $"{palette.Error("✘")} {text}"
            : setup.Outcome == SetupOutcome.NothingToChange ? text
            : demo ? palette.Muted(text)
            : $"{palette.Success("✔")} {text}");
        return failed;
    }

    // The last line after a setup that wrote the settings: where the owner and their assistant read how a run
    // works, now that the project is ready for one.
    static void NameAbout(CliContext context, SetupResult? setup, bool demo)
    {
        if (setup is { Outcome: SetupOutcome.Written, SettingsWritten: true } && !demo)
            context.Output.WriteLine(context.OutputPalette.Muted("`toobusy about` tells how a run works, for you and for your assistant."));
    }

    // What was chosen on the page: the milestone, the model, the effort; false when nothing was.
    static bool ReportChoices(CliContext context, Session session, bool demo)
    {
        (string Label, string? Value)[] choices =
        [
            ("Milestone", session.Chosen is { } milestone ? milestone.Title ?? "none" : null),
            ("Model", session.ChosenModel is { } model ? ModelScreen.Describe(model) : null),
            ("Effort", session.ChosenEffort),
        ];
        var chosen = choices.Where(choice => choice.Value is not null).ToList();
        foreach (var (label, value) in chosen)
            context.Output.WriteLine($"{context.OutputPalette.Success("✔")} {label,-16} {value}");
        if (demo && chosen.Count > 0)
            context.Output.WriteLine(context.OutputPalette.Muted("Demo: what was chosen is not remembered."));
        return chosen.Count > 0;
    }

    // Opens the page of toobusy for what is done on it, and closes it when that is over or the page was left: the
    // session it gives remembers what was done, for the report each command leaves in the terminal. A run that was
    // killed on the page is told as one, even where one more Ctrl+C stopped the tool from outside while the run did
    // what the kill does not cut.
    static async Task<Session> WithPageAsync(CliContext context, TerminalDevice terminal, Workbench bench, Func<Session, Task> open, CancellationToken cancellationToken)
    {
        using var page = new Page(context.Output, context.OutputPalette, terminal, $"toobusy · {context.Shorten(bench.Root)}");
        var session = new Session(page, bench);
        try
        {
            await open(session);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested || session.Runs is [.., { End: RunEnd.Killed }])
        {
            // The page was left.
        }

        return session;
    }

    // What the commands work with in the project. Outside a demo the choices of the user need a place on the
    // machine; without one it says so and gives null. `watched` says whether a page shows what is read while it is
    // read.
    static async Task<Workbench?> OpenAsync(CliContext context, string root, bool demo, bool ready, bool watched, CancellationToken cancellationToken)
    {
        if (!demo && context.PersonalFolder is null)
        {
            Say(context, "toobusy: there is no home folder to keep your choices in");
            return null;
        }

        return await Workbench.OpenAsync(root, demo, context.Processes, context.PersonalFolder ?? "", ready, watched, context.Clock, context.Home, context.Variables, cancellationToken);
    }

    // The root of the project around the folder; without a project it says so and gives null.
    static string? FindProject(CliContext context)
    {
        var root = ProjectLocator.FindRoot(context.Folder);
        if (root is null)
            Say(context, "toobusy: not inside a git repository");
        return root;
    }

    // The settings file of the project; without a project or without the file it says so and gives null.
    static SettingsFile? FindSettings(CliContext context)
    {
        if (FindProject(context) is not { } root)
            return null;

        var file = new SettingsFile(root);
        if (file.Exists)
            return file;

        Say(context, "toobusy: this project is not set up yet.");
        Say(context, "Run `toobusy init` to set it up.");

        // Nobody is at a terminal to be asked: who reads this may be an assistant.
        if (context.Terminal is null)
            Say(context, ForAnAssistant);
        return null;
    }

    // A line where errors go. It gives no exit code: a command that fails returns its own, where it fails, so that
    // the code of every failure is the one SPEC gives it and not the one a helper has.
    static void Say(CliContext context, string message) =>
        context.Error.WriteLine(context.ErrorPalette.Message(message));
}
