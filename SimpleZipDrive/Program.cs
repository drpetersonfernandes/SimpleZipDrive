using DokanNet;
using DokanNet.Logging;

namespace SimpleZipDrive;

file static class Program
{
    public static async Task Main(string[] args)
    {
        Console.WriteLine("ZIP Drive using DokanNet (Streaming Access with In-Memory Entry Cache)");
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
                mountPointArg = args[1];
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

        ILogger logger = new ConsoleLogger("[DokanNet] ");
        var mountLifecycleCompleted = false;

        using (var dokan = new Dokan(logger))
        {
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
            await ErrorLogger.LogErrorAsync(ex, context); // Use async logger for top-level errors in Program.cs
            // Specific user guidance for common Dokan mount errors
            if (!ex.Message.Contains("AssignDriveLetter", StringComparison.OrdinalIgnoreCase) &&
                !ex.Message.Contains("MountPoint", StringComparison.OrdinalIgnoreCase) &&
                (!ex.Message.Contains("CreateFile", StringComparison.OrdinalIgnoreCase) ||
                 !ex.Message.Contains("returned NTSTATUS C000003A", StringComparison.OrdinalIgnoreCase))) return false;

            Console.WriteLine("This Dokan error might be due to:");
            Console.WriteLine($"  - The mount point '{mountPoint}' already being in use.");
            Console.WriteLine("  - Insufficient privileges (try running as Administrator).");
            Console.WriteLine("  - If mounting to a folder, the specified folder path might not exist.");
            Console.WriteLine("  - Dokan driver not installed or not running correctly.");
            return false;
        }
        catch (Exception ex) // Catches other exceptions during mount setup, including those from ZipFs constructor
        {
            var context = $"Unexpected error during setup or operation for '{mountPoint}'. ZIP: '{zipFilePath}'";
            // If the exception came from ZipFs constructor, it would have already been logged by ZipFs via LogErrorSync.
            // However, logging it again here with LogErrorAsync ensures it's also sent to API if that part failed in sync.
            // This also catches errors like FileStream creation failure before ZipFs is even instantiated.
            await ErrorLogger.LogErrorAsync(ex, context);
            return false;
        }
        finally
        {
            if (cancelKeyPressHandler != null)
            {
                Console.CancelKeyPress -= cancelKeyPressHandler;
            }

            unmountBlocker.Dispose();
            Console.WriteLine($"Finished mount/unmount attempt for '{mountPoint}'.");
        }
    }
}
