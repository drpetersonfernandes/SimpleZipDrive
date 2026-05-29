using SimpleZipDrive_WinFsp.Models;

namespace SimpleZipDrive_WinFsp.Services;

public interface ISettingsService
{
    AppSettings Settings { get; }

    void SaveSettings();

    void ReloadSettings();

    void UpdateRamLimit(int maxMemoryPerFileMb);
}
