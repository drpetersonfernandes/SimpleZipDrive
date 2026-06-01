using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Windows;
using DokanNet;
using DokanNet.Logging;

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
            throw new FileNotFoundException($"Archive file not found at '{archivePath}'.", archivePath);
        }

        var archiveType = GetArchiveType(archivePath);
        var supportedExtensions = new[] { ".zip", ".7z", ".rar" };

        if (!supportedExtensions.Any(ext =>
                Path.GetExtension(archivePath).Equals(ext, StringComparison.OrdinalIgnoreCase)))
        {
            _loggingService.Log($"\n{AppTheme.Section("INVALID FILE TYPE")}");
            _loggingService.Log($"Error: The file '{Path.GetFileName(archivePath)}' is not a supported archive.");
            throw new ArgumentException(
                $"The file '{Path.GetFileName(archivePath)}' is not a supported archive format (expected .zip, .7z, or .rar).",
                nameof(archivePath));
        }

        if (!CheckForAdministratorRole.IsAdministrator())
        {
            _loggingService.Log("");
            _loggingService.Log("Warning: Running without Administrator privileges.");
            _loggingService.Log("Mounting to drive letters or certain paths may require elevated permissions.");
            _loggingService.Log("");
        }

        if (!IsDokanInstalled())
        {
            _loggingService.LogError("Dokan driver not found. Unable to mount archive.");
            ShowDokanNotInstalledDialog();
            return Task.CompletedTask;
        }

        ILogger logger = new DokanPrefixedLogger(AppTheme.DokanLogPrefix);
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
            var cts = _mountCancellation;
            try
            {
                cts?.Cancel();
            }
            catch (ObjectDisposedException)
            {
            }

            IsMounted = false;

            try
            {
                if (cts != null) await Task.Delay(500, cts.Token);
            }
            catch (OperationCanceledException)
            {
                // Expected if cancellation completes before delay
            }

            _currentZipFs?.Dispose();
            _currentZipFs = null;

            CurrentMountPoint = null;
            CurrentArchivePath = null;

            _loggingService.Log("Drive unmounted successfully.");
            OnMountStatusChanged();
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
        try
        {
            _mountCancellation?.Cancel();
        }
        catch (ObjectDisposedException)
        {
        }

        _mountCancellation?.Dispose();
        _currentZipFs?.Dispose();
        _currentZipFs = null;
        GC.SuppressFinalize(this);
    }

    [DllImport("dokan2.dll", ExactSpelling = true)]
    private static extern uint DokanVersion();

    private static bool IsDokanInstalled()
    {
        try
        {
            return DokanVersion() > 0;
        }
        catch (DllNotFoundException)
        {
            return false;
        }
        catch (EntryPointNotFoundException)
        {
            return false;
        }
    }

    private static void ShowDokanNotInstalledDialog()
    {
        const string message = "The Dokan file system driver (dokan2.dll) is required to mount archives as virtual drives. " +
                               "It does not appear to be installed on this system.\n\n" +
                               "Would you like to open the Dokan download page?";

        var result = MessageBox.Show(message, "Dokan Driver Not Found",
            MessageBoxButton.YesNo, MessageBoxImage.Warning);

        if (result == MessageBoxResult.Yes)
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = "https://github.com/dokan-dev/dokany/releases",
                UseShellExecute = true
            });
        }
    }

    private static void ShowDokanDriverErrorDialog(string errorMessage)
    {
        const string message = "The Dokan file system driver encountered an error:\n\n" +
                               "\"{0}\"\n\n" +
                               "Your Dokan driver may be outdated, corrupted, or not configured correctly. " +
                               "Please try reinstalling or updating the Dokan driver.\n\n" +
                               "Would you like to open the Dokan download page?";

        var formattedMessage = string.Format(CultureInfo.CurrentCulture, message, errorMessage);

        var result = MessageBox.Show(formattedMessage, "Dokan Driver Error",
            MessageBoxButton.YesNo, MessageBoxImage.Warning);

        if (result == MessageBoxResult.Yes)
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = "https://github.com/dokan-dev/dokany/releases",
                UseShellExecute = true
            });
        }
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

            Dokan dokan;
            try
            {
                dokan = new Dokan(logger);
            }
            catch (DllNotFoundException ex)
            {
                ErrorLoggerStatic.ReportSilentException(ex, "Dokan driver not found during auto-mount");
                _loggingService.LogError("Dokan driver not found. Unable to mount archive.");
                ShowDokanNotInstalledDialog();
                return;
            }

            using (dokan)
            {
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
        }

        _loggingService.Log("Error: Failed to auto-mount on any preferred drive letters.");
    }

    private async Task MountWithSpecifiedPointAsync(string archivePath, string mountPoint, string archiveType, ILogger logger)
    {
        if (mountPoint.Length == 1 && char.IsLetter(mountPoint[0]))
        {
            mountPoint = mountPoint.ToUpperInvariant() + @":\";
        }

        Dokan dokan;
        try
        {
            dokan = new Dokan(logger);
        }
        catch (DllNotFoundException ex)
        {
            ErrorLoggerStatic.ReportSilentException(ex, "Dokan driver not found during specified mount");
            _loggingService.LogError("Dokan driver not found. Unable to mount archive.");
            ShowDokanNotInstalledDialog();
            return;
        }

        using (dokan)
        {
            _loggingService.Log($"Dokan Library Version: {dokan.Version}");
            _loggingService.Log($"Dokan Driver Version: {dokan.DriverVersion}");
            _loggingService.Log("");

            if (!await AttemptMountLifecycleAsync(archivePath, mountPoint, dokan, archiveType))
            {
                _loggingService.Log($"Error: Failed to mount on '{mountPoint}'.");
            }
            // Event is already fired inside AttemptMountLifecycleAsync when mount succeeds
        }
    }

    private async Task<bool> AttemptMountLifecycleAsync(string archivePath, string mountPoint, Dokan dokan, string archiveType)
    {
        _mountCancellation = new CancellationTokenSource();

        try
        {
            var fileInfo = new FileInfo(archivePath);
            _loggingService.Log($"Processing {archiveType.ToUpperInvariant()} file: '{archivePath}', Size: {fileInfo.Length / 1024.0 / 1024.0:F2} MB");
            _loggingService.Log("");

            // Log the effective RAM cache setting (validation happens in AppSettings)
            var effectiveMaxMemoryBytes = _settingsService.Settings.MaxMemoryPerFileBytes;
            var effectiveMaxMemoryMb = effectiveMaxMemoryBytes / 1024.0 / 1024.0;
            var availableMemoryMb = GC.GetGCMemoryInfo().TotalAvailableMemoryBytes / 1024.0 / 1024.0;
            _loggingService.Log($"RAM cache limit: {effectiveMaxMemoryMb:F0} MB (Available system memory: {availableMemoryMb:F0} MB)");
            _loggingService.Log("");

            Stream fileStream = new FileStream(archivePath, FileMode.Open, System.IO.FileAccess.Read, FileShare.Read);

            try
            {
                var volumeLabel = Path.GetFileNameWithoutExtension(archivePath);
                _currentZipFs = new ZipFs(
                    fileStream,
                    mountPoint,
                    ErrorLoggerStatic.LogErrorSync,
                    () => PromptForPassword(archivePath, archiveType),
                    archiveType,
                    effectiveMaxMemoryBytes,
                    volumeLabel);
            }
            catch
            {
                await fileStream.DisposeAsync();
                throw;
            }

            const int maxRetries = 2;
            const int retryDelayMs = 1000;

            DokanInstance? dokanInstance;
            try
            {
                var builder = new DokanInstanceBuilder(dokan)
                    .ConfigureOptions(options =>
                    {
                        options.Options = DokanOptions.RemovableDrive;
                        options.MountPoint = mountPoint;
                    });

                for (var attempt = 0;; attempt++)
                {
                    try
                    {
                        dokanInstance = builder.Build(_currentZipFs);
                        break;
                    }
                    catch (DokanException) when (attempt < maxRetries)
                    {
                        var delay = retryDelayMs * (attempt + 1);
                        _loggingService.Log($"Dokan driver error, retrying in {delay / 1000}s... (attempt {attempt + 1}/{maxRetries})");
                        await Task.Delay(delay);
                    }
                }
            }
            catch
            {
                _currentZipFs?.Dispose();
                _currentZipFs = null;
                throw;
            }

            using (dokanInstance)
            {
                _loggingService.Log($"Successfully mounted on '{mountPoint}'.");
                _loggingService.Log("");
                _loggingService.Log("Use the Unmount button or close the window to unmount.");
                _loggingService.Log("");

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
            _loggingService.LogError($"Dokan error: {ex.Message}");
            ErrorLoggerStatic.ReportSilentException(ex, $"MountService.AttemptMountLifecycleAsync: DokanException mounting '{archivePath}' to '{mountPoint}'", true);
            ShowDokanDriverErrorDialog(ex.Message);
            return false;
        }
        catch (Exception ex) when (ex.Message.Contains("drive", StringComparison.OrdinalIgnoreCase) ||
                                   ex.Message.Contains("mount", StringComparison.OrdinalIgnoreCase))
        {
            _loggingService.LogError($"Mount error: {ex.Message}");
            ErrorLoggerStatic.ReportSilentException(ex, $"MountService.AttemptMountLifecycleAsync: Drive/mount error for '{archivePath}' to '{mountPoint}'", true);
            return false;
        }
        catch (Exception ex)
        {
            _loggingService.LogError($"Mount error: {ex.Message}");
            ErrorLoggerStatic.LogErrorSync(ex, $"MountService.AttemptMountLifecycleAsync: Error mounting archive '{archivePath}' to '{mountPoint}'");
            return false;
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
            var password = result == true ? passwordWindow.Password : null;
            passwordWindow.ClearPassword();
            return password;
        });
    }
}

/// <summary>
/// ILogger wrapper that guarantees the DokanNet prefix is applied to all log messages.
/// Writes directly to Console.Out, which is redirected to LogTextWriter.
/// </summary>
internal sealed class DokanPrefixedLogger : ILogger, IDisposable
{
    private readonly string _prefix;

    public bool DebugEnabled => true;

    public DokanPrefixedLogger(string prefix)
    {
        _prefix = prefix;
    }

    public void Debug(string message, params object[] args)
    {
        Log("DEBUG", message, args);
    }

    public void Info(string message, params object[] args)
    {
        Log("INFO", message, args);
    }

    public void Warn(string message, params object[] args)
    {
        Log("WARN", message, args);
    }

    public void Error(string message, params object[] args)
    {
        Log("ERROR", message, args);
    }

    public void Fatal(string message, params object[] args)
    {
        Log("FATAL", message, args);
    }

    private void Log(string level, string message, params object[] args)
    {
        var formatted = args.Length > 0 ? string.Format(CultureInfo.InvariantCulture, message, args) : message;
        Console.WriteLine($"{_prefix}[{level}] {formatted}");
    }

    public void Dispose()
    {
    }
}