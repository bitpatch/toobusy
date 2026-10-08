using System.Xml.Linq;

namespace TooBusy.Architecture.Tests;

// The direction of dependencies described in docs/ARCHITECTURE.md, read from the project files.
public class ProjectReferenceTests
{
    static readonly Dictionary<string, string[]> Allowed = new()
    {
        ["TooBusy.Core"] = [],
        ["TooBusy.Infrastructure"] = ["TooBusy.Core"],
        ["TooBusy.Trackers.GitHub"] = ["TooBusy.Core"],
        ["TooBusy.Assistants.ClaudeCode"] = ["TooBusy.Core"],
        ["TooBusy.Cli"] =
            ["TooBusy.Core", "TooBusy.Infrastructure", "TooBusy.Trackers.GitHub", "TooBusy.Assistants.ClaudeCode"],
    };

    public static TheoryData<string> Projects => [.. SourceProjects().Select(Path.GetFileNameWithoutExtension).OfType<string>()];

    [Theory]
    [MemberData(nameof(Projects))]
    public void ProjectReferencesOnlyWhatItsLayerAllows(string project)
    {
        Assert.True(Allowed.TryGetValue(project, out var allowed), $"{project} has no rule in {nameof(ProjectReferenceTests)}");

        var path = SourceProjects().Single(file => Path.GetFileNameWithoutExtension(file) == project);
        var references = XDocument.Load(path).Descendants("ProjectReference")
            .Select(reference => Path.GetFileNameWithoutExtension(reference.Attribute("Include")!.Value.Replace('\\', '/')));

        Assert.Empty(references.Except(allowed!));
    }

    [Fact]
    public void EveryRuleHasItsProject() =>
        Assert.Empty(Allowed.Keys.Except(SourceProjects().Select(Path.GetFileNameWithoutExtension)));

    static string[] SourceProjects() =>
        Directory.GetFiles(Path.Combine(RepositoryRoot(), "src"), "*.csproj", SearchOption.AllDirectories);

    static string RepositoryRoot()
    {
        var folder = new DirectoryInfo(AppContext.BaseDirectory);
        while (folder is not null && !File.Exists(Path.Combine(folder.FullName, "toobusy.slnx")))
            folder = folder.Parent;
        return folder?.FullName ?? throw new InvalidOperationException("toobusy.slnx was not found above the test assembly");
    }
}
