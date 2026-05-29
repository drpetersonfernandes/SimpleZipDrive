using SimpleZipDrive_WinFsp.Models;

namespace SimpleZipDrive_WinFsp.Services;

public class SettingsService : ISettingsService
{
    public AppSettings Settings { get; private set; } = AppSettings.Load();

    public void SaveSettings()
    {
        Settings.Save();
    }

    public void ReloadSettings()
    {
        Settings = AppSettings.Load();
    }

    public void UpdateRamLimit(int maxMemoryPerFileMb)
    {
        if (maxMemoryPerFileMb > 0)
        {
            Settings.MaxMemoryPerFileMb = maxMemoryPerFileMb;
            SaveSettings();
        }
    }
}
