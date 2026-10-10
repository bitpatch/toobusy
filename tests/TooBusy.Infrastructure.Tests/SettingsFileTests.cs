using TooBusy.Core.Settings;
using TooBusy.Infrastructure.Settings;

namespace TooBusy.Infrastructure.Tests;

public sealed class SettingsFileTests : IDisposable
{
    static readonly ProjectSettings Settings = new(
        new TrackerSettings("github", null),
        new QueueSettings(new LabelSettings(["manual"], [], "manual", "interrupted")),
        new AssistantSettings("claude-code"));

    readonly DirectoryInfo folder = Directory.CreateTempSubdirectory("toobusy-settings-");

    public void Dispose() => folder.Delete(recursive: true);

    [Fact]
    public void TheFileLivesInTheToobusyFolderOfTheProject()
    {
        var file = new SettingsFile(folder.FullName);

        Assert.Equal(Path.Combine(folder.FullName, ".toobusy", "settings.toml"), file.Path);
        Assert.False(file.Exists);
        Assert.Null(file.Load());
    }

    [Fact]
    public void SavingCreatesTheFolderAndTheFile()
    {
        var file = new SettingsFile(folder.FullName);

        file.Save(Settings);

        Assert.True(file.Exists);
        Assert.Equal(Settings.Tracker, file.Load()!.Settings!.Tracker);
    }

    [Fact]
    public void SavingAgainKeepsWhatTheOwnerWrote()
    {
        var file = new SettingsFile(folder.FullName);
        file.Save(Settings);
        File.WriteAllText(file.Path, File.ReadAllText(file.Path).Replace("[\"manual\"]", "[\"manual\"]  # by hand", StringComparison.Ordinal));

        file.Save(Settings with { Tracker = Settings.Tracker with { Board = "https://github.com/orgs/bitpatch/projects/9" } });

        var text = File.ReadAllText(file.Path);
        Assert.Contains("blocking = [\"manual\"]  # by hand", text, StringComparison.Ordinal);
        Assert.Contains("board = \"https://github.com/orgs/bitpatch/projects/9\"", text, StringComparison.Ordinal);
    }

    [Fact]
    public void AFileThatDoesNotValidateGivesItsErrors()
    {
        var file = new SettingsFile(folder.FullName);
        file.Save(Settings);
        File.AppendAllText(file.Path, "model = \"opus\"\n");

        var result = file.Load()!;

        Assert.Null(result.Settings);
        Assert.Equal("assistant.model", Assert.Single(result.Errors).Key);
    }
}
