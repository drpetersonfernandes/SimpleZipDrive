using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Windows;
using Fsp;
using Microsoft.Win32;

namespace SimpleZipDrive_WinFsp.Services;

public class MountService : IDisposable, IMountService
{
    [DllImport("kernel32.dll", CharSet = CharSet.Auto, SetLastError = true)]
    private static extern bool DefineDosDevice(int dwFlags, string lpDeviceName, string? lpTargetPath);

    private const int DddRemoveDefinition = 0x00000002;
    private const int DddExactMatchOnRemove = 0x00000004;

    private readonly ILoggingService _loggingService;
    private readonly ISettingsService _settingsService;
    private CancellationTokenSource? _mountCancellation;
    private ZipFs? _currentZipFs;
    private FileSystemHost? _currentHost;

    // For admin directory-based mounts: we mount WinFsp to a directory
    // then map a drive letter to it via DefineDosDevice for session-wide visibility
    private string? _mountDirectoryPath;
    private string? _driveLetterMapping;

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
        DiagnosticLogger.LogSection($"UNMOUNT REQUESTED: {CurrentMountPoint}");
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

            // Remove any DefineDosDevice drive letter mapping we created
            if (!string.IsNullOrEmpty(_driveLetterMapping))
            {
                try
                {
                    DefineDosDevice(DddRemoveDefinition | DddExactMatchOnRemove, _driveLetterMapping, null);
                    DiagnosticLogger.Log($"  Removed drive mapping: {_driveLetterMapping}");
                }
                catch (Exception ex)
                {
                    DiagnosticLogger.Log(ex, "Failed to remove drive mapping");
                }

                _driveLetterMapping = null;
            }

            _currentHost?.Dispose();
            _currentHost = null;
            _currentZipFs?.Dispose();
            _currentZipFs = null;

            // Clean up mount directory if we used one
            if (!string.IsNullOrEmpty(_mountDirectoryPath) && Directory.Exists(_mountDirectoryPath))
            {
                try
                {
                    Directory.Delete(_mountDirectoryPath, false);
                    DiagnosticLogger.Log($"  Removed mount directory: {_mountDirectoryPath}");
                }
                catch (Exception ex)
                {
                    DiagnosticLogger.Log(ex, $"Failed to remove mount directory {_mountDirectoryPath}");
                }

                _mountDirectoryPath = null;
            }

            CurrentMountPoint = null;
            CurrentArchivePath = null;

            _loggingService.Log("Drive unmounted successfully.");
            OnMountStatusChanged();
        }
        catch (OperationCanceledException)
        {
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
        if (!EnsureWinFspOnPath())
            return false;

        return true;
    }

    private static bool EnsureWinFspOnPath()
    {
        try
        {
            var currentPath = Environment.GetEnvironmentVariable("PATH") ?? string.Empty;
            if (currentPath.Contains("WinFsp", StringComparison.OrdinalIgnoreCase))
                return true;

            string? binDir = null;

            using var key = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\WOW6432Node\WinFsp");
            var sxsDir = key?.GetValue("SxsDir") as string;
            if (!string.IsNullOrEmpty(sxsDir))
            {
                var sxsBin = Path.Combine(sxsDir, "bin");
                if (Directory.Exists(sxsBin))
                {
                    binDir = sxsBin;
                }
            }

            if (binDir == null)
            {
                var installDir = key?.GetValue("InstallDir") as string;
                if (!string.IsNullOrEmpty(installDir))
                {
                    var installBin = Path.Combine(installDir, "bin");
                    if (Directory.Exists(installBin))
                    {
                        binDir = installBin;
                    }
                }
            }

            if (binDir == null)
                return false;

            var dllName = Environment.Is64BitProcess ? "winfsp-x64.dll" : "winfsp-x86.dll";
            var dllPath = Path.Combine(binDir, dllName);
            if (!File.Exists(dllPath))
                return false;

            Environment.SetEnvironmentVariable("PATH", binDir + ";" + currentPath, EnvironmentVariableTarget.Process);
            return true;
        }
        catch (Exception ex)
        {
            ErrorLoggerStatic.ReportSilentException(ex, "MountService.EnsureWinFspOnPath: Failed", true);
            return false;
        }
    }

    private static string GetDeepestMessage(Exception ex)
    {
        var current = ex;
        while (current.InnerException != null)
        {
            current = current.InnerException;
        }

        return current.Message;
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
        DiagnosticLogger.Log("  Auto-mount: trying drive letters...");
        char[] preferredDriveLetters = ['M', 'N', 'O', 'P', 'Q'];
        var existingDrives = DriveInfo.GetDrives().Select(static d => d.Name).ToList();

        foreach (var letter in preferredDriveLetters)
        {
            var driveCheck = letter + @":\";

            if (existingDrives.Any(d => string.Equals(d, driveCheck, StringComparison.OrdinalIgnoreCase)))
            {
                _loggingService.Log($"Skipping '{driveCheck}' (already in use).");
                continue;
            }

            var currentMountPoint = letter + ":";

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
            mountPoint = mountPoint.ToUpperInvariant() + ":";
        }

        if (!await AttemptMountLifecycleAsync(archivePath, mountPoint, archiveType))
        {
            _loggingService.Log($"Error: Failed to mount on '{mountPoint}'.");
        }
    }

    private async Task<bool> AttemptMountLifecycleAsync(string archivePath, string mountPoint, string archiveType)
    {
        var isDriveLetter = IsDriveLetterMountPoint(mountPoint);
        var isAdmin = CheckForAdministratorRole.IsAdministrator();

        // Admin drive-letter: mount to a temp directory, then use DefineDosDevice
        // to create a session-wide drive letter mapping (same as subst).
        // Directory mounts need admin for NTFS reparse points.
        var winFspMountPoint = mountPoint;

        if (isDriveLetter && isAdmin)
        {
            _mountDirectoryPath = Path.Combine(Path.GetTempPath(), "SimpleZipDrive", $"mount_{Guid.NewGuid():N}");
            Directory.CreateDirectory(_mountDirectoryPath);
            winFspMountPoint = _mountDirectoryPath;
            _loggingService.Log($"Admin mode: mounting to directory, then mapping {mountPoint} via DefineDosDevice.");
        }

        DiagnosticLogger.LogSection($"MOUNT START: {archivePath} -> {mountPoint} (WinFsp: {winFspMountPoint})");
        DiagnosticLogger.Log($"  Archive type: {archiveType}");
        DiagnosticLogger.Log($"  IsAdmin: {isAdmin}, IsDriveLetter: {isDriveLetter}");

        try
        {
            Directory.CreateDirectory(mountPoint);
        }
        catch
        {
            // ignored
        }

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
                DiagnosticLogger.Log("  Creating ZipFs instance...");
                _currentZipFs = new ZipFs(
                    fileStream,
                    mountPoint,
                    ErrorLoggerStatic.LogErrorSync,
                    () => PromptForPassword(archivePath, archiveType),
                    archiveType,
                    effectiveMaxMemoryBytes);
                DiagnosticLogger.Log("  ZipFs created successfully.");
            }
            catch
            {
                await fileStream.DisposeAsync();
                throw;
            }

            var host = new FileSystemHost(_currentZipFs);

            try
            {
                try
                {
                    var winfspDebugLogPath = Path.Combine(
                        Path.GetDirectoryName(DiagnosticLogger.LogFilePath) ?? AppDomain.CurrentDomain.BaseDirectory,
                        $"winfsp_debug_{DateTime.Now:yyyyMMdd_HHmmss}.log");
                    FileSystemHost.SetDebugLogFile(winfspDebugLogPath);
                    DiagnosticLogger.Log($"  WinFsp debug log: {winfspDebugLogPath}");
                }
                catch (Exception debugLogEx)
                {
                    DiagnosticLogger.Log($"  WinFsp debug log setup failed (non-fatal): {debugLogEx.Message}");
                }

                DiagnosticLogger.Log($"  Calling host.Mount(\"{winFspMountPoint}\", DebugLog=-1)...");
                var mountStatus = host.Mount(winFspMountPoint, null, false, unchecked((uint)-1));

                if (mountStatus != 0)
                {
                    DiagnosticLogger.Log($"  host.Mount FAILED: 0x{mountStatus:X8}");
                    if (winFspMountPoint != mountPoint)
                    {
                        DiagnosticLogger.Log("  Directory mount failed, trying direct drive letter mount...");
                        mountStatus = host.Mount(mountPoint, null, false, unchecked((uint)-1));
                    }
                }

                if (mountStatus != 0)
                {
                    DiagnosticLogger.Log($"  All mount attempts FAILED: 0x{mountStatus:X8}");
                    CleanupMountDirectory();
                    _currentZipFs?.Dispose();
                    _currentZipFs = null;
                    host.Dispose();
                    _loggingService.LogError($"WinFsp mount failed with status 0x{mountStatus:X8}");
                    ShowWinFspDriverErrorDialog($"Mount failed with status 0x{mountStatus:X8}");
                    return false;
                }

                // Create drive letter mapping for directory-based admin mounts
                if (!string.IsNullOrEmpty(_mountDirectoryPath) && isDriveLetter)
                {
                    if (DefineDosDevice(0, mountPoint, _mountDirectoryPath))
                    {
                        _driveLetterMapping = mountPoint;
                        DiagnosticLogger.Log($"  DefineDosDevice: {mountPoint} -> {_mountDirectoryPath} (session-wide)");
                        _loggingService.Log($"Drive {mountPoint} mapped for session-wide visibility. Drag-and-drop should work.");
                    }
                    else
                    {
                        var error = Marshal.GetLastWin32Error();
                        DiagnosticLogger.Log($"  DefineDosDevice FAILED: Win32 error {error}");
                        _loggingService.Log($"Warning: Could not create drive letter mapping (error {error}).");
                    }
                }
            }
            catch (Exception ex)
            {
                DiagnosticLogger.Log(ex, "Mount exception");
                CleanupMountDirectory();
                _currentZipFs?.Dispose();
                _currentZipFs = null;
                host.Dispose();
                var detail = GetDeepestMessage(ex);
                _loggingService.LogError($"WinFsp mount error: {detail}");
                ShowWinFspDriverErrorDialog(detail);
                ErrorLoggerStatic.ReportSilentException(ex, $"MountService.AttemptMountLifecycleAsync: Error mounting '{archivePath}' to '{mountPoint}'", true);
                return false;
            }

            _currentHost = host;

            DiagnosticLogger.Log($"  Mounted successfully on '{mountPoint}'.");
            DiagnosticLogger.LogSection($"MOUNT SUCCESS: {mountPoint}");

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
            DiagnosticLogger.LogSection($"UNMOUNT: {mountPoint}");
            if (_currentHost == host)
            {
                try
                {
                    host.Unmount();
                }
                catch (Exception unmountEx)
                {
                    DiagnosticLogger.Log(unmountEx, "host.Unmount() failed (non-fatal)");
                }
            }

            return true;
        }
        catch (Exception ex) when (ex.Message.Contains("drive", StringComparison.OrdinalIgnoreCase) ||
                                   ex.Message.Contains("mount", StringComparison.OrdinalIgnoreCase))
        {
            DiagnosticLogger.Log(ex, "Mount error (drive/mount)");
            _loggingService.LogError($"Mount error: {ex.Message}");
            ErrorLoggerStatic.ReportSilentException(ex, $"MountService.AttemptMountLifecycleAsync: Drive/mount error for '{archivePath}' to '{mountPoint}'", true);
            return false;
        }
        catch (Exception ex)
        {
            DiagnosticLogger.Log(ex, "Mount error (general)");
            _loggingService.LogError($"Mount error: {ex.Message}");
            ErrorLoggerStatic.LogErrorSync(ex, $"MountService.AttemptMountLifecycleAsync: Error mounting archive '{archivePath}' to '{mountPoint}'");
            return false;
        }
    }

    private void CleanupMountDirectory()
    {
        if (!string.IsNullOrEmpty(_mountDirectoryPath) && Directory.Exists(_mountDirectoryPath))
        {
            try
            {
                Directory.Delete(_mountDirectoryPath, false);
            }
            catch
            {
                // ignored
            }
        }

        _mountDirectoryPath = null;
        _driveLetterMapping = null;
    }

    private static bool IsDriveLetterMountPoint(string mountPoint)
    {
        return mountPoint is [_, ':'] && char.IsLetter(mountPoint[0]);
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
            var password = result == true ? passwordWindow.Password : null;
            passwordWindow.ClearPassword();
            return password;
        });
    }
}
