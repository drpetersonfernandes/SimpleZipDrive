using SimpleZipDrive.Models;

namespace SimpleZipDrive.Services;

/// <summary>
/// Implementation of the settings service.
/// </summary>
public class SettingsService : ISettingsService
{
    /// <inheritdoc />
    public AppSettings Settings { get; private set; }

    /// <summary>
    /// Initializes a new instance of the <see cref="SettingsService"/> class.
    /// </summary>
    public SettingsService()
    {
        Settings = AppSettings.Load();
    }

    /// <inheritdoc />
    public void SaveSettings()
    {
        Settings.Save();
    }

    /// <inheritdoc />
    public void ReloadSettings()
    {
        Settings = AppSettings.Load();
    }

    /// <inheritdoc />
    public void UpdateRamLimit(int maxMemoryPerFileMb)
    {
        if (maxMemoryPerFileMb > 0)
        {
            Settings.MaxMemoryPerFileMb = maxMemoryPerFileMb;
            SaveSettings();
        }
    }
}
