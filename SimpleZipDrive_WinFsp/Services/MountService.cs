using System.Diagnostics;
using System.Globalization;
using System.Windows;
using Fsp;
using SimpleZipDrive_WinFsp.Views;

namespace SimpleZipDrive_WinFsp.Services;

public class MountService : IDisposable, IMountService
{
    private readonly ILoggingService _loggingService;
    private readonly ISettingsService _settingsService;
    private CancellationTokenSource? _mountCancellation;
    private ZipFs? _currentZipFs;
    private FileSystemHost? _currentHost;

    public event EventHandler<MountStatusChangedEventArgs>? MountStatusChanged;

    public bool IsMounted { get; private set; }

    public string? CurrentMountPoint { get; private set; }

    public string? CurrentArchivePath { get; private set; }

    public MountService(ILoggingService loggingService, ISettingsService settingsService)
    {
        _loggingService = loggingService ?? throw new ArgumentNullException(nameof(loggingService));
        _settingsService = settingsService ?? throw new ArgumentNullException(nameof(settingsService));
    }

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

        if (!IsWinFspInstalled())
        {
            _loggingService.LogError("WinFsp not found. Unable to mount archive.");
            ShowWinFspNotInstalledDialog();
            return Task.CompletedTask;
        }

        CurrentArchivePath = archivePath;

        if (string.IsNullOrEmpty(mountPoint))
        {
            return MountWithAutoDriveLetterAsync(archivePath, archiveType);
        }
        else
        {
            return MountWithSpecifiedPointAsync(archivePath, mountPoint, archiveType);
        }
    }

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

            if (cts != null) await Task.Delay(500, cts.Token);

            _currentHost?.Dispose();
            _currentHost = null;
            _currentZipFs?.Dispose();
            _currentZipFs = null;

            CurrentMountPoint = null;
            CurrentArchivePath = null;

            _loggingService.Log("Drive unmounted successfully.");
            OnMountStatusChanged();
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _loggingService.LogError($"Error unmounting drive: {ex.Message}");
            ErrorLoggerStatic.LogErrorSync(ex, "MountService.UnmountAsync: Error unmounting drive");
            throw;
        }
    }

    public string GetArchiveType(string filePath)
    {
        var extension = Path.GetExtension(filePath).ToLowerInvariant();
        return extension.TrimStart('.');
    }

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
        _currentHost?.Dispose();
        _currentZipFs?.Dispose();
        GC.SuppressFinalize(this);
    }

    private static bool IsWinFspInstalled()
    {
        try
        {
            var version = FileSystemHost.Version();
            return version != null;
        }
        catch
        {
            return false;
        }
    }

    private static void ShowWinFspNotInstalledDialog()
    {
        const string message = "The WinFsp file system driver is required to mount archives as virtual drives. " +
                               "It does not appear to be installed on this system.\n\n" +
                               "Would you like to open the WinFsp download page?";

        var result = MessageBox.Show(message, "WinFsp Not Found",
            MessageBoxButton.YesNo, MessageBoxImage.Warning);

        if (result == MessageBoxResult.Yes)
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = "https://github.com/winfsp/winfsp/releases",
                UseShellExecute = true
            });
        }
    }

    private static void ShowWinFspDriverErrorDialog(string errorMessage)
    {
        const string message = "The WinFsp file system driver encountered an error:\n\n" +
                               "\"{0}\"\n\n" +
                               "Your WinFsp driver may be outdated, corrupted, or not configured correctly. " +
                               "Please try reinstalling or updating the WinFsp driver.\n\n" +
                               "Would you like to open the WinFsp download page?";

        var formattedMessage = string.Format(CultureInfo.CurrentCulture, message, errorMessage);

        var result = MessageBox.Show(formattedMessage, "WinFsp Driver Error",
            MessageBoxButton.YesNo, MessageBoxImage.Warning);

        if (result == MessageBoxResult.Yes)
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = "https://github.com/winfsp/winfsp/releases",
                UseShellExecute = true
            });
        }
    }

    private async Task MountWithAutoDriveLetterAsync(string archivePath, string archiveType)
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

            _loggingService.Log($"Attempting to mount on '{currentMountPoint}'...");

            if (await AttemptMountLifecycleAsync(archivePath, currentMountPoint, archiveType))
            {
                return;
            }
        }

        _loggingService.Log("Error: Failed to auto-mount on any preferred drive letters.");
    }

    private async Task MountWithSpecifiedPointAsync(string archivePath, string mountPoint, string archiveType)
    {
        if (mountPoint.Length == 1 && char.IsLetter(mountPoint[0]))
        {
            mountPoint = mountPoint.ToUpperInvariant() + @":\";
        }

        if (!await AttemptMountLifecycleAsync(archivePath, mountPoint, archiveType))
        {
            _loggingService.Log($"Error: Failed to mount on '{mountPoint}'.");
        }
    }

    private async Task<bool> AttemptMountLifecycleAsync(string archivePath, string mountPoint, string archiveType)
    {
        _mountCancellation = new CancellationTokenSource();

        try
        {
            var fileInfo = new FileInfo(archivePath);
            _loggingService.Log($"Processing {archiveType.ToUpperInvariant()} file: '{archivePath}', Size: {fileInfo.Length / 1024.0 / 1024.0:F2} MB");
            _loggingService.Log("");

            var effectiveMaxMemoryBytes = _settingsService.Settings.MaxMemoryPerFileBytes;
            var effectiveMaxMemoryMb = effectiveMaxMemoryBytes / 1024.0 / 1024.0;
            var availableMemoryMb = GC.GetGCMemoryInfo().TotalAvailableMemoryBytes / 1024.0 / 1024.0;
            _loggingService.Log($"RAM cache limit: {effectiveMaxMemoryMb:F0} MB (Available system memory: {availableMemoryMb:F0} MB)");
            _loggingService.Log("");

            Stream fileStream = new FileStream(archivePath, FileMode.Open, FileAccess.Read, FileShare.Read);

            try
            {
                _currentZipFs = new ZipFs(
                    fileStream,
                    mountPoint,
                    ErrorLoggerStatic.LogErrorSync,
                    () => PromptForPassword(archivePath, archiveType),
                    archiveType,
                    effectiveMaxMemoryBytes);
            }
            catch
            {
                await fileStream.DisposeAsync();
                throw;
            }

            var host = new FileSystemHost(_currentZipFs);

            try
            {
                var status = host.Mount(mountPoint);

                if (status != 0)
                {
                    _currentZipFs?.Dispose();
                    _currentZipFs = null;
                    host.Dispose();
                    _loggingService.LogError($"WinFsp mount failed with status 0x{status:X8}");
                    ShowWinFspDriverErrorDialog($"Mount failed with status 0x{status:X8}");
                    return false;
                }
            }
            catch (Exception ex)
            {
                _currentZipFs?.Dispose();
                _currentZipFs = null;
                host.Dispose();
                _loggingService.LogError($"WinFsp mount error: {ex.Message}");
                ShowWinFspDriverErrorDialog(ex.Message);
                ErrorLoggerStatic.ReportSilentException(ex, $"MountService.AttemptMountLifecycleAsync: Error mounting '{archivePath}' to '{mountPoint}'", true);
                return false;
            }

            _currentHost = host;

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
            host.Unmount();

            return true;
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

    private static string? PromptForPassword(string archivePath, string archiveType)
    {
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
