using TooBusy.Core.Settings;

namespace TooBusy.Infrastructure.Settings;

// `.toobusy/settings.toml` of the project at the given root.
public sealed class SettingsFile(string projectRoot) : ISettingsStore
{
    // How messages name the file, whatever the platform.
    public const string DisplayPath = ".toobusy/settings.toml";

    public string Path { get; } = System.IO.Path.Combine(projectRoot, ".toobusy", "settings.toml");

    public bool Exists => File.Exists(Path);

    string ISettingsStore.DisplayPath => DisplayPath;

    public SettingsValidation? Load() => Exists ? SettingsToml.Read(File.ReadAllText(Path)) : null;

    // A file that cannot be edited in place is replaced by a new one.
    public SettingsPreview Preview(ProjectSettings settings)
    {
        var before = Exists ? File.ReadAllText(Path) : "";
        try
        {
            return new SettingsPreview(before, SettingsToml.Write(settings, before));
        }
        catch (FormatException)
        {
            return new SettingsPreview(before, SettingsToml.Write(settings));
        }
    }

    // Writes a new file, or changes the values of the existing one and keeps the rest of it.
    public void Save(ProjectSettings settings)
    {
        var text = Preview(settings).After;
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(Path)!);
        File.WriteAllText(Path, text);
    }
}
