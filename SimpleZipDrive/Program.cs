using DokanNet;
using DokanNet.Logging;
using System.Security.Principal;

namespace SimpleZipDrive;

file static class Program
{
    public static async Task Main(string[] args)
    {
        Console.WriteLine("ZIP Drive using DokanNet (Streaming Access with In-Memory Entry Cache)");

        await UpdateChecker.CheckForUpdateAsync();

        _ = typeof(ErrorLogger); // Ensures static constructor of ErrorLogger runs

        string? zipFilePath;
        string? mountPointArg = null;
        var isDragAndDrop = false;

        switch (args.Length)
        {
            case 1 when
                !string.IsNullOrWhiteSpace(args[0]) &&
                File.Exists(args[0]) &&
                Path.GetExtension(args[0]).Equals(".zip", StringComparison.OrdinalIgnoreCase):
                zipFilePath = args[0];
                isDragAndDrop = true;
                Console.WriteLine($"Drag-and-drop mode: Detected ZIP file '{zipFilePath}'.");
                break;
            case >= 2:
                zipFilePath = args[0];
                // Sanitize mount point argument to remove potential quotes from user input
                mountPointArg = args[1].Trim().Trim('"');
                Console.WriteLine($"Standard mode: ZIP file '{zipFilePath}', Mount point arg '{mountPointArg}'.");
                break;
            default:
                PrintUsage();
                KeepConsoleOpenOnErrorIfLaunchedByDoubleClick(args);
                return;
        }

        if (zipFilePath == null || !File.Exists(zipFilePath))
        {
            var errorMsg = $"Error: ZIP file not found at '{zipFilePath ?? " unspecified"}'.";
            Console.WriteLine(errorMsg);
            KeepConsoleOpenOnError();
            return;
        }

        if (!IsAdministrator())
        {
            Console.WriteLine("\nWarning: Running without Administrator privileges.");
            Console.WriteLine("Mounting to drive letters or certain paths may require elevated permissions.");
            Console.WriteLine("If mounting fails, please try running SimpleZipDrive.exe as Administrator.");
        }

        ILogger logger = new ConsoleLogger("[DokanNet] ");
        var mountLifecycleCompleted = false;

        try
        {
            using var dokan = new Dokan(logger);
            Console.WriteLine($"Dokan Library Version: {dokan.Version}");
            Console.WriteLine($"Dokan Driver Version: {dokan.DriverVersion}");

            if (isDragAndDrop)
            {
                char[] preferredDriveLetters = { 'M', 'N', 'O', 'P', 'Q' };
                foreach (var letter in preferredDriveLetters)
                {
                    var currentMountPoint = letter + @":\";
                    Console.WriteLine($"Drag-and-drop: Attempting to mount on '{currentMountPoint}'...");
                    if (!await AttemptMountLifecycle(zipFilePath, currentMountPoint, logger, dokan)) continue;

                    mountLifecycleCompleted = true;
                    break;
                }

                if (!mountLifecycleCompleted)
                {
                    Console.WriteLine($"Error: Failed to auto-mount '{zipFilePath}' on any of the preferred drive letters (M, N, O, P, Q).");
                    Console.WriteLine("Please check error messages above for details (e.g., drive in use, permissions, Dokan driver issues).");
                    KeepConsoleOpenOnError();
                }
            }
            else
            {
                if (string.IsNullOrWhiteSpace(mountPointArg))
                {
                    Console.WriteLine("Error: Mount point argument cannot be empty in standard mode.");
                    PrintUsage();
                    KeepConsoleOpenOnError();
                    return;
                }

                var mountPoint = mountPointArg;
                if (mountPointArg.Length == 1 && char.IsLetter(mountPointArg[0]))
                {
                    mountPoint = mountPointArg.ToUpperInvariant() + @":\";
                }

                if (await AttemptMountLifecycle(zipFilePath, mountPoint, logger, dokan))
                {
                    mountLifecycleCompleted = true;
                }
                else
                {
                    Console.WriteLine($"Error: Failed to mount '{zipFilePath}' on '{mountPoint}'. Review messages above.");
                    KeepConsoleOpenOnError();
                }
            }
        }
        catch (Exception ex) when (ex is DokanException || (ex is DllNotFoundException dnfEx && dnfEx.Message.Contains("dokan", StringComparison.OrdinalIgnoreCase)))
        {
            // const string context = "Dokan library initialization failed. This is a strong indicator that Dokan is not installed.";
            // _ = ErrorLogger.LogErrorAsync(ex, context);

            Console.WriteLine("\n--- DOKAN INITIALIZATION FAILED ---");
            Console.WriteLine("Could not initialize the Dokan file system library.");
            Console.WriteLine("This usually means the Dokan driver is not installed on your system.");
            Console.WriteLine("\nPlease download and install the latest Dokan driver from:");
            Console.WriteLine("https://github.com/dokan-dev/dokany/releases");
            Console.WriteLine("\nAfter installation, please try running this application again.");
            KeepConsoleOpenOnError();
        }

        if (mountLifecycleCompleted) Console.WriteLine("Mount operation concluded. Application will now exit.");
        else Console.WriteLine("No mount operation was successfully initiated or an early error prevented it.");

        Console.WriteLine("Application fully exited.");
    }

    private static void PrintUsage()
    {
        Console.WriteLine("\nUsage 1 (Explicit Mount): SimpleZipDrive.exe <PathToZipFile> <MountPoint>");
        Console.WriteLine("""  Example: SimpleZipDrive.exe "C:\path\to\archive.zip" M""");
        Console.WriteLine(@"  MountPoint can be a drive letter (e.g., M) or a path to an existing empty folder (e.g., C:\mount\zip)");
        Console.WriteLine("\nUsage 2 (Drag-and-Drop): Drag a .zip file onto the SimpleZipDrive.exe icon.");
        Console.WriteLine(@"  It will attempt to mount on M:\, then N:\, O:\, P:\, Q:\ automatically.");
    }

    private static void KeepConsoleOpenOnError()
    {
        Console.WriteLine("\n--- Operation failed or concluded with errors ---");
        Console.WriteLine("Press any key to exit.");
        if (!Console.IsInputRedirected) Console.ReadKey();
    }

    private static void KeepConsoleOpenOnErrorIfLaunchedByDoubleClick(string[] args)
    {
        if (args.Length == 0 && !Console.IsInputRedirected) KeepConsoleOpenOnError();
    }

    private static async Task<bool> AttemptMountLifecycle(string zipFilePath, string mountPoint, ILogger logger, Dokan dokan)
    {
        ManualResetEvent unmountBlocker = new(false);
        ConsoleCancelEventHandler? cancelKeyPressHandler = null;

        try
        {
            // Check if the mount point is a directory (not a drive root) and create it if it doesn't exist.
            // A drive root like "C:\" will have GetPathRoot(path) == path. A folder like "C:\mount" will not.
            try
            {
                if (Path.IsPathFullyQualified(mountPoint) && Path.GetPathRoot(mountPoint) != mountPoint)
                {
                    if (!Directory.Exists(mountPoint))
                    {
                        Console.WriteLine($"Mount point folder '{mountPoint}' does not exist. Attempting to create it...");
                        Directory.CreateDirectory(mountPoint);
                        Console.WriteLine($"Successfully created directory '{mountPoint}'.");
                    }
                }
            }
            catch (Exception ex) when (ex is UnauthorizedAccessException or IOException or ArgumentException)
            {
                var context = $"Failed to create mount point directory '{mountPoint}'.";
                _ = ErrorLogger.LogErrorAsync(ex, context);
                Console.WriteLine($"Error: Could not create the mount point directory '{mountPoint}'.");
                Console.WriteLine($"Reason: {ex.Message}");
                Console.WriteLine("Please ensure the path is valid and you have sufficient permissions, or create it manually.");
                return false; // Abort the mount attempt
            }

            var fileInfoSys = new FileInfo(zipFilePath);
            var fileSize = fileInfoSys.Length;
            Console.WriteLine($"Processing ZIP file: '{zipFilePath}', Size: {fileSize / 1024.0 / 1024.0:F2} MB for mount on '{mountPoint}'");

            Console.WriteLine($"Opening ZIP file '{zipFilePath}' for streaming access...");
            // The stream is created here and its lifetime is managed by this 'await using'
            await using Stream zipFileSourceStream = new FileStream(zipFilePath, FileMode.Open, System.IO.FileAccess.Read, FileShare.Read);
            Console.WriteLine("ZIP file opened for streaming.");

            // Pass ErrorLogger.LogErrorSync to ZipFs constructor
            using var zipFs = new ZipFs(zipFileSourceStream, mountPoint, ErrorLogger.LogErrorSync);
            Console.WriteLine($"Attempting to mount on '{mountPoint}'...");

            cancelKeyPressHandler = (sender, e) =>
            {
                e.Cancel = true;
                Console.WriteLine($"Ctrl+C detected for mount '{mountPoint}'. Signaling unmount...");
                try
                {
                    // ReSharper disable once AccessToDisposedClosure
                    unmountBlocker.Set();
                }
                catch (ObjectDisposedException)
                {
                    Console.WriteLine($"Ctrl+C for '{mountPoint}': unmountBlocker.Set() skipped as it was already disposed.");
                }
            };
            Console.CancelKeyPress += cancelKeyPressHandler;

            var dokanInstanceBuilder = new DokanInstanceBuilder(dokan)
                .ConfigureOptions(options =>
                {
                    options.Options = DokanOptions.RemovableDrive;
                    options.MountPoint = mountPoint;
                });

            using (dokanInstanceBuilder.Build(zipFs))
            {
                Console.WriteLine($"Successfully mounted on '{mountPoint}'. Virtual drive should be available.");
                Console.WriteLine("Press Ctrl+C in this window to unmount this instance and exit if it's the active mount.");
                unmountBlocker.WaitOne();
                Console.WriteLine($"Unmount signal received for '{mountPoint}'. Proceeding to shutdown this instance.");
            }

            Console.WriteLine($"Dokan instance for '{mountPoint}' shut down successfully.");
            return true;
        }
        catch (DokanException ex) // Catches Dokan-specific errors
        {
            var context = $"DokanException trying to mount on '{mountPoint}'. ZIP: '{zipFilePath}'";
            _ = ErrorLogger.LogErrorAsync(ex, context);

            // Specific user guidance for common Dokan mount errors
            var isCommonMountError = ex.Message.Contains("AssignDriveLetter", StringComparison.OrdinalIgnoreCase) ||
                                     ex.Message.Contains("MountPoint", StringComparison.OrdinalIgnoreCase) ||
                                     (ex.Message.Contains("CreateFile", StringComparison.OrdinalIgnoreCase) &&
                                      ex.Message.Contains("returned NTSTATUS C000003A", StringComparison.OrdinalIgnoreCase));

            if (isCommonMountError)
            {
                Console.WriteLine("This Dokan error might be due to:");
                Console.WriteLine($"  - The mount point '{mountPoint}' already being in use.");
                Console.WriteLine("  - Insufficient privileges (try running as Administrator).");
                Console.WriteLine("  - If mounting to a folder, the application will attempt to create it if it doesn't exist. This can fail due to insufficient permissions or an invalid path.");
                Console.WriteLine("  - Dokan driver not installed or not running correctly. You can get it from:");
                Console.WriteLine("    https://github.com/dokan-dev/dokany/releases");
            }
            else if (ex.Message.Contains("Can't install the Dokan driver", StringComparison.OrdinalIgnoreCase))
            {
                Console.WriteLine("\n--- DOKAN DRIVER INSTALLATION FAILED ---");
                Console.WriteLine("The application failed to communicate with or install the Dokan kernel driver.");
                Console.WriteLine("This can be caused by:");
                Console.WriteLine("  - Insufficient privileges (the most common cause).");
                Console.WriteLine("  - A problem with the Dokan installation on your system.");
                Console.WriteLine("  - Antivirus software interfering with the driver installation.");
                Console.WriteLine("\nPlease try running this application as an Administrator.");
                Console.WriteLine("If the problem persists, reinstalling Dokan is recommended:");
                Console.WriteLine("  https://github.com/dokan-dev/dokany/releases");
            }
            else if (ex.Message.Contains("Something's wrong with the Dokan driver", StringComparison.OrdinalIgnoreCase))
            {
                Console.WriteLine("A Dokan driver error occurred. This can sometimes be resolved by:");
                Console.WriteLine("  - Restarting your computer.");
                Console.WriteLine("  - Reinstalling the Dokan driver. You can get the latest version from:");
                Console.WriteLine("    https://github.com/dokan-dev/dokany/releases");
            }
            else
            {
                Console.WriteLine("A generic Dokan-related error occurred during mount. See logs for details.");
            }

            return false;
        }
        catch (Exception ex) // Catches other exceptions during mount setup, including those from ZipFs constructor
        {
            var context = $"Unexpected error during setup or operation for '{mountPoint}'. ZIP: '{zipFilePath}'";
            // If the exception came from ZipFs constructor, it would have already been logged by ZipFs via LogErrorSync.
            // However, logging it again here with LogErrorAsync ensures it's also sent to API if that part failed in sync.
            // This also catches errors like FileStream creation failure before ZipFs is even instantiated.
            _ = ErrorLogger.LogErrorAsync(ex, context);

            // --- Specific message for ZipException ---
            // The Zip file is corrupted or invalid.
            if (ex is ICSharpCode.SharpZipLib.Zip.ZipException zipEx &&
                zipEx.Message.Contains("Cannot find central directory", StringComparison.OrdinalIgnoreCase))
            {
                Console.WriteLine("\n--- ZIP FILE ERROR ---");
                Console.WriteLine($"Error: The ZIP file '{zipFilePath}' appears to be corrupted or invalid.");
                Console.WriteLine("Reason: The application could not find the central directory within the ZIP archive.");
                Console.WriteLine("Please ensure the ZIP file is complete and not corrupted.");
            }
            else
            {
                Console.WriteLine($"An unexpected error occurred during mount setup: {ex.Message}");
                Console.WriteLine("Please check the 'error.log' file for more details.");
            }

            return false;
        }
        finally
        {
            if (cancelKeyPressHandler != null)
            {
                Console.CancelKeyPress -= cancelKeyPressHandler;
            }

            // Clean up the dedicated temporary directory used by this application instance.
            var tempDirectoryPath = Path.Combine(Path.GetTempPath(), "SimpleZipDrive");
            try
            {
                if (Directory.Exists(tempDirectoryPath))
                {
                    Directory.Delete(tempDirectoryPath, true);
                    Console.WriteLine($"Successfully deleted temporary directory: '{tempDirectoryPath}'.");
                }
            }
            catch (DirectoryNotFoundException)
            {
                // Harmless race condition: another process or instance deleted it between our check and our delete call.
                // The directory is gone, which was the goal, so we can ignore this.
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Warning: Failed to delete temporary directory '{tempDirectoryPath}'. This may be due to locked files or permissions.");
                _ = ErrorLogger.LogErrorAsync(ex, $"Failed to delete temp directory '{tempDirectoryPath}'.");
            }

            unmountBlocker.Dispose();
            Console.WriteLine($"Finished mount/unmount attempt for '{mountPoint}'.");
        }
    }

    private static bool IsAdministrator()
    {
        using var identity = WindowsIdentity.GetCurrent();
        var principal = new WindowsPrincipal(identity);
        return principal.IsInRole(WindowsBuiltInRole.Administrator);
    }
}