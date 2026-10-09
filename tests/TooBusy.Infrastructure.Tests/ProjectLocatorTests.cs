using TooBusy.Infrastructure.Settings;

namespace TooBusy.Infrastructure.Tests;

public sealed class ProjectLocatorTests : IDisposable
{
    readonly DirectoryInfo folder = Directory.CreateTempSubdirectory("toobusy-locator-");

    public void Dispose() => folder.Delete(recursive: true);

    [Fact]
    public void TheRootIsTheFolderThatHoldsGit()
    {
        folder.CreateSubdirectory(".git");
        var inside = folder.CreateSubdirectory("src").CreateSubdirectory("deep");

        Assert.Equal(folder.FullName, ProjectLocator.FindRoot(folder.FullName));
        Assert.Equal(folder.FullName, ProjectLocator.FindRoot(inside.FullName));
    }

    [Fact]
    public void AWorktreeHasAGitFileInsteadOfAFolder()
    {
        File.WriteAllText(Path.Combine(folder.FullName, ".git"), "gitdir: /somewhere/else");
        var inside = folder.CreateSubdirectory("src");

        Assert.Equal(folder.FullName, ProjectLocator.FindRoot(inside.FullName));
    }

    [Fact]
    public void OutsideAWorkingCopyThereIsNoRoot()
    {
        Assert.Null(ProjectLocator.FindRoot(folder.CreateSubdirectory("src").FullName));
    }
}
