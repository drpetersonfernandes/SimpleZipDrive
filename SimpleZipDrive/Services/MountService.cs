using System.Windows;
using DokanNet;
using DokanNet.Logging;
using SimpleZipDrive.Views;

namespace SimpleZipDrive.Services;

/// <summary>
/// Implementation of the mount service.
/// </summary>
public class MountService : IDisposable, IMountService
{
    private readonly ILoggingService _loggingService;
    private readonly ISettingsService _settingsService;
    private CancellationTokenSource? _mountCancellation;
    private ZipFs? _currentZipFs;

    /// <inheritdoc />
    public event EventHandler<MountStatusChangedEventArgs>? MountStatusChanged;

    /// <inheritdoc />
    public bool IsMounted { get; private set; }

    /// <inheritdoc />
    public string? CurrentMountPoint { get; private set; }

    /// <inheritdoc />
    public string? CurrentArchivePath { get; private set; }

    /// <summary>
    /// Initializes a new instance of the <see cref="MountService"/> class.
    /// </summary>
    /// <param name="loggingService">The logging service.</param>
    /// <param name="settingsService">The settings service.</param>
    public MountService(ILoggingService loggingService, ISettingsService settingsService)
    {
        _loggingService = loggingService ?? throw new ArgumentNullException(nameof(loggingService));
        _settingsService = settingsService ?? throw new ArgumentNullException(nameof(settingsService));
    }

    /// <inheritdoc />
    public Task MountAsync(string archivePath, string? mountPoint = null)
    {
        if (IsMounted)
        {
            throw new InvalidOperationException("A drive is already mounted. Please unmount it first.");
        }

        if (!File.Exists(archivePath))
        {
            _loggingService.Log($"Error: Archive file not found at '{archivePath}'.");
            return Task.CompletedTask;
        }

        var archiveType = GetArchiveType(archivePath);
        var supportedExtensions = new[] { ".zip", ".7z", ".rar" };

        if (!supportedExtensions.Any(ext =>
                Path.GetExtension(archivePath).Equals(ext, StringComparison.OrdinalIgnoreCase)))
        {
            _loggingService.Log($"\n{AppTheme.Section("INVALID FILE TYPE")}");
            _loggingService.Log($"Error: The file '{Path.GetFileName(archivePath)}' is not a supported archive.");
            return Task.CompletedTask;
        }

        if (!CheckForAdministratorRole.IsAdministrator())
        {
            _loggingService.Log("");
            _loggingService.Log("Warning: Running without Administrator privileges.");
            _loggingService.Log("Mounting to drive letters or certain paths may require elevated permissions.");
            _loggingService.Log("");
        }

        ILogger logger = new ConsoleLogger(AppTheme.DokanLogPrefix);
        CurrentArchivePath = archivePath;

        if (string.IsNullOrEmpty(mountPoint))
        {
            // Auto-select drive letter
            return MountWithAutoDriveLetterAsync(archivePath, archiveType, logger);
        }
        else
        {
            // Use specified mount point
            return MountWithSpecifiedPointAsync(archivePath, mountPoint, archiveType, logger);
        }
    }

    /// <inheritdoc />
    public async Task UnmountAsync()
    {
        if (!IsMounted)
        {
            return;
        }

        try
        {
            _loggingService.Log("Unmounting drive...");
            _mountCancellation?.Cancel();
            IsMounted = false;

            // Allow time for ongoing Dokan operations to acknowledge cancellation
            // before disposing the ZipFs instance to avoid race conditions
            await Task.Delay(500);

            _currentZipFs?.Dispose();
            _currentZipFs = null;

            CurrentMountPoint = null;
            CurrentArchivePath = null;

            _loggingService.Log("Drive unmounted successfully.");
            OnMountStatusChanged();
        }
        catch (OperationCanceledException)
        {
            // Expected during unmount - no need to report
            throw;
        }
        catch (Exception ex)
        {
            _loggingService.LogError($"Error unmounting drive: {ex.Message}");
            ErrorLoggerStatic.LogErrorSync(ex, "MountService.UnmountAsync: Error unmounting drive");
            throw;
        }
    }

    /// <inheritdoc />
    public string GetArchiveType(string filePath)
    {
        var extension = Path.GetExtension(filePath).ToLowerInvariant();
        return extension.TrimStart('.');
    }

    /// <inheritdoc />
    public void Dispose()
    {
        _mountCancellation?.Dispose();
        _currentZipFs?.Dispose();
        GC.SuppressFinalize(this);
    }

    private async Task MountWithAutoDriveLetterAsync(string archivePath, string archiveType, ILogger logger)
    {
        char[] preferredDriveLetters = ['M', 'N', 'O', 'P', 'Q'];
        var existingDrives = DriveInfo.GetDrives().Select(static d => d.Name).ToList();

        foreach (var letter in preferredDriveLetters)
        {
            var currentMountPoint = letter + @":\";

            if (existingDrives.Any(d => string.Equals(d, currentMountPoint, StringComparison.OrdinalIgnoreCase)))
            {
                _loggingService.Log($"Skipping '{currentMountPoint}' (already in use).");
                continue;
            }

            using var dokan = new Dokan(logger);
            _loggingService.Log($"Dokan Library Version: {dokan.Version}");
            _loggingService.Log($"Dokan Driver Version: {dokan.DriverVersion}");
            _loggingService.Log("");
            _loggingService.Log($"Attempting to mount on '{currentMountPoint}'...");

            if (await AttemptMountLifecycleAsync(archivePath, currentMountPoint, dokan, archiveType))
            {
                // Event is already fired inside AttemptMountLifecycleAsync when mount succeeds
                return;
            }
        }

        _loggingService.Log("Error: Failed to auto-mount on any preferred drive letters.");
    }

    private async Task MountWithSpecifiedPointAsync(string archivePath, string mountPoint, string archiveType, ILogger logger)
    {
        if (mountPoint.Length == 1 && char.IsLetter(mountPoint[0]))
        {
            mountPoint = mountPoint.ToUpperInvariant() + @":\";
        }

        using var dokan = new Dokan(logger);
        _loggingService.Log($"Dokan Library Version: {dokan.Version}");
        _loggingService.Log($"Dokan Driver Version: {dokan.DriverVersion}");
        _loggingService.Log("");

        if (!await AttemptMountLifecycleAsync(archivePath, mountPoint, dokan, archiveType))
        {
            _loggingService.Log($"Error: Failed to mount on '{mountPoint}'.");
        }
        // Event is already fired inside AttemptMountLifecycleAsync when mount succeeds
    }

    private async Task<bool> AttemptMountLifecycleAsync(string archivePath, string mountPoint, Dokan dokan, string archiveType)
    {
        _mountCancellation = new CancellationTokenSource();

        try
        {
            var fileInfo = new FileInfo(archivePath);
            _loggingService.Log($"Processing {archiveType.ToUpperInvariant()} file: '{archivePath}', Size: {fileInfo.Length / 1024.0 / 1024.0:F2} MB");
            _loggingService.Log("");

            // Validate and clamp memory setting to prevent out-of-memory errors
            var effectiveMaxMemoryBytes = GetValidatedMaxMemoryBytes();

            await using Stream fileStream = new FileStream(archivePath, FileMode.Open, System.IO.FileAccess.Read, FileShare.Read);

            _currentZipFs = new ZipFs(
                fileStream,
                mountPoint,
                ErrorLoggerStatic.LogErrorSync,
                () => PromptForPassword(archivePath, archiveType), // Password callback using WPF dialog
                archiveType,
                effectiveMaxMemoryBytes);

            var builder = new DokanInstanceBuilder(dokan)
                .ConfigureOptions(options =>
                {
                    options.Options = DokanOptions.RemovableDrive;
                    options.MountPoint = mountPoint;
                });

            using (builder.Build(_currentZipFs))
            {
                _loggingService.Log($"Successfully mounted on '{mountPoint}'.");
                _loggingService.Log("");
                _loggingService.Log("Use the Unmount button or close the window to unmount.");
                _loggingService.Log("");

                // Fire the status changed event immediately after successful mount
                IsMounted = true;
                CurrentMountPoint = mountPoint;
                CurrentArchivePath = archivePath;
                OnMountStatusChanged();

                try
                {
                    await Task.Delay(Timeout.Infinite, _mountCancellation.Token);
                }
                catch (OperationCanceledException)
                {
                }

                _loggingService.Log($"Unmounting '{mountPoint}'...");
            }

            return true;
        }
        catch (DokanException ex)
        {
            // Dokan errors are typically user/environment issues - log but don't report as bug
            _loggingService.LogError($"Dokan error: {ex.Message}");
            return false;
        }
        catch (Exception ex) when (ex.Message.Contains("drive", StringComparison.OrdinalIgnoreCase) ||
                                   ex.Message.Contains("mount", StringComparison.OrdinalIgnoreCase))
        {
            // Drive/mount related errors are typically user/environment issues
            _loggingService.LogError($"Mount error: {ex.Message}");
            return false;
        }
        catch (Exception ex)
        {
            _loggingService.LogError($"Mount error: {ex.Message}");
            // Report actual bugs to the API
            ErrorLoggerStatic.LogErrorSync(ex, $"MountService.AttemptMountLifecycleAsync: Error mounting archive '{archivePath}' to '{mountPoint}'");
            return false;
        }
        finally
        {
            _mountCancellation?.Dispose();
            _mountCancellation = null;
        }
    }

    private void OnMountStatusChanged()
    {
        MountStatusChanged?.Invoke(this, new MountStatusChangedEventArgs
        {
            IsMounted = IsMounted,
            MountPoint = CurrentMountPoint,
            ArchivePath = CurrentArchivePath
        });
    }

    /// <summary>
    /// Validates the configured max memory per file setting and clamps it to 90% of available system memory
    /// if the configured value exceeds available memory. This prevents out-of-memory errors.
    /// </summary>
    /// <returns>The validated max memory bytes to use.</returns>
    private long GetValidatedMaxMemoryBytes()
    {
        var configuredBytes = _settingsService.Settings.MaxMemoryPerFileBytes;

        // Get available physical memory
        var availableMemory = GC.GetGCMemoryInfo().TotalAvailableMemoryBytes;

        // Calculate 90% of available memory as the maximum safe limit
        var safeMaxMemory = (long)(availableMemory * 0.9);

        if (configuredBytes > safeMaxMemory)
        {
            var configuredMb = configuredBytes / 1024.0 / 1024.0;
            var availableMb = availableMemory / 1024.0 / 1024.0;
            var safeMaxMb = safeMaxMemory / 1024.0 / 1024.0;

            _loggingService.Log($"WARNING: Configured RAM cache ({configuredMb:F0} MB) exceeds available system memory.");
            _loggingService.Log($"         Available memory: {availableMb:F0} MB");
            _loggingService.Log($"         Using safe limit (90%): {safeMaxMb:F0} MB");
            _loggingService.Log("");

            return safeMaxMemory;
        }

        return configuredBytes;
    }

    /// <summary>
    /// Prompts the user for a password using a WPF dialog.
    /// This method is thread-safe and will marshal to the UI thread if necessary.
    /// </summary>
    /// <param name="archivePath">The path to the archive file.</param>
    /// <param name="archiveType">The type of archive (zip, 7z, rar).</param>
    /// <returns>The password entered by the user, or null if cancelled.</returns>
    private static string? PromptForPassword(string archivePath, string archiveType)
    {
        // Use Dispatcher to show dialog on UI thread
        return Application.Current?.Dispatcher.Invoke(() =>
        {
            var passwordWindow = new PasswordWindow(archivePath, archiveType)
            {
                Owner = Application.Current.MainWindow,
                WindowStartupLocation = WindowStartupLocation.CenterOwner
            };

            var result = passwordWindow.ShowDialog();
            return result == true ? passwordWindow.Password : null;
        });
    }
}
