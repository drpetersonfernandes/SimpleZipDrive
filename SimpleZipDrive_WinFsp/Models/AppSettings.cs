using System.Text.Json;

namespace SimpleZipDrive_WinFsp.Models;

public class AppSettings
{
    private static readonly string SettingsDirectory = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "SimpleZipDrive");

    private static readonly string SettingsFilePath = Path.Combine(SettingsDirectory, "settings.dat");

    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    private long _maxMemoryPerFileMb = 512;

    public long MaxMemoryPerFileMb
    {
        get => _maxMemoryPerFileMb;
        set
        {
            var availableMemoryBytes = GC.GetGCMemoryInfo().TotalAvailableMemoryBytes;
            var availableMemoryMb = availableMemoryBytes / 1024 / 1024;
            var maxAllowedMb = (long)(availableMemoryMb * 0.9);

            if (value > maxAllowedMb)
            {
                _maxMemoryPerFileMb = maxAllowedMb;
            }
            else
            {
                _maxMemoryPerFileMb = value;
            }
        }
    }

    public long MaxMemoryPerFileBytes => MaxMemoryPerFileMb * 1024 * 1024;

    public static AppSettings Load()
    {
        AppSettings? settings = null;
        try
        {
            if (File.Exists(SettingsFilePath))
            {
                var json = File.ReadAllText(SettingsFilePath);
                settings = JsonSerializer.Deserialize<AppSettings>(json);
            }
        }
        catch (FileNotFoundException)
        {
        }
        catch (DirectoryNotFoundException)
        {
        }
        catch (UnauthorizedAccessException ex)
        {
            ErrorLoggerStatic.ReportSilentException(ex, "AppSettings.Load: Permission denied accessing settings file", true);
        }
        catch (JsonException ex)
        {
            ErrorLoggerStatic.ReportSilentException(ex, "AppSettings.Load: Failed to parse settings file (corrupted JSON)", true);
        }
        catch (Exception ex)
        {
            ErrorLoggerStatic.ReportSilentException(ex, "AppSettings.Load: Unexpected error loading settings", true);
        }

        settings ??= new AppSettings();

        var availableMemoryBytes = GC.GetGCMemoryInfo().TotalAvailableMemoryBytes;
        var availableMemoryMb = availableMemoryBytes / 1024 / 1024;
        var maxAllowedMb = (long)(availableMemoryMb * 0.9);

        if (settings.MaxMemoryPerFileMb > maxAllowedMb)
        {
            settings.MaxMemoryPerFileMb = maxAllowedMb;
        }

        return settings;
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(SettingsDirectory);
            var json = JsonSerializer.Serialize(this, JsonOptions);
            File.WriteAllText(SettingsFilePath, json);
        }
        catch (UnauthorizedAccessException ex)
        {
            ErrorLoggerStatic.ReportSilentException(ex, "AppSettings.Save: Permission denied saving settings", true);
        }
        catch (IOException ex)
        {
            ErrorLoggerStatic.ReportSilentException(ex, "AppSettings.Save: IO error saving settings", true);
        }
        catch (Exception ex)
        {
            ErrorLoggerStatic.ReportSilentException(ex, "AppSettings.Save: Unexpected error saving settings", true);
        }
    }
}
