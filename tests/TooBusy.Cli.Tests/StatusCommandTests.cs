using System.Text.RegularExpressions;
using TooBusy.Core.Processes;
using TooBusy.Core.Run;

namespace TooBusy.Cli.Tests;

// `toobusy status`: whether a run goes on in the working copy, as text and as JSON.
public sealed class StatusCommandTests : CliAppTests
{
    [Fact]
    public async Task StatusSaysThatNoRunIsGoingOnAndNeedsNothingButAProject()
    {
        GitRepository();
        var processes = new ScriptedProcesses(_ => new ProcessResult(ProcessStatus.NotFound, 0, "", ""));

        var (exit, output, error) = await RunAsync(Context(processes) with { PersonalFolder = null }, ["status"]);

        Assert.Equal(0, exit);
        Assert.Equal("", error);
        Assert.Equal("No run is going on." + Environment.NewLine, output);

        // No settings, no command of the machine, and nothing is left behind.
        Assert.Empty(processes.Asked);
        Assert.False(Directory.Exists(Path.Combine(folder.FullName, ".toobusy")));

        var (code, json, _) = await RunAsync("status", "--json");
        Assert.Equal(0, code);
        Assert.Equal("{\n  \"running\": false\n}", json.ReplaceLineEndings("\n").TrimEnd());
    }

    [Fact]
    public async Task StatusNamesTheTasksAnEarlierRunLeftInItsWorktrees()
    {
        GitRepository();
        foreach (var (name, state) in ((string, string)[])[("14", "paused"), ("15", "working")])
        {
            var local = Directory.CreateDirectory(Path.Combine(folder.FullName, ".toobusy", "local", "worktrees", name, ".toobusy", "local"));
            File.WriteAllText(Path.Combine(local.FullName, "session.json"), $$"""{"task":{{name}},"session":"abc12345","since":"2030-01-01T09:00:00.0000000Z","state":"{{state}}"}""");
        }

        var (exit, output, _) = await RunAsync("status");

        Assert.Equal(0, exit);
        Assert.Contains("An earlier run left #14 with a session", output, StringComparison.Ordinal);
        Assert.Contains("An earlier run left #15 with a session", output, StringComparison.Ordinal);

        var (_, json, _) = await RunAsync("status", "--json");
        Assert.Equal(
            """
            {
              "running": false,
              "left": {
                "task": 14,
                "state": "paused"
              },
              "beside": [
                {
                  "task": 15,
                  "state": "working"
                }
              ]
            }
            """.ReplaceLineEndings("\n"),
            json.ReplaceLineEndings("\n").TrimEnd());
    }

    [Fact]
    public async Task StatusNamesTheTaskAnEarlierRunLeftWithASession()
    {
        GitRepository();
        var local = Directory.CreateDirectory(Path.Combine(folder.FullName, ".toobusy", "local"));
        var record = """{"task":14,"session":"abc12345","since":"2030-01-01T09:00:00.0000000Z","state":"paused"}""";
        File.WriteAllText(Path.Combine(local.FullName, "session.json"), record);

        var (exit, output, _) = await RunAsync("status");

        Assert.Equal(0, exit);
        Assert.Equal(
            "No run is going on." + Environment.NewLine + "An earlier run left #14 with a session: the next run goes on with it." + Environment.NewLine,
            output);

        var (code, json, _) = await RunAsync("status", "--json");
        Assert.Equal(0, code);
        Assert.Equal(
            """
            {
              "running": false,
              "left": {
                "task": 14,
                "state": "paused"
              }
            }
            """.ReplaceLineEndings("\n"),
            json.ReplaceLineEndings("\n").TrimEnd());
        Assert.Equal(record, File.ReadAllText(Path.Combine(local.FullName, "session.json")));
        Assert.Equal(["session.json"], local.GetFiles().Select(file => file.Name));
    }

    [Fact]
    public async Task StatusNamesTheTaskOfARunThatGoesOnAndHowItsSessionIsOpened()
    {
        GitRepository();
        GoingRun(Working);

        // The record of the task is there too, as it is while a session works: the run is what is told.
        File.WriteAllText(Path.Combine(folder.FullName, ".toobusy", "local", "session.json"), """{"task":12,"session":"1a2b3c4d","since":"2030-01-01T09:00:00.0000000Z","state":"working"}""");

        var (exit, output, error) = await RunFromAsync(folder.CreateSubdirectory("src").FullName, "status");

        Assert.Equal(3, exit);
        Assert.Equal("", error);
        Assert.Equal("A run is going on: #12 Export the orders as a file · claude attach 1a2b3c4d" + Environment.NewLine, output);

        var (code, json, _) = await RunAsync("status", "--json");
        Assert.Equal(3, code);
        using var told = System.Text.Json.JsonDocument.Parse(json);
        Assert.Equal(
            ["running", "process", "since", "phase", "task", "session"],
            told.RootElement.EnumerateObject().Select(property => property.Name));
        Assert.True(told.RootElement.GetProperty("running").GetBoolean());
        Assert.Equal(Environment.ProcessId, told.RootElement.GetProperty("process").GetInt32());
        Assert.Matches(@"^\d{4}-\d\d-\d\dT\d\d:\d\d:\d\dZ$", told.RootElement.GetProperty("since").GetString());
        Assert.Equal("working", told.RootElement.GetProperty("phase").GetString());
        Assert.Equal(
            """{"number":12,"title":"Export the orders as a file","url":"https://github.com/acme/rocket/issues/12"}""",
            Regex.Replace(told.RootElement.GetProperty("task").GetRawText(), @"\s+(?=[""}])|(?<=[{:,])\s+", ""));
        Assert.Equal("claude attach 1a2b3c4d", told.RootElement.GetProperty("session").GetString());
    }

    [Fact]
    public async Task StatusSaysThatARunIsBetweenTasksOrWaitsForAUsageLimit()
    {
        GitRepository();
        GoingRun(new RunSign(RunPhase.Preparing));

        var (exit, output, _) = await RunAsync("status");
        Assert.Equal(3, exit);
        Assert.Equal("A run is going on: between tasks" + Environment.NewLine, output);
        Assert.Contains("\"phase\": \"between-tasks\"", (await RunAsync("status", "--json")).Output, StringComparison.Ordinal);

        GoingRun(Working with { Phase = RunPhase.WaitingForLimit, Until = new DateTimeOffset(2030, 1, 1, 14, 5, 0, TimeSpan.Zero) });

        (exit, output, _) = await RunAsync("status");
        Assert.Equal(3, exit);
        Assert.Equal(
            "A run is going on: #12 Export the orders as a file · waits for a usage limit until 2030-01-01 14:05 UTC · claude attach 1a2b3c4d" + Environment.NewLine,
            output);
        var json = (await RunAsync("status", "--json")).Output;
        Assert.Contains("\"phase\": \"waiting-for-limit\"", json, StringComparison.Ordinal);
        Assert.Contains("\"until\": \"2030-01-01T14:05:00Z\"", json, StringComparison.Ordinal);
    }

    [Fact]
    public async Task StatusDoesNotCountWhatARunThatDiedLeftBehind()
    {
        GitRepository();
        var local = Directory.CreateDirectory(Path.Combine(folder.FullName, ".toobusy", "local"));

        // No process of the machine has this number.
        var left = """{"process":2147483646,"since":"2030-01-01T09:00:00.0000000Z","phase":"working","task":12,"title":"Export the orders as a file","open":"claude attach 1a2b3c4d"}""";
        File.WriteAllText(Path.Combine(local.FullName, "run.json"), left);
        File.WriteAllText(Path.Combine(local.FullName, "session.json"), """{"task":12,"session":"1a2b3c4d","since":"2030-01-01T09:00:00.0000000Z","state":"working"}""");

        var (exit, output, _) = await RunAsync("status");

        Assert.Equal(0, exit);
        Assert.Equal(
            "No run is going on." + Environment.NewLine + "An earlier run left #12 with a session: the next run goes on with it." + Environment.NewLine,
            output);

        // It changes nothing: what was left is the next run's to put its own over.
        Assert.Equal(left, File.ReadAllText(Path.Combine(local.FullName, "run.json")));
    }

    // Outside a git repository, and in a project where a run goes on, the demo is the same: what goes on there is not
    // read.
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task StatusOfADemoShowsAMadeUpRunThatGoesOn(bool inAProject)
    {
        if (inAProject)
        {
            GitRepository();
            GoingRun(Working);
        }

        var (exit, output, error) = await RunAsync("status", "--demo");

        Assert.Equal(3, exit);
        Assert.Equal("", error);
        Assert.Equal(
            "A run is going on: #102 Export the orders as a CSV file · claude attach 1a2b3c4d" + Environment.NewLine + "Demo: the run is made up." + Environment.NewLine,
            output);

        var (code, json, _) = await RunAsync("status", "--demo", "--json");
        Assert.Equal(3, code);
        Assert.StartsWith("{", json, StringComparison.Ordinal);
        Assert.DoesNotContain("Demo", json, StringComparison.Ordinal);
        Assert.Contains("\"url\": \"https://github.com/example/demo-project/issues/102\"", json, StringComparison.Ordinal);
        Assert.Equal(inAProject, Directory.Exists(Path.Combine(folder.FullName, ".toobusy")));
    }
}
