using TooBusy.Core.Run;
using TooBusy.Infrastructure.Run;

namespace TooBusy.Infrastructure.Tests;

public sealed class RunStateFileTests : IDisposable
{
    readonly DirectoryInfo root = Directory.CreateTempSubdirectory("toobusy-local-");

    public void Dispose() => root.Delete(recursive: true);

    [Fact]
    public void TheLocalFolderKeepsItselfOutOfGit()
    {
        var folder = LocalFolder.Ensure(root.FullName);

        Assert.Equal(Path.Combine(root.FullName, ".toobusy", "local"), folder);
        Assert.Equal("*\n", File.ReadAllText(Path.Combine(folder, ".gitignore")));
    }

    [Fact]
    public void AnIgnoreFileThatIsThereIsLeftAsItIs()
    {
        var folder = LocalFolder.Ensure(root.FullName);
        File.WriteAllText(Path.Combine(folder, ".gitignore"), "*\n!keep\n");

        LocalFolder.Ensure(root.FullName);

        Assert.Equal("*\n!keep\n", File.ReadAllText(Path.Combine(folder, ".gitignore")));
    }

    [Fact]
    public void ATaskThatIsPausedIsRememberedUntilItIsCleared()
    {
        var folder = LocalFolder.Ensure(root.FullName);
        var state = new RunStateFile(folder);
        Assert.Null(state.LoadPaused());

        state.SavePaused(new PausedTask(12, "15555bc6-0c7f-4efb-a0a0-211c72424c97"));

        Assert.Equal(new PausedTask(12, "15555bc6-0c7f-4efb-a0a0-211c72424c97"), new RunStateFile(folder).LoadPaused());
        Assert.Equal("""{"task":12,"conversation":"15555bc6-0c7f-4efb-a0a0-211c72424c97"}""", File.ReadAllText(Path.Combine(folder, "paused.json")));

        state.ClearPaused();
        state.ClearPaused();

        Assert.Null(state.LoadPaused());
        Assert.False(File.Exists(Path.Combine(folder, "paused.json")));
    }

    [Theory]
    [InlineData("")]
    [InlineData("not json")]
    [InlineData("[12]")]
    [InlineData("""{"task":"12","conversation":"c"}""")]
    [InlineData("""{"task":12}""")]
    [InlineData("""{"task":12,"conversation":""}""")]
    public void AFileThatSaysNothingUsefulIsNoPause(string text)
    {
        var folder = LocalFolder.Ensure(root.FullName);
        File.WriteAllText(Path.Combine(folder, "paused.json"), text);

        Assert.Null(new RunStateFile(folder).LoadPaused());
    }
}
