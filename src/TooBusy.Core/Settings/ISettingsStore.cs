namespace TooBusy.Core.Settings;

// The settings file of a project, as the setup needs it.
public interface ISettingsStore
{
    // How messages name the file.
    string DisplayPath { get; }

    // Null when the project has no settings file.
    SettingsValidation? Load();

    // The text of the file as it is and as saving the settings would leave it.
    SettingsPreview Preview(ProjectSettings settings);

    void Save(ProjectSettings settings);
}

// Before is empty when there is no file yet.
public sealed record SettingsPreview(string Before, string After);
