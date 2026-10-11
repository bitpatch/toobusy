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
    public void TheRecordOfATaskIsRememberedUntilItIsCleared()
    {
        var folder = LocalFolder.Ensure(root.FullName);
        var state = new RunStateFile(folder);
        var since = new DateTimeOffset(2030, 1, 1, 9, 0, 0, TimeSpan.Zero);
        Assert.Null(state.Load());

        state.Save(new TaskRecord(12, new SessionTrace("15555bc6", null, since)));

        Assert.Equal(new TaskRecord(12, new SessionTrace("15555bc6", null, since)), new RunStateFile(folder).Load());
        Assert.Equal("""{"task":12,"session":"15555bc6","since":"2030-01-01T09:00:00.0000000Z","state":"working"}""", File.ReadAllText(Path.Combine(folder, "session.json")));

        // A record that is written again takes the place of the one before, whole.
        var paused = new TaskRecord(12, new SessionTrace("15555bc6", "15555bc6-0c7f-4efb-a0a0-211c72424c97", since), RecordState.Paused);
        state.Save(paused);

        Assert.Equal(paused, new RunStateFile(folder).Load());
        Assert.False(File.Exists(Path.Combine(folder, "session.json.tmp")));

        state.Save(paused with { State = RecordState.WrappedUp });
        Assert.Equal(RecordState.WrappedUp, state.Load()!.State);

        state.Clear();
        state.Clear();

        Assert.Null(state.Load());
        Assert.False(File.Exists(Path.Combine(folder, "session.json")));
    }

    [Theory]
    [InlineData("")]
    [InlineData("not json")]
    [InlineData("[12]")]
    [InlineData("""{"task":"12","conversation":"c"}""")]
    [InlineData("""{"conversation":"c"}""")]
    public void AFileThatSaysNothingUsefulIsNoRecord(string text)
    {
        var folder = LocalFolder.Ensure(root.FullName);
        File.WriteAllText(Path.Combine(folder, "session.json"), text);

        Assert.Null(new RunStateFile(folder).Load());
    }

    [Fact]
    public void ARecordThatNamesOnlyItsTaskIsOneOfASessionThatIsNotKnown()
    {
        var folder = LocalFolder.Ensure(root.FullName);
        File.WriteAllText(Path.Combine(folder, "session.json"), """{"task":12,"session":"","state":"later"}""");

        Assert.Equal(new TaskRecord(12, new SessionTrace(null, null, DateTimeOffset.MinValue)), new RunStateFile(folder).Load());
    }
}
