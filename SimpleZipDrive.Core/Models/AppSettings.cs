using System.Text.Json;

namespace SimpleZipDrive.Core.Models;

/// <summary>
/// Specifies how the archive virtual drive is mounted in the filesystem.
/// </summary>
public enum MountType
{
    /// <summary>Mount as a standard drive letter (e.g., <c>Z:\</c>).</summary>
    DriveLetter,

    /// <summary>Mount as a directory within an existing drive.</summary>
    Folder
}

/// <summary>
/// Persists user-configurable application settings to disk as JSON.
/// </summary>
public class AppSettings
{
    internal static readonly string SettingsDirectory = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "SimpleZipDrive");

    private static readonly string SettingsFilePath = Path.Combine(SettingsDirectory, "settings.dat");

    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    private long _maxMemoryPerFileMb = 512;

    /// <summary>
    /// Gets or sets the maximum memory (in megabytes) used to cache a single archive entry in RAM.
    /// Values are clamped between 1 MB and 90% of available system memory.
    /// </summary>
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

    /// <summary>Gets the maximum memory per file converted to bytes.</summary>
    public long MaxMemoryPerFileBytes => MaxMemoryPerFileMb * 1024L * 1024L;

    /// <summary>Gets or sets the default mount type used when opening archives.</summary>
    public MountType DefaultMountType { get; set; } = MountType.DriveLetter;

    /// <summary>Gets or sets a value indicating whether the mounted drive should be opened in Explorer automatically.</summary>
    public bool AutoOpenMountedDrive { get; set; }

    /// <summary>
    /// Loads settings from the persistent settings file, applying system-memory validation.
    /// Returns default settings if the file is missing, corrupted, or inaccessible.
    /// </summary>
    /// <returns>The loaded or default <see cref="AppSettings"/> instance.</returns>
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

    /// <summary>
    /// Persists the current settings to disk. Errors are reported silently without throwing.
    /// </summary>
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
