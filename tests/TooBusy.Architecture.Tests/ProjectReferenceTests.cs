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

    // The packages Core may have, by name: none so far. A package is let in here as a choice, and never one that
    // reaches outside the process.
    static readonly string[] CorePackages = [];

    public static TheoryData<string> Projects => [.. SourceProjects().Select(Path.GetFileNameWithoutExtension).OfType<string>()];

    public static TheoryData<string> TestProjects => [.. TestProjectFiles().Select(Path.GetFileNameWithoutExtension).OfType<string>()];

    [Theory]
    [MemberData(nameof(Projects))]
    public void ProjectReferencesOnlyWhatItsLayerAllows(string project)
    {
        Assert.True(Allowed.TryGetValue(project, out var allowed), $"{project} has no rule in {nameof(ProjectReferenceTests)}");

        Assert.Empty(References(SourceProjects().Single(file => Path.GetFileNameWithoutExtension(file) == project)).Except(allowed!));
    }

    [Fact]
    public void EveryRuleHasItsProject() =>
        Assert.Empty(Allowed.Keys.Except(SourceProjects().Select(Path.GetFileNameWithoutExtension)));

    // What Core is built with comes from its project and from the files of the build above it, which every project
    // of src imports.
    [Fact]
    public void CoreHasNoPackageButThoseLetIn()
    {
        var core = SourceProjects().Single(file => Path.GetFileNameWithoutExtension(file) == "TooBusy.Core");
        var packages = Above(core).Prepend(core).SelectMany(file => Items(file, "PackageReference")).Select(package => package.Attribute("Include")!.Value);

        Assert.Empty(packages.Except(CorePackages));
    }

    // A project of src is built of the files of its own folder, and takes in an assembly only by a reference of a
    // project or of a package, which the rules see: a file of another project compiled into it, or its assembly
    // referenced as a file, would go round them.
    [Theory]
    [MemberData(nameof(Projects))]
    public void ProjectIsBuiltOfItsOwnFolder(string project)
    {
        var file = SourceProjects().Single(path => Path.GetFileNameWithoutExtension(path) == project);
        var folder = Path.GetDirectoryName(file)! + Path.DirectorySeparatorChar;

        var compiled = Items(file, "Compile").SelectMany(item => item.Attribute("Include")!.Value.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));

        Assert.Empty(Items(file, "Reference").Select(reference => reference.Attribute("Include")!.Value));
        Assert.DoesNotContain(compiled, path => path.Contains("$(", StringComparison.Ordinal) || !Path.GetFullPath(path.Replace('\\', '/'), folder).StartsWith(folder, StringComparison.Ordinal));
    }

    // A test project tests the project it is named after, and references no other: Core is tested with hand-written
    // fakes of its ports, not with the adapters behind them. The tests of the rules read the files and reference none.
    [Theory]
    [MemberData(nameof(TestProjects))]
    public void TestProjectReferencesOnlyTheProjectItTests(string project)
    {
        var tested = project[..^".Tests".Length];
        string[] allowed = SourceProjects().Any(file => Path.GetFileNameWithoutExtension(file) == tested) ? [tested] : [];

        Assert.Equal(allowed, References(TestProjectFiles().Single(file => Path.GetFileNameWithoutExtension(file) == project)));
    }

    static IEnumerable<string> References(string project) =>
        Items(project, "ProjectReference").Select(reference => Path.GetFileNameWithoutExtension(reference.Attribute("Include")!.Value.Replace('\\', '/')));

    static IEnumerable<XElement> Items(string file, string kind) =>
        XDocument.Load(file).Descendants(kind).Where(item => item.Attribute("Include") is not null);

    // The files of the build that MSBuild imports for a project from the folders above it, up to the root.
    static IEnumerable<string> Above(string project)
    {
        var root = RepositoryRoot();
        for (var folder = Directory.GetParent(project); folder is not null && folder.FullName.Length >= root.Length; folder = folder.Parent)
        {
            foreach (var name in (string[])["Directory.Build.props", "Directory.Build.targets"])
            {
                if (Path.Combine(folder.FullName, name) is var file && File.Exists(file))
                    yield return file;
            }
        }
    }

    static string[] SourceProjects() =>
        Directory.GetFiles(Path.Combine(RepositoryRoot(), "src"), "*.csproj", SearchOption.AllDirectories);

    static string[] TestProjectFiles() =>
        Directory.GetFiles(Path.Combine(RepositoryRoot(), "tests"), "*.Tests.csproj", SearchOption.AllDirectories);

    static string RepositoryRoot()
    {
        var folder = new DirectoryInfo(AppContext.BaseDirectory);
        while (folder is not null && !File.Exists(Path.Combine(folder.FullName, "toobusy.slnx")))
            folder = folder.Parent;
        return folder?.FullName ?? throw new InvalidOperationException("toobusy.slnx was not found above the test assembly");
    }
}
