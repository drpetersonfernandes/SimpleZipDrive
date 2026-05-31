namespace SimpleZipDrive.Core.Services;

/// <summary>
/// Service for managing application settings.
/// </summary>
public interface ISettingsService
{
    /// <summary>
    /// Gets the current application settings.
    /// </summary>
    AppSettings Settings { get; }

    /// <summary>
    /// Saves the current settings.
    /// </summary>
    void SaveSettings();

    /// <summary>
    /// Reloads settings from disk.
    /// </summary>
    void ReloadSettings();

    /// <summary>
    /// Updates the RAM limit per file.
    /// </summary>
    /// <param name="maxMemoryPerFileMb">The maximum memory per file in MB.</param>
    void UpdateRamLimit(int maxMemoryPerFileMb);
}
