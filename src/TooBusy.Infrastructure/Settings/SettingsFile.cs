using TooBusy.Core.Settings;

namespace TooBusy.Infrastructure.Settings;

// `.toobusy/settings.toml` of the project at the given root.
public sealed class SettingsFile(string projectRoot)
{
    // How messages name the file, whatever the platform.
    public const string DisplayPath = ".toobusy/settings.toml";

    public string Path { get; } = System.IO.Path.Combine(projectRoot, ".toobusy", "settings.toml");

    public bool Exists => File.Exists(Path);

    public SettingsValidation Load() => SettingsToml.Read(File.ReadAllText(Path));

    // Writes a new file, or changes the values of the existing one and keeps the rest of it.
    public void Save(ProjectSettings settings)
    {
        var text = SettingsToml.Write(settings, Exists ? File.ReadAllText(Path) : "");
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(Path)!);
        File.WriteAllText(Path, text);
    }
}
