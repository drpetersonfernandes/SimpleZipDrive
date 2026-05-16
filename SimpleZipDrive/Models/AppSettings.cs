using System.Text.Json;

namespace SimpleZipDrive.Models;

public class AppSettings
{
    private static readonly string SettingsDirectory = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "SimpleZipDrive");

    private static readonly string SettingsFilePath = Path.Combine(SettingsDirectory, "settings.dat");

    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    public long MaxMemoryPerFileMb { get; set; } = 512;

    public long MaxMemoryPerFileBytes => MaxMemoryPerFileMb * 1024 * 1024;

    public static AppSettings Load()
    {
        try
        {
            if (File.Exists(SettingsFilePath))
            {
                var json = File.ReadAllText(SettingsFilePath);
                var settings = JsonSerializer.Deserialize<AppSettings>(json);
                if (settings != null)
                    return settings;
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
            // Corrupted settings file - report it and use defaults
            ErrorLoggerStatic.ReportSilentException(ex, "AppSettings.Load: Failed to parse settings file (corrupted JSON)", true);
        }
        catch (Exception ex)
        {
            // Other errors - report silently
            ErrorLoggerStatic.ReportSilentException(ex, "AppSettings.Load: Unexpected error loading settings", true);
        }

        return new AppSettings();
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
