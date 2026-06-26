using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Windows;
using Fsp;
using Microsoft.Win32;

#pragma warning disable CA1707
namespace SimpleZipDrive_WinFsp.Services;
#pragma warning restore CA1707

public class MountService : IDisposable, IMountService
{
    private static readonly Version RequiredWinFspVersion = new(2, 2);

    // NTSTATUS codes relevant to WinFsp mount operations
    private const int StatusSuccess = 0x00000000;
    private const int StatusObjectNameNotFound = unchecked((int)0xC0000034);
    private const int StatusAccessDenied = unchecked((int)0xC0000022);
    private const int StatusInsufficientResources = unchecked((int)0xC000009A);
    private const int StatusDeviceAlreadyExists = unchecked((int)0xC0000038);
    private const int StatusObjectPathNotFound = unchecked((int)0xC000003A);
    private const int StatusNoSuchDevice = unchecked((int)0xC000000E);

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

    [RequiresAssemblyFiles]
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
        var supportedTypes = new[] { "zip", "7z", "rar", "tar" };

        if (!supportedTypes.Contains(archiveType, StringComparer.OrdinalIgnoreCase))
        {
            _loggingService.Log($"\n{AppTheme.Section("INVALID FILE TYPE")}");
            _loggingService.Log($"Error: The file '{Path.GetFileName(archivePath)}' is not a supported archive.");
            throw new ArgumentException(
                $"The file '{Path.GetFileName(archivePath)}' is not a supported archive format (expected .zip, .7z, .rar, .tar, .tar.gz, .tar.bz2, or .tar.xz).",
                nameof(archivePath));
        }

        if (!IsWinFspInstalled())
        {
            _loggingService.LogError("WinFsp not found. Unable to mount archive.");
            ShowWinFspNotInstalledDialog();
            return Task.CompletedTask;
        }

        CurrentArchivePath = archivePath;

        var crossIntegrity = _settingsService.Settings.CrossIntegrityMount;

        if (!crossIntegrity && IsRunningAsAdministrator())
        {
            crossIntegrity = true;
            _loggingService.Log("Running as Administrator: Cross-integrity mount enforced so standard processes can access the drive.");
        }

        if (string.IsNullOrEmpty(mountPoint))
        {
            if (crossIntegrity)
            {
                return MountWithCrossIntegrityFolderAsync(archivePath, archiveType);
            }

            return MountWithAutoDriveLetterAsync(archivePath, archiveType);
        }
        else
        {
            if (crossIntegrity && IsDriveLetterMountPoint(mountPoint))
            {
                _loggingService.Log("Cross-integrity mode: Drive letter mounts are not supported. Redirecting to folder mount.");
                var folderPath = GetCrossIntegrityMountPath(archivePath);
                return MountWithSpecifiedPointAsync(archivePath, folderPath, archiveType);
            }

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

            try
            {
                if (cts != null) await Task.Delay(500, cts.Token);
            }
            catch (OperationCanceledException)
            {
                // Expected if cancellation completes before delay
            }

            var host = Interlocked.Exchange(ref _currentHost, null);
            host?.Dispose();
            _currentZipFs?.Dispose();
            _currentZipFs = null;

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
        var fileName = Path.GetFileName(filePath).ToLowerInvariant();

        if (fileName.EndsWith(".tar.gz", StringComparison.Ordinal) || fileName.EndsWith(".tar.bz2", StringComparison.Ordinal) || fileName.EndsWith(".tar.xz", StringComparison.Ordinal) ||
            fileName.EndsWith(".tgz", StringComparison.Ordinal) || fileName.EndsWith(".tbz2", StringComparison.Ordinal) || fileName.EndsWith(".txz", StringComparison.Ordinal))
        {
            return "tar";
        }

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

        // Give the driver time to finish pending callbacks before disposing resources
        Thread.Sleep(500);

        _mountCancellation?.Dispose();
        var host = Interlocked.Exchange(ref _currentHost, null);
        host?.Dispose();
        _currentZipFs?.Dispose();
        _currentZipFs = null;
        CurrentArchivePath = null;
        GC.SuppressFinalize(this);
    }

    private static bool IsWinFspInstalled()
    {
        if (!EnsureWinFspOnPath())
            return false;

        return true;
    }

    private static bool IsWinFspDriverRunning()
    {
        try
        {
            // Check if WinFsp driver DLL exists and is accessible
            var installDir = GetWinFspInstallDir();
            if (string.IsNullOrEmpty(installDir))
                return false;

            var dllName = Environment.Is64BitProcess ? "winfsp-x64.dll" : "winfsp-x86.dll";
            var dllPath = Path.Combine(installDir, "bin", dllName);

            if (!File.Exists(dllPath))
                return false;

            // Try to check if the driver service is registered via sc query
            try
            {
                var processInfo = new ProcessStartInfo
                {
                    FileName = "sc",
                    Arguments = "query WinFsp.Launcher",
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardOutput = true
                };

                using var process = Process.Start(processInfo);
                if (process != null)
                {
                    var output = process.StandardOutput.ReadToEnd();
                    process.WaitForExit();
                    return output.Contains("RUNNING", StringComparison.OrdinalIgnoreCase);
                }
            }
            catch
            {
                // Fallback: assume driver is running if DLL exists
                return true;
            }

            return true;
        }
        catch
        {
            return false;
        }
    }

    private static string? GetWinFspInstallDir()
    {
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\WOW6432Node\WinFsp")
                            ?? Registry.LocalMachine.OpenSubKey(@"SOFTWARE\WinFsp");
            return key?.GetValue("InstallDir") as string;
        }
        catch
        {
            return null;
        }
    }

    private static bool VerifyMountPoint(string mountPoint, bool isDriveLetter)
    {
        try
        {
            if (isDriveLetter)
            {
                // For drive letters, just check if the letter is available
                var drives = DriveInfo.GetDrives();
                return !drives.Any(d => d.Name.StartsWith(mountPoint, StringComparison.OrdinalIgnoreCase));
            }

            // For folder mounts, verify we can create/access the directory
            if (!Directory.Exists(mountPoint))
            {
                Directory.CreateDirectory(mountPoint);
            }

            // Test write access by creating and deleting a temp file
            var testFile = Path.Combine(mountPoint, $".sfz_test_{Guid.NewGuid():N}");
            File.WriteAllText(testFile, "test");
            File.Delete(testFile);
            return true;
        }
        catch (Exception ex)
        {
            DiagnosticLogger.Log($"Mount point verification failed for '{mountPoint}': {ex.Message}");
            return false;
        }
    }

    private static string GetMountStatusErrorMessage(int statusCode)
    {
        return statusCode switch
        {
            StatusObjectNameNotFound => "The WinFsp driver was not found or is not running. Please install or start the WinFsp service.",
            StatusObjectPathNotFound => "The mount point path was not found. Please verify the path exists and is accessible.",
            StatusAccessDenied => "Access denied. Please run as administrator or check permissions.",
            StatusInsufficientResources => "Insufficient system resources. Please close other applications and try again.",
            StatusDeviceAlreadyExists => "A device already exists at this mount point. Please choose a different location.",
            StatusNoSuchDevice => "The WinFsp device is not available. Please verify the WinFsp driver is installed and running.",
            _ => $"Mount failed with status 0x{unchecked((uint)statusCode):X8}. This may be caused by an outdated WinFsp driver."
        };
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

    private static Version? GetInstalledWinFspVersion()
    {
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\WOW6432Node\WinFsp")
                            ?? Registry.LocalMachine.OpenSubKey(@"SOFTWARE\WinFsp");
            if (key == null)
                return null;

            var versionStr = key.GetValue("Version") as string;
            if (!string.IsNullOrEmpty(versionStr) && Version.TryParse(versionStr, out var version))
                return version;

            var installDir = key.GetValue("InstallDir") as string;
            if (!string.IsNullOrEmpty(installDir))
            {
                var dllName = Environment.Is64BitProcess ? "winfsp-x64.dll" : "winfsp-x86.dll";
                var dllPath = Path.Combine(installDir, "bin", dllName);
                if (File.Exists(dllPath))
                {
                    var fvi = FileVersionInfo.GetVersionInfo(dllPath);
                    if (fvi.FileVersion != null && Version.TryParse(fvi.FileVersion, out var fileVersion))
                        return fileVersion;
                }
            }
        }
        catch
        {
            // Best-effort; version detection failure is non-fatal
        }

        return null;
    }

    [RequiresAssemblyFiles("Calls System.Reflection.Assembly.Location")]
    private static Version? GetWinFspLibraryVersion()
    {
        try
        {
            var assembly = typeof(FileSystemHost).Assembly;
            var location = assembly.Location;

            if (string.IsNullOrEmpty(location))
            {
                var baseDir = AppContext.BaseDirectory;
                var candidate = Path.Combine(baseDir, "winfsp-msil.dll");
                if (File.Exists(candidate))
                {
                    location = candidate;
                }
            }

            if (!string.IsNullOrEmpty(location) && File.Exists(location))
            {
                var fvi = FileVersionInfo.GetVersionInfo(location);
                if (fvi.FileVersion != null && Version.TryParse(fvi.FileVersion, out var version))
                    return version;
            }

            return assembly.GetName().Version;
        }
        catch
        {
            // Best-effort; version detection failure is non-fatal
            return null;
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

    private static bool ShowWinFspVersionMismatchWarningDialog(Version installed, Version required)
    {
        var message = $"WinFsp version mismatch detected.\n\n" +
                      $"Installed version: {installed.Major}.{installed.Minor}\n" +
                      $"Required version: {required.Major}.{required.Minor} or later\n\n" +
                      "The installed WinFsp driver is older than recommended. " +
                      "Mounting may fail or not work correctly with this version.\n\n" +
                      "Would you like to update WinFsp first?\n\n" +
                      "• Yes - Open the WinFsp download page\n" +
                      "• No - Try to mount anyway (may fail)";

        var result = MessageBox.Show(message, "WinFsp Version Warning",
            MessageBoxButton.YesNo, MessageBoxImage.Warning);

        if (result == MessageBoxResult.Yes)
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = "https://github.com/winfsp/winfsp/releases",
                UseShellExecute = true
            });
            return false; // User chose to update, don't continue mounting
        }

        return true; // User chose to try anyway
    }

    private static void ShowWinFspVersionMismatchFailedDialog(Version installed, Version required)
    {
        var message = $"Mount failed due to WinFsp version mismatch.\n\n" +
                      $"Installed version: {installed.Major}.{installed.Minor}\n" +
                      $"Required version: {required.Major}.{required.Minor} or later\n\n" +
                      "The installed WinFsp driver is too old. " +
                      "Please update WinFsp to the required version to mount archives.\n\n" +
                      "Would you like to open the WinFsp download page to update now?";

        var result = MessageBox.Show(message, "WinFsp Update Required",
            MessageBoxButton.YesNo, MessageBoxImage.Error);

        if (result == MessageBoxResult.Yes)
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = "https://github.com/winfsp/winfsp/releases",
                UseShellExecute = true
            });
        }
    }

    private static void ShowWinFspMountFailedUpdateDialog(string errorDetail)
    {
        var message = $"Mount failed.\n\n" +
                      $"Error: {errorDetail}\n\n" +
                      "This may be caused by an outdated WinFsp driver. " +
                      "Please update WinFsp to the latest version and try again.\n\n" +
                      "Would you like to open the WinFsp download page?";

        var result = MessageBox.Show(message, "Mount Failed - Update WinFsp",
            MessageBoxButton.YesNo, MessageBoxImage.Error);

        if (result == MessageBoxResult.Yes)
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = "https://github.com/winfsp/winfsp/releases",
                UseShellExecute = true
            });
        }
    }

    private static bool IsVersionMismatchError(Exception ex)
    {
        var deepest = GetDeepestMessage(ex);
        return deepest.Contains("incorrect dll version", StringComparison.OrdinalIgnoreCase);
    }

    private static Version? ExtractVersionFromMismatchMessage(string message)
    {
        // Parse "incorrect dll version (need 2.2, have 2.1)" format
        try
        {
            var haveIndex = message.IndexOf("have ", StringComparison.OrdinalIgnoreCase);
            if (haveIndex >= 0)
            {
                var versionStr = message.Substring(haveIndex + 5).TrimEnd(')');
                if (Version.TryParse(versionStr, out var version))
                    return version;
            }
        }
        catch
        {
            // Best-effort parsing
        }

        return null;
    }

    [RequiresAssemblyFiles("Calls SimpleZipDrive_WinFsp.Services.MountService.AttemptMountLifecycleAsync(String, String, String)")]
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

    [RequiresAssemblyFiles("Calls SimpleZipDrive_WinFsp.Services.MountService.AttemptMountLifecycleAsync(String, String, String)")]
    private async Task MountWithCrossIntegrityFolderAsync(string archivePath, string archiveType)
    {
        var mountPoint = GetCrossIntegrityMountPath(archivePath);
        _loggingService.Log($"Cross-integrity mode: mounting to folder '{mountPoint}'.");

        if (!await AttemptMountLifecycleAsync(archivePath, mountPoint, archiveType))
        {
            _loggingService.Log($"Error: Failed to cross-integrity mount on '{mountPoint}'.");
        }
    }

    [RequiresAssemblyFiles("Calls SimpleZipDrive_WinFsp.Services.MountService.AttemptMountLifecycleAsync(String, String, String)")]
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

    [RequiresAssemblyFiles("Calls SimpleZipDrive_WinFsp.Services.MountService.GetWinFspLibraryVersion()")]
    private async Task<bool> AttemptMountLifecycleAsync(string archivePath, string mountPoint, string archiveType)
    {
        var isDriveLetter = IsDriveLetterMountPoint(mountPoint);

        DiagnosticLogger.LogSection($"MOUNT START: {archivePath} -> {mountPoint}");
        DiagnosticLogger.Log($"  Archive type: {archiveType}");
        DiagnosticLogger.Log($"  IsDriveLetter: {isDriveLetter}");

        // Check if WinFsp driver service is running
        if (!IsWinFspDriverRunning())
        {
            _loggingService.LogError("WinFsp driver service is not running. Please start the WinFsp.Launcher service.");
            DiagnosticLogger.Log("WinFsp driver service check failed - service not running.");
            ShowWinFspDriverErrorDialog("The WinFsp driver service is not running. Please start the WinFsp.Launcher service and try again.");
            return false;
        }

        // Verify mount point accessibility
        if (!VerifyMountPoint(mountPoint, isDriveLetter))
        {
            _loggingService.LogError($"Mount point '{mountPoint}' is not accessible or cannot be created.");
            DiagnosticLogger.Log($"Mount point verification failed for '{mountPoint}'.");
            ShowWinFspDriverErrorDialog($"The mount point '{mountPoint}' is not accessible. Please choose a different location or check permissions.");
            return false;
        }

        try
        {
            if (!isDriveLetter) // Only create directory if it's a directory mount point
            {
                Directory.CreateDirectory(mountPoint);
            }
        }
        catch (Exception ex)
        {
            ErrorLoggerStatic.ReportSilentException(ex, $"Failed to create mount directory '{mountPoint}'");
            DiagnosticLogger.Log(ex, $"Failed to create mount directory '{mountPoint}'");
            _loggingService.LogError($"Error: Failed to create mount directory '{mountPoint}'. {ex.Message}");
            return false;
        }

        _mountCancellation?.Dispose();
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

            var crossIntegrity = (_settingsService.Settings.CrossIntegrityMount || IsRunningAsAdministrator()) && !isDriveLetter;

            Stream fileStream = new FileStream(archivePath, FileMode.Open, FileAccess.Read, FileShare.Read);

            try
            {
                DiagnosticLogger.Log("  Creating ZipFs instance...");
                var volumeLabel = ZipFsHelpers.SanitizeVolumeLabel(ZipFsHelpers.GetArchiveFileNameWithoutExtension(archivePath));
                _currentZipFs = new ZipFs(
                    fileStream,
                    mountPoint,
                    ErrorLoggerStatic.LogErrorSync,
                    () => PromptForPassword(archivePath, archiveType),
                    archiveType,
                    effectiveMaxMemoryBytes,
                    volumeLabel,
                    crossIntegrity);
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

                var libraryVersion = GetWinFspLibraryVersion();
                if (libraryVersion != null)
                    _loggingService.Log($"WinFsp Library Version: {libraryVersion.Major}.{libraryVersion.Minor}.{libraryVersion.Build}");

                if (libraryVersion != null && libraryVersion < RequiredWinFspVersion)
                {
                    _loggingService.LogError($"WinFsp MSIL library version {libraryVersion.Major}.{libraryVersion.Minor} is incompatible with this application. Version {RequiredWinFspVersion.Major}.{RequiredWinFspVersion.Minor} or later is required. Mount cannot proceed.");
                    DiagnosticLogger.Log($"  Blocked mount: MSIL library version {libraryVersion} < Required {RequiredWinFspVersion}.");
                    ShowWinFspMountFailedUpdateDialog($"WinFsp library version {libraryVersion.Major}.{libraryVersion.Minor} is incompatible. Please update the WinFsp NuGet package to version {RequiredWinFspVersion.Major}.{RequiredWinFspVersion.Minor} or later.");
                    _currentZipFs?.Dispose();
                    _currentZipFs = null;
                    host.Dispose();
                    return false;
                }

                var installedVersion = GetInstalledWinFspVersion();
                if (installedVersion != null)
                {
                    _loggingService.Log($"WinFsp Driver Version: {installedVersion.Major}.{installedVersion.Minor}.{installedVersion.Build}");
                    DiagnosticLogger.Log($"  Installed WinFsp version: {installedVersion}, Required: {RequiredWinFspVersion}");
                    if (installedVersion < RequiredWinFspVersion)
                    {
                        _loggingService.Log($"WARNING: WinFsp version mismatch: installed {installedVersion.Major}.{installedVersion.Minor}, recommended {RequiredWinFspVersion.Major}.{RequiredWinFspVersion.Minor}.");
                        var continueWithOldVersion = ShowWinFspVersionMismatchWarningDialog(installedVersion, RequiredWinFspVersion);
                        if (!continueWithOldVersion)
                        {
                            _currentZipFs?.Dispose();
                            _currentZipFs = null;
                            host.Dispose();
                            return false;
                        }

                        _loggingService.Log("WARNING: Continuing with older WinFsp version. Mount may fail.");
                    }
                }
                else
                {
                    DiagnosticLogger.Log("  Could not determine installed WinFsp version.");
                }

                DiagnosticLogger.Log($"  Calling host.Mount(\"{mountPoint}\", DebugLog=-1)...");
                var securityDescriptor = crossIntegrity ? CreateCrossIntegritySecurityDescriptor() : null;
                if (securityDescriptor != null)
                    DiagnosticLogger.Log("  Cross-integrity: using permissive DACL (Everyone Full Access).");
                var mountStatus = host.Mount(mountPoint, securityDescriptor, false, unchecked((uint)-1));

                if (mountStatus != 0)
                {
                    DiagnosticLogger.Log($"  host.Mount FAILED: 0x{mountStatus:X8}");
                    _currentZipFs?.Dispose();
                    _currentZipFs = null;
                    host.Dispose();

                    var specificError = GetMountStatusErrorMessage(mountStatus);
                    _loggingService.LogError($"WinFsp mount failed with status 0x{mountStatus:X8}: {specificError}");
                    DiagnosticLogger.Log($"  Specific error: {specificError}");

                    switch (mountStatus)
                    {
                        // Show specific error dialog based on status code
                        case StatusObjectNameNotFound:
                        case StatusNoSuchDevice:
                        case StatusAccessDenied:
                            ShowWinFspDriverErrorDialog(specificError);
                            break;
                        default:
                            ShowWinFspMountFailedUpdateDialog(specificError);
                            break;
                    }
                    return false;
                }
            }
            catch (Exception ex)
            {
                DiagnosticLogger.Log(ex, "Mount exception");
                _currentZipFs?.Dispose();
                _currentZipFs = null;
                host.Dispose();
                var detail = GetDeepestMessage(ex);
                _loggingService.LogError($"WinFsp mount error: {detail}");

                if (IsVersionMismatchError(ex))
                {
                    var installed = GetInstalledWinFspVersion();
                    if (installed != null)
                    {
                        _loggingService.LogError($"Mount failed: WinFsp version mismatch. Installed: {installed.Major}.{installed.Minor}, Required: {RequiredWinFspVersion.Major}.{RequiredWinFspVersion.Minor}.");
                        ShowWinFspVersionMismatchFailedDialog(installed, RequiredWinFspVersion);
                    }
                    else
                    {
                        var guessedInstalled = ExtractVersionFromMismatchMessage(detail);
                        if (guessedInstalled != null)
                        {
                            _loggingService.LogError($"Mount failed: WinFsp version mismatch. Installed: ~{guessedInstalled.Major}.{guessedInstalled.Minor}, Required: {RequiredWinFspVersion.Major}.{RequiredWinFspVersion.Minor}.");
                            ShowWinFspVersionMismatchFailedDialog(guessedInstalled, RequiredWinFspVersion);
                        }
                        else
                        {
                            _loggingService.LogError($"Mount failed: WinFsp version mismatch. Please update WinFsp to version {RequiredWinFspVersion.Major}.{RequiredWinFspVersion.Minor} or later.");
                            ShowWinFspMountFailedUpdateDialog($"WinFsp version mismatch. Please update to version {RequiredWinFspVersion.Major}.{RequiredWinFspVersion.Minor} or later.");
                        }
                    }
                }
                else
                {
                    ShowWinFspDriverErrorDialog(detail);
                }

                if (!IsVersionMismatchError(ex))
                {
                    ErrorLoggerStatic.ReportSilentException(ex, $"MountService.AttemptMountLifecycleAsync: Error mounting '{archivePath}' to '{mountPoint}'", true);
                }
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
            if (Interlocked.CompareExchange(ref _currentHost, null, host) == host)
            {
                try
                {
                    host.Unmount();
                }
                catch (Exception unmountEx)
                {
                    ErrorLoggerStatic.ReportSilentException(unmountEx, "host.Unmount() failed");
                    DiagnosticLogger.Log(unmountEx, "host.Unmount() failed (non-fatal)");
                }

                host.Dispose();
            }

            return true;
        }
        catch (Exception ex) when (ex.Message.Contains("drive", StringComparison.OrdinalIgnoreCase) ||
                                   ex.Message.Contains("mount", StringComparison.OrdinalIgnoreCase))
        {
            DiagnosticLogger.Log(ex, "Mount error (drive/mount)");
            _loggingService.LogError($"Mount error: {ex.Message}");
            ErrorLoggerStatic.ReportSilentException(ex, $"MountService.AttemptMountLifecycleAsync: Drive/mount error for '{archivePath}' to '{mountPoint}'", true);
            CurrentArchivePath = null;
            return false;
        }
        catch (Exception ex)
        {
            DiagnosticLogger.Log(ex, "Mount error (general)");
            _loggingService.LogError($"Mount error: {ex.Message}");
            ErrorLoggerStatic.LogErrorSync(ex, $"MountService.AttemptMountLifecycleAsync: Error mounting archive '{archivePath}' to '{mountPoint}'");
            CurrentArchivePath = null;
            return false;
        }
    }

    private static bool IsDriveLetterMountPoint(string mountPoint)
    {
        if (mountPoint.Length < 2) return false;
        if (!char.IsLetter(mountPoint[0])) return false;

        return mountPoint[1] == ':';
    }

    private static bool IsRunningAsAdministrator()
    {
        using var identity = WindowsIdentity.GetCurrent();
        var principal = new WindowsPrincipal(identity);
        return principal.IsInRole(WindowsBuiltInRole.Administrator);
    }

    private string GetCrossIntegrityMountPath(string archivePath)
    {
        var configuredFolder = _settingsService.Settings.CrossIntegrityMountFolder;
        var baseDir = string.IsNullOrWhiteSpace(configuredFolder)
            ? Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "SimpleZipDrive", "Mounts")
            : configuredFolder;
        var folderName = Path.GetFileNameWithoutExtension(archivePath);
        return Path.Combine(baseDir, ZipFsHelpers.SanitizeFolderName(folderName));
    }

    private static byte[] CreateCrossIntegritySecurityDescriptor()
    {
        const string sddl = "D:P(A;;FA;;;WD)";
        var sd = new RawSecurityDescriptor(sddl);
        var bytes = new byte[sd.BinaryLength];
        sd.GetBinaryForm(bytes, 0);
        return bytes;
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
