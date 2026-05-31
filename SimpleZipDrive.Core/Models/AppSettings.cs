using System.Text.Json;

namespace SimpleZipDrive.Core.Models;

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
            // Get available system memory and calculate 90% limit
            var availableMemoryBytes = GC.GetGCMemoryInfo().TotalAvailableMemoryBytes;
            var availableMemoryMb = availableMemoryBytes / 1024 / 1024;
            var maxAllowedMb = (long)(availableMemoryMb * 0.9);

            // Clamp to valid range: minimum 1 MB, maximum 90% of available memory
            _maxMemoryPerFileMb = value < 1 ? 1 : value > maxAllowedMb ? maxAllowedMb : value;
        }
    }

    public long MaxMemoryPerFileBytes => MaxMemoryPerFileMb * 1024L * 1024L;

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
            // Expected when settings file doesn't exist - use defaults
        }
        catch (DirectoryNotFoundException)
        {
            // Expected when settings directory doesn't exist - use defaults
        }
        catch (UnauthorizedAccessException ex)
        {
            // Permission issue - report it but use defaults
            ErrorLoggerStatic.ReportSilentException(ex, "AppSettings.Load: Permission denied accessing settings file", true);
        }
        catch (JsonException ex)
        {
            // Corrupted settings file - report it, delete it, and use defaults
            ErrorLoggerStatic.ReportSilentException(ex, "AppSettings.Load: Failed to parse settings file (corrupted JSON)", true);
            try
            {
                File.Delete(SettingsFilePath);
            }
            catch (Exception deleteEx)
            {
                ErrorLoggerStatic.ReportSilentException(deleteEx, "AppSettings.Load: Failed to delete corrupted settings file", true);
            }
        }
        catch (Exception ex)
        {
            // Other errors - report silently
            ErrorLoggerStatic.ReportSilentException(ex, "AppSettings.Load: Unexpected error loading settings", true);
        }

        // Return validated settings or defaults
        settings ??= new AppSettings();

        // Re-apply validation in case the loaded value exceeds current system limits
        var availableMemoryBytes = GC.GetGCMemoryInfo().TotalAvailableMemoryBytes;
        var availableMemoryMb = availableMemoryBytes / 1024 / 1024;
        var maxAllowedMb = (long)(availableMemoryMb * 0.9);

        if (settings.MaxMemoryPerFileMb > maxAllowedMb)
        {
            settings.MaxMemoryPerFileMb = maxAllowedMb;
        }
        else if (settings.MaxMemoryPerFileMb < 1)
        {
            settings.MaxMemoryPerFileMb = 1;
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
