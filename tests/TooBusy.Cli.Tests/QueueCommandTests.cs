using TooBusy.Cli.Terminal;
using TooBusy.Core.Processes;

namespace TooBusy.Cli.Tests;

// `toobusy queue`: what a run takes now, what opens later and what is held, as text and as JSON.
public sealed class QueueCommandTests : CliAppTests
{
    // The open tasks of that project as `gh` gives them, among them one for every reason a task is held for.
    const string OpenTasks = "total\t11\n"
        + "7\tChoose the format of the export\thttps://github.com/acme/rocket/issues/7\tTodo\t\t\t\tfeature\tneeds-owner\n"
        + "9\tDraft of the new page\thttps://github.com/acme/rocket/issues/9\tTodo\t\t\t\tfeature\tdraft\n"
        + "10\tTidy the styles\thttps://github.com/acme/rocket/issues/10\tTodo\t\t\t\tchore\n"
        + "12\tExport the orders as a file\thttps://github.com/acme/rocket/issues/12\tTodo\t\t\t\tfeature\n"
        + "14\tRename the settings page\thttps://github.com/acme/rocket/issues/14\tIn Progress\t\t\t\tfeature\n"
        + "15\tFix the rounding of a discount\thttps://github.com/acme/rocket/issues/15\tTodo\t\t\t\tbug\n"
        + "16\tCount the visits\thttps://github.com/acme/rocket/issues/16\t\t\t\t\tfeature\n"
        + "20\tDeploy\thttps://github.com/acme/rocket/issues/20\tTodo\t12\t\t\tfeature\n"
        + "21\tWrite the export down\thttps://github.com/acme/rocket/issues/21\tTodo\t7\t\t\tfeature\n"
        + "22\tTranslate the manual\thttps://github.com/acme/rocket/issues/22\tTodo\t\t99\t\tfeature\n"
        + "71\tPaint the windows yellow\thttps://github.com/acme/rocket/issues/71\tTodo\t\t\t\tfeature\tinterrupted\n";

    [Fact]
    public async Task QueueTakesATaskOnlyFromTheStatusTheSettingsNameAndAboutNamesTheStatusesSo()
    {
        GitRepository();
        WriteSettings(SettingsWithABoard.Replace("[queue.labels]", "[tracker.statuses]\ntodo = \"Ready\"\ndone = \"Shipped\"\n\n[queue.labels]", StringComparison.Ordinal));
        await RunAsync("milestone", "--none");
        var processes = Tracker("total\t3\n"
            + "12\tExport the orders as a file\thttps://github.com/acme/rocket/issues/12\tready\t\t\t\tfeature\n"
            + "14\tRename the settings page\thttps://github.com/acme/rocket/issues/14\tTodo\t\t\t\tfeature\n"
            + "15\tFix the rounding of a discount\thttps://github.com/acme/rocket/issues/15\tShipped\t\t\t\tbug\n");

        var (exit, output, error) = await RunAsync(Context(processes), ["queue"]);

        Assert.Equal("", error);
        Assert.Equal(0, exit);
        Assert.Equal(
            """
            Ready
              #12 Export the orders as a file

            Later
              no tasks

            Held
              #14 Rename the settings page        its status is Todo
              #15 Fix the rounding of a discount  its status is Shipped

            """.ReplaceLineEndings(),
            output);

        (_, var about, _) = await RunAsync(Context(processes), ["about"]);
        Assert.Contains("a task is taken only from the status `Ready`. The statuses a run knows are named below as the settings of the project call them, `Ready`, `In Progress` and `Shipped`.", about, StringComparison.Ordinal);
    }

    [Fact]
    public async Task QueueShowsWhatARunTakesNowWhatOpensLaterAndWhatIsHeldWithEveryReason()
    {
        GitRepository();
        WriteSettings(SettingsWithABoard);
        await RunAsync("milestone", "--none");
        var processes = Tracker(OpenTasks);

        var (exit, output, error) = await RunAsync(Context(processes), ["queue"]);

        Assert.Equal("", error);
        Assert.Equal(0, exit);
        Assert.Equal(
            """
            Ready
              #71 Paint the windows yellow         interrupted
              #12 Export the orders as a file
              #15 Fix the rounding of a discount

            Later
              #20 Deploy                           waits for #12

            Held
              #7  Choose the format of the export  it waits for the owner: it has the needs-owner label
              #9  Draft of the new page            it has the draft label
              #10 Tidy the styles                  it has none of the labels to take: feature, bug
              #14 Rename the settings page         its status is In Progress
              #16 Count the visits                 it is not on the board
              #21 Write the export down            it waits for #7 (it waits for the owner: it has the needs-owner label)
              #22 Translate the manual             it waits for #99 (not among these tasks)

            """.ReplaceLineEndings(),
            output);

        // It reads the tasks and nothing else: no check of `doctor` is made, nothing is changed in the tracker,
        // nothing is asked of the assistant, and nothing is left on the machine. The model and the effort are not
        // chosen here, and it does not ask for them.
        Assert.Equal(["git", "gh"], processes.Asked.Select(asked => asked.Command));
        Assert.Equal(["api", "graphql"], processes.Asked[1].Arguments.Take(2));
        Assert.False(Directory.Exists(Path.Combine(folder.FullName, ".toobusy", "local")));
    }

    [Fact]
    public async Task QueueStartsWithTheTaskAnEarlierRunLeftWithASession()
    {
        GitRepository();
        WriteSettings(SettingsWithABoard);
        await RunAsync("milestone", "--none");

        // The record of a task that stands as one that is worked on: a run goes on with it whatever its status.
        var local = Directory.CreateDirectory(Path.Combine(folder.FullName, ".toobusy", "local"));
        var record = """{"task":14,"session":"abc12345","since":"2030-01-01T09:00:00.0000000Z","state":"working"}""";
        File.WriteAllText(Path.Combine(local.FullName, "session.json"), record);

        var (exit, output, _) = await RunAsync(Context(Tracker(OpenTasks)), ["queue"]);

        Assert.Equal(0, exit);
        Assert.StartsWith(
            """
            Ready
              #14 Rename the settings page         left with a session
              #71 Paint the windows yellow         interrupted
              #12 Export the orders as a file

            """.ReplaceLineEndings(),
            output,
            StringComparison.Ordinal);
        Assert.DoesNotContain("its status is In Progress", output, StringComparison.Ordinal);
        Assert.Equal(record, File.ReadAllText(Path.Combine(local.FullName, "session.json")));
        Assert.Equal(["session.json"], local.GetFiles().Select(file => file.Name));
    }

    [Fact]
    public async Task QueueDoesNotPutFirstATaskOfARecordThatARunWouldNotGoOnWith()
    {
        GitRepository();
        WriteSettings(SettingsWithABoard);
        await RunAsync("milestone", "--none");
        var local = Directory.CreateDirectory(Path.Combine(folder.FullName, ".toobusy", "local"));
        File.WriteAllText(Path.Combine(local.FullName, "session.json"), """{"task":9,"session":"abc12345","since":"2030-01-01T09:00:00.0000000Z","state":"working"}""");

        var (_, output, _) = await RunAsync(Context(Tracker(OpenTasks)), ["queue"]);

        Assert.DoesNotContain(QueueLook.Left, output, StringComparison.Ordinal);
        Assert.Contains("  #9  Draft of the new page            it has the draft label" + Environment.NewLine, output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task QueueAsJsonHasTheThreeListsWithWhatATaskWaitsForAndWhyItIsHeld()
    {
        GitRepository();
        WriteSettings(SettingsWithABoard);
        await RunAsync("milestone", "--none");
        var local = Directory.CreateDirectory(Path.Combine(folder.FullName, ".toobusy", "local"));
        File.WriteAllText(Path.Combine(local.FullName, "session.json"), """{"task":14,"session":"abc12345","since":"2030-01-01T09:00:00.0000000Z","state":"working"}""");

        var (exit, output, error) = await RunAsync(Context(Tracker(OpenTasks)), ["queue", "--json"]);

        Assert.Equal("", error);
        Assert.Equal(0, exit);
        using var queue = System.Text.Json.JsonDocument.Parse(output);
        var ready = queue.RootElement.GetProperty("ready").EnumerateArray().ToList();
        Assert.Equal([14, 71, 12, 15], ready.Select(task => task.GetProperty("number").GetInt32()));
        Assert.Equal(["session", "interrupted", null, null], ready.Select(task => task.TryGetProperty("first", out var first) ? first.GetString() : null));
        Assert.Equal("Paint the windows yellow", ready[1].GetProperty("title").GetString());
        Assert.Equal("https://github.com/acme/rocket/issues/71", ready[1].GetProperty("url").GetString());
        Assert.Equal(["feature", "interrupted"], ready[1].GetProperty("labels").EnumerateArray().Select(label => label.GetString()));

        var later = Assert.Single(queue.RootElement.GetProperty("later").EnumerateArray());
        Assert.Equal(20, later.GetProperty("number").GetInt32());
        Assert.Equal("Deploy", later.GetProperty("title").GetString());
        Assert.Equal("https://github.com/acme/rocket/issues/20", later.GetProperty("url").GetString());
        Assert.Equal(["feature"], later.GetProperty("labels").EnumerateArray().Select(label => label.GetString()));
        Assert.Equal([12], later.GetProperty("waitsFor").EnumerateArray().Select(number => number.GetInt32()));

        var held = queue.RootElement.GetProperty("held").EnumerateArray().ToList();
        Assert.Equal([7, 9, 10, 16, 21, 22], held.Select(task => task.GetProperty("number").GetInt32()));
        Assert.Equal("it waits for the owner: it has the needs-owner label", held[0].GetProperty("reason").GetString());
        Assert.Equal(["feature", "needs-owner"], held[0].GetProperty("labels").EnumerateArray().Select(label => label.GetString()));
        Assert.Equal("https://github.com/acme/rocket/issues/7", held[0].GetProperty("url").GetString());
    }

    [Fact]
    public async Task QueueSaysOfEveryListThatIsEmptyThatItHasNoTasks()
    {
        GitRepository();
        WriteSettings(Settings);
        await RunAsync("milestone", "--none");

        var (exit, output, _) = await RunAsync(Context(Tracker("total\t0\n")), ["queue"]);

        Assert.Equal(0, exit);
        Assert.Equal(
            """
            Ready
              no tasks

            Later
              no tasks

            Held
              no tasks

            """.ReplaceLineEndings(),
            output);

        var (_, json, _) = await RunAsync(Context(Tracker("total\t0\n")), ["queue", "--json"]);

        using var queue = System.Text.Json.JsonDocument.Parse(json);
        Assert.Equal([0, 0, 0], queue.RootElement.EnumerateObject().Select(list => list.Value.GetArrayLength()));
        Assert.Equal(["ready", "later", "held"], queue.RootElement.EnumerateObject().Select(list => list.Name));
    }

    [Fact]
    public async Task QueuePaintsTheNamesOfTheListsTheNumbersAndWhatIsSaidOfATask()
    {
        GitRepository();
        WriteSettings(SettingsWithABoard);
        await RunAsync("milestone", "--none");
        var palette = Palette.Dark;

        var (_, output, _) = await RunAsync(Context(Tracker(OpenTasks)) with { OutputPalette = palette }, ["queue"]);

        Assert.StartsWith(palette.Paint(Tone.Strong, "Ready") + Environment.NewLine, output, StringComparison.Ordinal);
        Assert.Contains($"  {palette.Muted("#71")} Paint the windows yellow         {palette.Warning("interrupted")}" + Environment.NewLine, output, StringComparison.Ordinal);
        Assert.Contains($"  {palette.Muted("#12")} Export the orders as a file" + Environment.NewLine, output, StringComparison.Ordinal);
        Assert.Contains($"  {palette.Muted("#20")} Deploy                           {palette.Muted("waits for #12")}" + Environment.NewLine, output, StringComparison.Ordinal);
        Assert.Contains($"  {palette.Muted("#9 ")} Draft of the new page            {palette.Muted("it has the draft label")}" + Environment.NewLine, output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task QueueCutsATitleThatIsTooLongAndKeepsItWholeInJson()
    {
        GitRepository();
        WriteSettings(Settings);
        await RunAsync("milestone", "--none");
        var title = new string('a', 70);
        var tasks = $"total\t1\n12\t{title}\thttps://github.com/acme/rocket/issues/12\t\t\t\t\tmanual\n";

        var (_, output, _) = await RunAsync(Context(Tracker(tasks)), ["queue"]);
        var (_, json, _) = await RunAsync(Context(Tracker(tasks)), ["queue", "--json"]);

        Assert.Contains($"  #12 {new string('a', 59)}…  it has the manual label" + Environment.NewLine, output, StringComparison.Ordinal);
        Assert.Contains($"\"title\": \"{title}\"", json, StringComparison.Ordinal);
    }

    [Fact]
    public async Task QueueSaysWhatTheTrackerSaysWhenTheTasksCannotBeRead()
    {
        GitRepository();
        WriteSettings(Settings);
        await RunAsync("milestone", "--none");
        var processes = new ScriptedProcesses(command => command == "git"
            ? new ProcessResult(ProcessStatus.Exited, 0, "https://github.com/acme/rocket.git\n", "")
            : new ProcessResult(ProcessStatus.Exited, 1, "", "HTTP 401: Bad credentials\n"));

        var (exit, output, error) = await RunAsync(Context(processes), ["queue", "--json"]);

        Assert.Equal(1, exit);
        Assert.Equal("", output);
        Assert.Equal("toobusy: The tasks of acme/rocket cannot be read. HTTP 401: Bad credentials" + Environment.NewLine, error);
    }

    [Fact]
    public async Task QueueIsNotShownBeforeTheMilestoneIsChosen()
    {
        GitRepository();
        WriteSettings(Settings);

        var (exit, output, error) = await RunAsync(Context(Tracker(OpenTasks)), ["queue"]);

        Assert.Equal(1, exit);
        Assert.Equal("", output);
        Assert.Equal(
            "toobusy: the milestone to work on is not chosen." + Environment.NewLine + "Run `toobusy milestone` to choose it." + Environment.NewLine,
            error);
    }

    [Fact]
    public async Task QueueIsNotShownForAMilestoneThatIsNotOpenAnyMore()
    {
        GitRepository();
        WriteSettings(Settings);
        await RunAsync(Context(GitHub(milestones: "1\tv2\t\t3\n2\tv3\t\t0\n")), ["milestone", "v2"]);

        var (exit, output, error) = await RunAsync(Context(GitHub(milestones: "1\tv3\t\t0\n", Labels)), ["queue"]);

        Assert.Equal(1, exit);
        Assert.Equal("", output);
        Assert.Equal(
            "toobusy: the milestone “v2” is not open any more." + Environment.NewLine + "Run `toobusy milestone` to choose it." + Environment.NewLine,
            error);
    }

    [Fact]
    public async Task QueueRefusesSettingsThatAreNotValid()
    {
        GitRepository();
        WriteSettings(Settings.Replace("blocking = [\"manual\"]", "blocking = \"manual\"", StringComparison.Ordinal));
        await RunAsync("milestone", "--none");

        var (exit, output, error) = await RunAsync(Context(Tracker(OpenTasks)), ["queue"]);

        Assert.Equal(1, exit);
        Assert.Equal("", output);
        Assert.StartsWith("toobusy: the settings of this project are not valid." + Environment.NewLine + ".toobusy/settings.toml:7: queue.labels.blocking: ", error, StringComparison.Ordinal);
    }

    [Fact]
    public async Task QueueOfAProjectWithoutAGitHubOriginHasNoTasks()
    {
        GitRepository();
        WriteSettings(Settings);
        await RunAsync("milestone", "--none");
        var processes = new ScriptedProcesses(command => new ProcessResult(ProcessStatus.Exited, 0, command == "git" ? "git@example.com:acme/rocket.git\n" : "", ""));

        var (exit, output, error) = await RunAsync(Context(processes), ["queue"]);

        Assert.Equal(1, exit);
        Assert.Equal("", output);
        Assert.Equal("toobusy: the `origin` remote is not a GitHub repository, so there are no tasks to take" + Environment.NewLine, error);
    }

    // The menu of a demo takes a moment over counting its made-up tasks, so that the count can be looked at; the
    // queue has no page to look at a wait on, and prints them at once.
    [Fact]
    public async Task QueueOfADemoWaitsForNothing()
    {
        var clock = new InstantClock();

        var (exit, _, _) = await RunAsync(Context(null) with { Clock = clock }, ["queue", "--demo"]);

        Assert.Equal(0, exit);
        Assert.Equal(InstantClock.Start, clock.Now);
    }

    // The fifteen tasks of the made-up shop, as the first run of a demo finds them, in a project that has a queue
    // of its own: the task a run was killed over stands first as one left with a session.
    [Fact]
    public async Task QueueOfADemoShowsTheMadeUpTasksOfADemoOfARun()
    {
        GitRepository();
        WriteSettings(SettingsWithABoard);
        var processes = new ScriptedProcesses();

        var (exit, output, error) = await RunAsync(Context(processes), ["queue", "--demo"]);

        Assert.Equal("", error);
        Assert.Equal(0, exit);
        Assert.Empty(processes.Asked);
        Assert.Equal(
            """
            Ready
              #100 Rename the settings page                       left with a session
              #101 Show the total of an order in its header
              #102 Export the orders as a CSV file
              #103 Fix the rounding of a discount
              #104 Update the dependencies
              #106 Let a customer save a cart for later
              #107 Send an email when an order ships
              #108 Find products whatever accents the search has
              #109 Show the stock of a product on its page
              #110 Add a page of frequently asked questions
              #111 Load the list of orders page by page

            Later
              #105 Describe the export in the manual              waits for #102

            Held
              #112 Choose the payment provider                    it waits for the owner: it has the manual label
              #113 Redesign the checkout page                     it has the design label
              #114 Add a dark theme                               its status is In Progress
            Demo: the tasks are made up.

            """.ReplaceLineEndings(),
            output);
        Assert.Equal(SettingsWithABoard, File.ReadAllText(Path.Combine(folder.FullName, ".toobusy", "settings.toml")));
        Assert.False(Directory.Exists(Path.Combine(folder.FullName, ".toobusy", "local")));

        // What an assistant reads has nothing but the lists, with the labels and the addresses of the tasks.
        var (_, json, _) = await RunAsync("queue", "--demo", "--json");

        using var queue = System.Text.Json.JsonDocument.Parse(json);
        var first = queue.RootElement.GetProperty("ready")[0];
        Assert.Equal(100, first.GetProperty("number").GetInt32());
        Assert.Equal("session", first.GetProperty("first").GetString());
        Assert.Equal("https://github.com/example/demo-project/issues/100", first.GetProperty("url").GetString());
        Assert.Equal(["chore"], first.GetProperty("labels").EnumerateArray().Select(label => label.GetString()));
        Assert.Equal(["design", "feature"], queue.RootElement.GetProperty("held")[1].GetProperty("labels").EnumerateArray().Select(label => label.GetString()).Order());
    }

    [Fact]
    public async Task QueueShowsTheQueueOfARunThatSkipsLabelsAsTextAndAsJson()
    {
        var (exit, output, _) = await RunAsync("queue", "--demo", "--skip-label", "feature,docs");

        // The task of the record has no label the run skips.
        Assert.Equal(0, exit);
        Assert.StartsWith(
            """
            Ready
              #100 Rename the settings page                       left with a session
              #103 Fix the rounding of a discount
              #104 Update the dependencies
              #108 Find products whatever accents the search has
              #111 Load the list of orders page by page

            Later
              no tasks

            Held
              #101 Show the total of an order in its header       it has the feature label, which this run skips
              #102 Export the orders as a CSV file                it has the feature label, which this run skips
              #105 Describe the export in the manual              it has the docs label, which this run skips
              #106 Let a customer save a cart for later           it has the feature label, which this run skips
              #107 Send an email when an order ships              it has the feature label, which this run skips
              #109 Show the stock of a product on its page        it has the feature label, which this run skips
              #110 Add a page of frequently asked questions       it has the docs label, which this run skips
              #112 Choose the payment provider                    it waits for the owner: it has the manual label
              #113 Redesign the checkout page                     it has the design label
              #114 Add a dark theme                               it has the feature label, which this run skips

            """.ReplaceLineEndings(),
            output,
            StringComparison.Ordinal);

        // What waits for a task that is skipped is held with it.
        (_, output, _) = await RunAsync("queue", "--demo", "--skip-label", "feature", "--json");

        using var queue = System.Text.Json.JsonDocument.Parse(output);
        var held = queue.RootElement.GetProperty("held").EnumerateArray().ToDictionary(task => task.GetProperty("number").GetInt32(), task => task.GetProperty("reason").GetString());
        Assert.Equal("it waits for #102 (it has the feature label, which this run skips)", held[105]);
    }
}
