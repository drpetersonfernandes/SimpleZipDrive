using System.Security.AccessControl;
using System.Security.Principal;
using System.Text.RegularExpressions;
using DokanNet;
using SharpCompress.Archives;
using SharpCompress.Archives.Rar;
using SharpCompress.Archives.SevenZip;
using SharpCompress.Archives.Zip;
using SharpCompress.Common;
using SharpCompress.Readers;
using FileAccess = DokanNet.FileAccess;

namespace SimpleZipDrive;

public class ZipFs : IDokanOperations, IDisposable
{
    /// <summary>
    /// The source archive stream. This stream is owned by the caller and is NOT disposed by this instance.
    /// </summary>
    private readonly Stream _sourceArchiveStream;

    private readonly IArchive _archive;
    private readonly Dictionary<string, IArchiveEntry> _archiveEntries = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, DateTime> _directoryCreationTimes = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, DateTime> _directoryLastWriteTimes = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, DateTime> _directoryLastAccessTimes = new(StringComparer.OrdinalIgnoreCase);

    private readonly Action<Exception?, string?> _logErrorAction;
    private readonly object _archiveLock = new();

    // Cache for large files extracted to disk. Key: normalized path in archive, Value: path to the temp file.
    private readonly Dictionary<string, string?> _largeFileCache = new(StringComparer.OrdinalIgnoreCase);
    private const int MaxMemorySize = 536870912; // 512 MB (512x1024x1024)

    // Memory throttling for small files to prevent unbounded resource consumption
    private const long MaxTotalMemoryCache = 1073741824; // 1 GB total limit for all small files (1x1024x1024x1024)
    private long _currentMemoryUsage;
    private readonly object _memoryLock = new();

    private readonly string _tempDirectoryPath;
    private readonly Func<string?> _passwordProvider;
    private readonly string _archiveType;

    private const string VolumeLabel = "SimpleZipDrive";
    private static readonly char[] Separator = ['/'];

    // Windows path length limits
    private const int MaxPath = 260;
    private const int MaxPathExtended = 32767;
    private const string ExtendedPathPrefix = @"\\?\";

    // Executable file extensions that require extraction to temp for execution
    private static readonly HashSet<string> ExecutableExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".exe", ".dll", ".sys", ".drv", ".com", ".bat", ".cmd",
        ".msi", ".msp", ".mst", ".ps1", ".vbs", ".js", ".wsf",
        ".jar", ".py", ".rb", ".pl", ".sh"
    };

    // Cache for extracted executables. Key: normalized path in archive, Value: path to the extracted temp file.
    private readonly Dictionary<string, string> _executableCache = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _executableLock = new();
    private readonly string _executableTempDirectory;

    /// <summary>
    /// Initializes a new instance of the <see cref="ZipFs"/> class.
    /// </summary>
    /// <param name="archiveStream">The stream containing the archive data. The caller retains ownership of this stream and is responsible for disposing it.</param>
    /// <param name="mountPoint">The mount point for the virtual drive (used for logging purposes).</param>
    /// <param name="logErrorAction">Action to invoke when logging errors.</param>
    /// <param name="passwordProvider">Function that provides the password for encrypted archives.</param>
    /// <param name="archiveType">The type of archive ("zip", "7z", or "rar").</param>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="logErrorAction"/> is null.</exception>
    /// <exception cref="NotSupportedException">Thrown when the archive type is not supported.</exception>
    /// <remarks>
    /// <para><strong>Important:</strong> The <paramref name="archiveStream"/> is stored by reference but NOT disposed by this instance.
    /// The caller must ensure the stream remains open during the entire lifetime of this instance and dispose it after
    /// this <see cref="ZipFs"/> instance has been disposed.</para>
    /// </remarks>
    public ZipFs(Stream archiveStream, string mountPoint, Action<Exception?, string?> logErrorAction, Func<string?> passwordProvider, string archiveType)
    {
        _sourceArchiveStream = archiveStream;
        _logErrorAction = logErrorAction ?? throw new ArgumentNullException(nameof(logErrorAction));
        _passwordProvider = passwordProvider;
        _archiveType = archiveType.ToLowerInvariant();
        // Use a unique temp directory per instance to avoid collisions between multiple running instances
        _tempDirectoryPath = Path.Combine(Path.GetTempPath(), "SimpleZipDrive", $"{Environment.ProcessId}_{Guid.NewGuid():N}");

        try
        {
            // Ensure the dedicated temporary directory exists for this session.
            Directory.CreateDirectory(_tempDirectoryPath);

            // Create a subdirectory specifically for extracted executables
            _executableTempDirectory = Path.Combine(_tempDirectoryPath, "Executables");
            Directory.CreateDirectory(_executableTempDirectory);

            // Reset stream position to ensure archive parsing starts from the beginning
            if (archiveStream.CanSeek)
            {
                archiveStream.Position = 0;
            }

            _archive = OpenArchive(archiveStream);
            InitializeEntries();
            Console.WriteLine($"ZipFs Constructor: Using SharpCompress. Archive type: {_archiveType}, _sourceArchiveStream.CanSeek = {_sourceArchiveStream.CanSeek}, _sourceArchiveStream type = {_sourceArchiveStream.GetType().FullName}");
        }
        catch (Exception ex)
        {
            _logErrorAction(ex, $"Error during ZipFs construction for mount point '{mountPoint}'.");
            throw;
        }
    }

    private IArchive OpenArchive(Stream stream)
    {
        // First, try to open the archive without a password
        try
        {
            var archiveWithoutPassword = _archiveType switch
            {
                "zip" => ZipArchive.OpenArchive(stream, new ReaderOptions()),
                "7z" => SevenZipArchive.OpenArchive(stream, new ReaderOptions()),
                "rar" => RarArchive.OpenArchive(stream, new ReaderOptions()),
                _ => throw new NotSupportedException($"Archive type '{_archiveType}' is not supported.")
            };

            // Check if any entry is encrypted - if so, we need a password
            var hasEncryptedEntries = archiveWithoutPassword.Entries.Any(static e => e.IsEncrypted);
            if (!hasEncryptedEntries)
            {
                return archiveWithoutPassword;
            }

            // Archive has encrypted entries, dispose and reopen with password
            archiveWithoutPassword.Dispose();
        }
        catch (Exception ex) when (IsPasswordRequiredException(ex))
        {
            // Password is required, will prompt below
        }

        // Reset stream position for retry with password
        if (stream.CanSeek)
        {
            stream.Position = 0;
        }

        // Prompt for password and retry
        var password = _passwordProvider();

        return _archiveType switch
        {
            "zip" => ZipArchive.OpenArchive(stream, new ReaderOptions { Password = password }),
            "7z" => SevenZipArchive.OpenArchive(stream, new ReaderOptions { Password = password }),
            "rar" => RarArchive.OpenArchive(stream, new ReaderOptions { Password = password }),
            _ => throw new NotSupportedException($"Archive type '{_archiveType}' is not supported.")
        };
    }

    private static bool IsPasswordRequiredException(Exception ex)
    {
        var message = ex.Message.ToLowerInvariant();
        return message.Contains("password") ||
               message.Contains("encrypted") ||
               (message.Contains("rar") && message.Contains("header"));
    }

    private void InitializeEntries()
    {
        try
        {
            foreach (var entry in _archive.Entries)
            {
                if (string.IsNullOrEmpty(entry.Key))
                {
                    _logErrorAction?.Invoke(null, "Skipping invalid archive entry with null/empty name.");
                    continue;
                }

                var normalizedPath = NormalizePath(entry.Key);
                _archiveEntries[normalizedPath] = entry;

                var currentPath = "";
                var parts = normalizedPath.Split(Separator, StringSplitOptions.RemoveEmptyEntries);
                for (var i = 0; i < parts.Length - (IsDirectory(entry) ? 0 : 1); i++)
                {
                    currentPath += "/" + parts[i];
                    if (_directoryCreationTimes.ContainsKey(currentPath)) continue;

                    var entryTime = entry.LastModifiedTime ?? entry.CreatedTime ?? DateTime.Now;
                    _directoryCreationTimes[currentPath] = entryTime;
                    _directoryLastWriteTimes[currentPath] = entryTime;
                    _directoryLastAccessTimes[currentPath] = DateTime.Now;
                }
            }

            if (_directoryCreationTimes.ContainsKey("/")) return;

            var now = DateTime.Now;
            _directoryCreationTimes["/"] = now;
            _directoryLastWriteTimes["/"] = now;
            _directoryLastAccessTimes["/"] = now;
        }
        catch (Exception ex)
        {
            _logErrorAction?.Invoke(ex, "Error during ZipFs.InitializeEntries.");
        }
    }

    private static bool IsDirectory(IArchiveEntry entry)
    {
        // Check if the entry key ends with a path separator indicating it's a directory
        return entry.Key != null && (entry.Key.EndsWith('/') || entry.Key.EndsWith('\\'));
    }

    /// <summary>
    /// Determines if the specified file is an executable based on its extension.
    /// </summary>
    private static bool IsExecutableFile(string filePath)
    {
        var extension = Path.GetExtension(filePath);
        return !string.IsNullOrEmpty(extension) && ExecutableExtensions.Contains(extension);
    }

    /// <summary>
    /// Checks if file access includes execute intent by examining access flags.
    /// </summary>
    private static bool HasExecuteIntent(FileAccess access)
    {
        // Check for execute-specific access rights
        // FileAccess.Execute = 0x20 (32)
        // FileAccess.ReadData = 0x1 (1) - combined with other flags often indicates execution intent
        return (access & FileAccess.Execute) == FileAccess.Execute ||
               access == FileAccess.ReadData ||
               access == (FileAccess.ReadData | FileAccess.Synchronize) ||
               access == (FileAccess.ReadData | FileAccess.ReadAttributes) ||
               access == (FileAccess.ReadData | FileAccess.ReadAttributes | FileAccess.Synchronize);
    }

    /// <summary>
    /// Extracts an executable file to the temp directory for execution.
    /// Returns the path to the extracted file.
    /// </summary>
    private string ExtractExecutableForExecution(IArchiveEntry entry, string normalizedPath)
    {
        lock (_executableLock)
        {
            // Check if already extracted
            if (_executableCache.TryGetValue(normalizedPath, out var cachedPath) && File.Exists(cachedPath))
            {
                Console.WriteLine($"Executable cache hit: '{normalizedPath}' -> '{cachedPath}'");
                return cachedPath;
            }

            // Generate a unique filename that preserves the original name for compatibility
            var originalFileName = Path.GetFileName(entry.Key) ?? "extracted.exe";
            var safeFileName = $"{Guid.NewGuid():N}_{originalFileName}";
            var extractPath = Path.Combine(_executableTempDirectory, safeFileName);

            Console.WriteLine($"Extracting executable: '{normalizedPath}' ({entry.Size / 1024.0:F2} KB) -> '{extractPath}'");

            try
            {
                // Extract the file
                using (var entryStream = entry.OpenEntryStream())
                using (var fileStream = new FileStream(extractPath, FileMode.Create, System.IO.FileAccess.Write, FileShare.None))
                {
                    entryStream.CopyTo(fileStream);
                }

                // Preserve the original timestamps
                try
                {
                    File.SetCreationTime(extractPath, entry.CreatedTime ?? DateTime.Now);
                    File.SetLastWriteTime(extractPath, entry.LastModifiedTime ?? DateTime.Now);
                    File.SetLastAccessTime(extractPath, DateTime.Now);
                }
                catch
                {
                    // Timestamps are not critical for execution
                }

                // Add to cache
                _executableCache[normalizedPath] = extractPath;
                Console.WriteLine($"Executable extraction complete: '{extractPath}'");

                return extractPath;
            }
            catch (Exception ex)
            {
                _logErrorAction(ex, $"Failed to extract executable '{normalizedPath}' to '{extractPath}'");
                // Clean up partial file if it exists
                try
                {
                    if (File.Exists(extractPath))
                    {
                        File.Delete(extractPath);
                    }
                }
                catch
                {
                    // Ignore cleanup errors
                }

                throw;
            }
        }
    }

    private static string NormalizePath(string path)
    {
        if (string.IsNullOrEmpty(path)) return "/";

        path = path.Replace('\\', '/');
        if (!path.StartsWith('/'))
        {
            path = "/" + path;
        }

        return path;
    }

    /// <summary>
    /// Validates that the path length is within acceptable limits.
    /// Returns true if the path is valid, false if it exceeds the maximum allowed length.
    /// </summary>
    private static bool IsPathLengthValid(string path)
    {
        if (string.IsNullOrEmpty(path))
            return true;

        // Check for extended-length path prefix
        if (path.StartsWith(ExtendedPathPrefix, StringComparison.Ordinal))
        {
            // Extended paths can be up to 32,767 characters
            return path.Length <= MaxPathExtended;
        }

        // Standard paths are limited to MAX_PATH (260) characters
        return path.Length <= MaxPath;
    }

    /// <summary>
    /// Validates path length and logs an error if it exceeds limits.
    /// Returns NtStatus.Success if valid, or an error status if invalid.
    /// </summary>
    private NtStatus ValidatePathLength(string path, string operationName)
    {
        if (!IsPathLengthValid(path))
        {
            var isExtended = path.StartsWith(ExtendedPathPrefix, StringComparison.Ordinal);
            var maxLength = isExtended ? MaxPathExtended : MaxPath;
            var pathType = isExtended ? "extended-length" : "standard";

            _logErrorAction(
                new PathTooLongException($"Path exceeds maximum length for {pathType} paths ({maxLength} characters)."),
                $"ZipFs.{operationName}: Path length validation failed - {path.Length} characters.");

            return DokanResult.Error;
        }

        return DokanResult.Success;
    }

    public NtStatus CreateFile(
        string fileName,
        FileAccess access,
        FileShare share,
        FileMode mode,
        FileOptions options,
        FileAttributes attributes,
        IDokanFileInfo info)
    {
        // Validate path length before processing
        var pathValidationResult = ValidatePathLength(fileName, nameof(CreateFile));
        if (pathValidationResult != DokanResult.Success)
            return pathValidationResult;

        var normalizedPath = NormalizePath(fileName);

        IArchiveEntry? entry;
        lock (_archiveLock)
        {
            _archiveEntries.TryGetValue(normalizedPath, out entry);
        }

        if (entry != null)
        {
            if (IsDirectory(entry))
            {
                info.IsDirectory = true;

                // Prevent file-like read/write access to a directory handle.
                if ((access & (FileAccess.ReadData | FileAccess.WriteData | FileAccess.AppendData)) != 0)
                {
                    return DokanResult.AccessDenied;
                }

                return mode switch
                {
                    FileMode.Open or FileMode.OpenOrCreate or FileMode.Create => DokanResult.Success,
                    FileMode.CreateNew => DokanResult.FileExists,
                    _ => DokanResult.AccessDenied
                };
            }
            else // It's a file
            {
                info.IsDirectory = false;

                // Check file mode before proceeding.
                switch (mode)
                {
                    case FileMode.Open:
                    case FileMode.OpenOrCreate: // On a read-only FS, OpenOrCreate on an existing file is just Open.
                    case FileMode.Create: // Same for Create.
                        break; // Proceed to create the stream context.
                    case FileMode.CreateNew:
                        return DokanResult.FileExists; // The file always exists in the archive.
                    default: // Truncate, Append, etc., are not supported.
                        return DokanResult.AccessDenied;
                }

                try
                {
                    var entrySize = entry.Size;

                    // --- EXECUTABLE HANDLING ---
                    // Check if this is an executable file being accessed for execution
                    if (IsExecutableFile(normalizedPath) && HasExecuteIntent(access))
                    {
                        Console.WriteLine($"Executable access detected: '{normalizedPath}' with access={access}, share={share}");

                        try
                        {
                            var extractedPath = ExtractExecutableForExecution(entry, normalizedPath);

                            // Open the extracted file for reading with the requested sharing mode
                            // Use FileShare.Read | FileShare.Write to allow the loader to memory-map the file
                            info.Context = new FileStream(
                                extractedPath,
                                FileMode.Open,
                                System.IO.FileAccess.Read,
                                FileShare.Read | FileShare.Write | FileShare.Delete);

                            Console.WriteLine($"Executable redirected to temp file: '{extractedPath}'");
                            return DokanResult.Success;
                        }
                        catch (Exception ex)
                        {
                            _logErrorAction(ex, $"Failed to extract and open executable '{normalizedPath}'");
                            info.Context = null;
                            return DokanResult.Error;
                        }
                    }

                    // Hybrid caching - memory for small files, temp disk file for large files
                    if (entrySize is >= MaxMemorySize or < 0)
                    {
                        // --- Large file: Cache to disk ---
                        string? cachedPath;
                        lock (_archiveLock)
                        {
                            if (!_largeFileCache.TryGetValue(normalizedPath, out cachedPath))
                            {
                                Console.WriteLine($"Large file detected: '{normalizedPath}' ({entrySize / 1024.0 / 1024.0:F2} MB). Extracting to temporary disk cache...");
                                var newTempFilePath = Path.Combine(_tempDirectoryPath, Guid.NewGuid().ToString("N") + ".tmp");

                                // --- Disk space check ---
                                // Only check disk space if entry size is known (non-negative)
                                if (entrySize >= 0)
                                {
                                    try
                                    {
                                        var tempDrivePathRoot = Path.GetPathRoot(newTempFilePath) ?? "C:\\";
                                        var tempDrive = new DriveInfo(tempDrivePathRoot);
                                        if (tempDrive.AvailableFreeSpace < entrySize)
                                        {
                                            var errorMessage = $"Insufficient disk space to extract large file '{normalizedPath}' ({entrySize / 1024.0 / 1024.0:F2} MB).";
                                            _logErrorAction(new IOException(errorMessage), "ZipFs.CreateFile: Disk space check failed.");
                                            return DokanResult.DiskFull;
                                        }
                                    }
                                    catch (Exception driveEx)
                                    {
                                        _logErrorAction(driveEx, $"Error checking disk space for large file extraction of '{normalizedPath}'.");
                                    }
                                }

                                using var entryStream = entry.OpenEntryStream();
                                using var tempFileStream = new FileStream(newTempFilePath, FileMode.Create, System.IO.FileAccess.Write, FileShare.None);
                                entryStream.CopyTo(tempFileStream);

                                _largeFileCache[normalizedPath] = newTempFilePath;
                                cachedPath = newTempFilePath;
                                Console.WriteLine($"Extraction complete for '{normalizedPath}'. Temp file: '{newTempFilePath}'");
                            }
                            else
                            {
                                Console.WriteLine($"Reusing existing temporary cache for '{normalizedPath}'.");
                            }
                        }

                        // Open the temp file for reading and assign it as the context.
                        if (!string.IsNullOrEmpty(cachedPath))
                        {
                            try
                            {
                                info.Context = new FileStream(cachedPath, FileMode.Open, System.IO.FileAccess.Read, FileShare.Read);
                            }
                            catch (Exception fsEx)
                            {
                                _logErrorAction(fsEx, $"ZipFs.CreateFile: Failed to open cached temp file '{cachedPath}' for reading.");
                                info.Context = null;
                                return DokanResult.Error;
                            }
                        }
                        // If cachedPath is null/empty here, something went wrong in caching but wasn't caught
                    }
                    else
                    {
                        // --- Small file: Cache in memory with throttling ---
                        // Check if adding this file would exceed total memory limit
                        bool useDiskCache;

                        lock (_memoryLock)
                        {
                            var projectedMemoryUsage = _currentMemoryUsage + entrySize;
                            useDiskCache = projectedMemoryUsage > MaxTotalMemoryCache;
                        }

                        if (useDiskCache)
                        {
                            // --- Memory limit exceeded: Fall back to disk caching ---
                            Console.WriteLine($"Memory limit approaching. Using disk cache for small file '{normalizedPath}' ({entrySize / 1024.0 / 1024.0:F2} MB).");

                            string? cachedPath;
                            lock (_archiveLock)
                            {
                                if (!_largeFileCache.TryGetValue(normalizedPath, out cachedPath))
                                {
                                    var newTempFilePath = Path.Combine(_tempDirectoryPath, Guid.NewGuid().ToString("N") + ".tmp");

                                    using var entryStream = entry.OpenEntryStream();
                                    using var tempFileStream = new FileStream(newTempFilePath, FileMode.Create, System.IO.FileAccess.Write, FileShare.None);
                                    entryStream.CopyTo(tempFileStream);

                                    _largeFileCache[normalizedPath] = newTempFilePath;
                                    cachedPath = newTempFilePath;
                                }
                            }

                            if (!string.IsNullOrEmpty(cachedPath))
                            {
                                try
                                {
                                    info.Context = new FileStream(cachedPath, FileMode.Open, System.IO.FileAccess.Read, FileShare.Read);
                                }
                                catch (Exception fsEx)
                                {
                                    _logErrorAction(fsEx, $"ZipFs.CreateFile: Failed to open disk-cached temp file '{cachedPath}' for reading.");
                                    info.Context = null;
                                    return DokanResult.Error;
                                }
                            }
                        }
                        else
                        {
                            // --- Small file: Cache in memory ---
                            byte[] entryBytes;
                            lock (_archiveLock)
                            {
                                using var entryStream = entry.OpenEntryStream();
                                using var tempMs = new MemoryStream(entrySize > 0 ? (int)Math.Min(entrySize, int.MaxValue) : 4096);
                                entryStream.CopyTo(tempMs);
                                entryBytes = tempMs.ToArray();
                            }

                            // Track memory usage
                            lock (_memoryLock)
                            {
                                _currentMemoryUsage += entryBytes.Length;
                            }

                            // Wrap in a custom stream that tracks disposal for memory accounting
                            info.Context = new TrackedMemoryStream(entryBytes, this);
                        }
                    }

                    // Verify that context was successfully created
                    if (info.Context is Stream)
                    {
                        return DokanResult.Success;
                    }

                    // If we reach here, context creation failed silently
                    _logErrorAction(new InvalidOperationException($"ZipFs.CreateFile: Context was not a Stream for file '{normalizedPath}' after caching attempt."), "ZipFs.CreateFile: Context invalid post-caching.");
                    return DokanResult.Error;
                }
                catch (CryptographicException cryptoEx)
                {
                    var contextMessage = $"ZipFs.CreateFile: Password error for '{normalizedPath}'. The provided password may be incorrect or missing.";
                    Console.WriteLine($"\n[!] Password Error: Could not decrypt '{normalizedPath}'.");
                    _logErrorAction(cryptoEx, contextMessage);
                    info.Context = null;
                    return DokanResult.Error;
                }
                catch (IOException ioEx) when ((uint)ioEx.HResult == 0x80070015) // ERROR_NOT_READY
                {
                    var msg = $"CRITICAL ERROR: The source drive containing the archive file is no longer ready. " +
                              $"Please check the connection to drive '{Path.GetPathRoot(_tempDirectoryPath)}'.";
                    Console.WriteLine($"\n[!!!] {msg}");
                    // _logErrorAction(ioEx, "ZipFs.CreateFile: Source device disconnected.");
                    return DokanResult.NotReady;
                }
                catch (IOException ioEx) when ((uint)ioEx.HResult == 0x800703EE || (uint)ioEx.HResult == 0x80070037) // ERROR_FILE_INVALID or ERROR_DEV_NOT_EXIST
                {
                    Console.WriteLine("\n--- SOURCE FILE ACCESS ERROR ---");
                    Console.WriteLine("Error: The source archive file is no longer accessible.");
                    Console.WriteLine($"Details: {ioEx.Message}");
                    Console.WriteLine("\nThis usually means:");
                    Console.WriteLine("  - The external drive/USB device was disconnected");
                    Console.WriteLine("  - The archive file was modified or deleted after mounting started");
                    Console.WriteLine("  - The source device is no longer available or has errors");
                    Console.WriteLine("\nPlease verify the drive is connected and the file has not been altered.");
                    _logErrorAction(ioEx, $"ZipFs.CreateFile: Source file inaccessible for entry '{normalizedPath}'");
                    info.Context = null;
                    return DokanResult.Error;
                }
                catch (Exception ex)
                {
                    // General exception during caching
                    _logErrorAction(ex, $"ZipFs.CreateFile: EXCEPTION caching entry '{normalizedPath}'.");
                    info.Context = null;
                    return DokanResult.Error;
                }
            }
        }
        else
        {
            bool isDirectory;
            lock (_archiveLock)
            {
                isDirectory = _directoryCreationTimes.ContainsKey(normalizedPath) || normalizedPath == "/";
            }

            if (isDirectory)
            {
                info.IsDirectory = true;
                return mode switch
                {
                    FileMode.Open or FileMode.OpenOrCreate or FileMode.Create => DokanResult.Success,
                    FileMode.CreateNew => DokanResult.FileExists,
                    _ => DokanResult.AccessDenied
                };
            }
        }

        return DokanResult.PathNotFound;
    }

    public NtStatus ReadFile(
        string fileName,
        byte[] buffer,
        out int bytesRead,
        long offset,
        IDokanFileInfo info)
    {
        bytesRead = 0;

        if (info.IsDirectory)
        {
            return DokanResult.AccessDenied;
        }

        // Validate path length before processing
        var pathValidationResult = ValidatePathLength(fileName, nameof(ReadFile));
        if (pathValidationResult != DokanResult.Success)
            return pathValidationResult;

        var normalizedPath = NormalizePath(fileName);

        // Defensive null check
        if (info.Context is not Stream stream)
        {
            // Log but return a more appropriate error code
            _logErrorAction(
                new InvalidOperationException($"ReadFile called for '{normalizedPath}' but info.Context was null. This indicates Cleanup was called before CloseFile."),
                "ZipFs.ReadFile: Context is null - handle already cleaned up.");
            return DokanResult.Error;
        }

        try
        {
            if (stream.CanSeek)
            {
                if (offset >= stream.Length) return DokanResult.Success; // EOF

                stream.Position = offset;
            }
            else
            {
                if (offset != stream.Position)
                {
                    _logErrorAction(new InvalidOperationException($"Non-sequential read requested for non-seekable stream. Path: '{normalizedPath}', Expected offset: {stream.Position}, Requested offset: {offset}."),
                        "ZipFs.ReadFile: Non-sequential read on non-seekable stream.");
                    return DokanResult.Error;
                }
            }

            bytesRead = stream.Read(buffer, 0, buffer.Length);
            return DokanResult.Success;
        }
        catch (Exception ex)
        {
            _logErrorAction(ex, $"ZipFs.ReadFile: EXCEPTION reading from stream for '{normalizedPath}', Offset={offset}.");
            return DokanResult.Error;
        }
    }

    public NtStatus GetFileInformation(string fileName, out FileInformation fileInfo, IDokanFileInfo info)
    {
        fileInfo = new FileInformation();

        // Validate path length before processing
        var pathValidationResult = ValidatePathLength(fileName, nameof(GetFileInformation));
        if (pathValidationResult != DokanResult.Success)
            return pathValidationResult;

        var normalizedPath = NormalizePath(fileName);

        IArchiveEntry? entry;
        bool isImplicitDir;
        var dirCreationTime = DateTime.Now;
        var dirLastWriteTime = DateTime.Now;
        var dirLastAccessTime = DateTime.Now;

        lock (_archiveLock)
        {
            _archiveEntries.TryGetValue(normalizedPath, out entry);
            isImplicitDir = _directoryCreationTimes.ContainsKey(normalizedPath) || normalizedPath == "/";

            if (entry == null && isImplicitDir)
            {
                _directoryCreationTimes.TryGetValue(normalizedPath, out dirCreationTime);
                _directoryLastWriteTimes.TryGetValue(normalizedPath, out dirLastWriteTime);
                _directoryLastAccessTimes.TryGetValue(normalizedPath, out dirLastAccessTime);
            }
        }

        if (entry != null)
        {
            if (IsDirectory(entry))
            {
                fileInfo.Attributes = FileAttributes.Directory;
                if (entry.Key != null)
                {
                    fileInfo.FileName = Path.GetFileName(entry.Key.TrimEnd('/', '\\'));
                }

                fileInfo.LastWriteTime = entry.LastModifiedTime ?? DateTime.Now;
                fileInfo.CreationTime = entry.CreatedTime ?? DateTime.Now;
                fileInfo.LastAccessTime = DateTime.Now;
                info.IsDirectory = true;
            }
            else
            {
                fileInfo.Attributes = FileAttributes.Archive | FileAttributes.ReadOnly;
                fileInfo.FileName = Path.GetFileName(entry.Key) ?? throw new InvalidOperationException("entry.Key is null");
                fileInfo.Length = entry.Size;
                fileInfo.LastWriteTime = entry.LastModifiedTime ?? DateTime.Now;
                fileInfo.CreationTime = entry.CreatedTime ?? DateTime.Now;
                fileInfo.LastAccessTime = DateTime.Now;
                info.IsDirectory = false;
            }

            return DokanResult.Success;
        }
        else if (isImplicitDir)
        {
            fileInfo.Attributes = FileAttributes.Directory;
            fileInfo.FileName = normalizedPath.Split('/').LastOrDefault(static s => !string.IsNullOrEmpty(s)) ?? "";
            fileInfo.LastWriteTime = dirLastWriteTime;
            fileInfo.CreationTime = dirCreationTime;
            fileInfo.LastAccessTime = dirLastAccessTime;
            info.IsDirectory = true;
            return DokanResult.Success;
        }

        return DokanResult.PathNotFound;
    }

    public NtStatus FindFiles(string fileName, out IList<FileInformation> files, IDokanFileInfo info)
    {
        files = new List<FileInformation>();

        // Validate path length before processing
        var pathValidationResult = ValidatePathLength(fileName, nameof(FindFiles));
        if (pathValidationResult != DokanResult.Success)
            return pathValidationResult;

        var normalizedPath = NormalizePath(fileName);

        List<IArchiveEntry> childEntries;
        List<string> implicitChildDirs;
        Dictionary<string, (DateTime CreationTime, DateTime LastWriteTime, DateTime LastAccessTime)> dirTimeSnapshot;

        lock (_archiveLock)
        {
            var isExplicitDirEntry = _archiveEntries.TryGetValue(normalizedPath, out var dirEntry) && IsDirectory(dirEntry);
            var isImplicitDir = _directoryCreationTimes.ContainsKey(normalizedPath) || normalizedPath == "/";

            if (!isExplicitDirEntry && !isImplicitDir) return DokanResult.PathNotFound;

            var searchPrefix = normalizedPath.TrimEnd('/') + (normalizedPath == "/" ? "" : "/");
            if (normalizedPath == "/")
            {
                searchPrefix = "/";
            }

            childEntries = _archiveEntries
                .Where(kvp =>
                {
                    var path = kvp.Key;
                    if (path.Equals(searchPrefix, StringComparison.OrdinalIgnoreCase)) return false;
                    if (!path.StartsWith(searchPrefix, StringComparison.OrdinalIgnoreCase)) return false;

                    var remainder = path.Substring(searchPrefix.Length);
                    // Include direct children (no slashes) or direct child directories (single component ending with /)
                    var slashIndex = remainder.IndexOf('/');
                    if (slashIndex == -1) return true; // No slash - direct child file or directory
                    // Has slash - only include if it's a directory entry AND the slash is at the end (direct child dir)
                    return remainder.EndsWith('/') && slashIndex == remainder.Length - 1;
                })
                .Select(static kvp => kvp.Value)
                .ToList();

            implicitChildDirs = _directoryCreationTimes.Keys
                .Where(k =>
                {
                    if (k.Equals(searchPrefix, StringComparison.OrdinalIgnoreCase)) return false;
                    if (!k.StartsWith(searchPrefix, StringComparison.OrdinalIgnoreCase)) return false;

                    var remainder = k.Substring(searchPrefix.Length);
                    return !remainder.Contains('/') && !string.IsNullOrEmpty(remainder);
                })
                .Where(k => childEntries.All(e => e.Key != null && NormalizePath(e.Key).TrimEnd('/', '\\') != k))
                .ToList();

            // Snapshot directory times while holding the lock
            dirTimeSnapshot = new Dictionary<string, (DateTime, DateTime, DateTime)>(StringComparer.OrdinalIgnoreCase);
            foreach (var dirPathKey in implicitChildDirs)
            {
                _directoryCreationTimes.TryGetValue(dirPathKey, out var ct);
                _directoryLastWriteTimes.TryGetValue(dirPathKey, out var lwt);
                _directoryLastAccessTimes.TryGetValue(dirPathKey, out var lat);
                dirTimeSnapshot[dirPathKey] = (ct, lwt, lat);
            }
        }

        try
        {
            foreach (var entry in childEntries)
            {
                var fi = new FileInformation
                {
                    LastWriteTime = entry.LastModifiedTime ?? DateTime.Now,
                    CreationTime = entry.CreatedTime ?? DateTime.Now,
                    LastAccessTime = DateTime.Now
                };
                if (IsDirectory(entry))
                {
                    fi.Attributes = FileAttributes.Directory;
                    if (entry.Key != null)
                    {
                        var tempFullName = entry.Key.TrimEnd('/', '\\');
                        fi.FileName = Path.GetFileName(tempFullName);
                    }
                }
                else
                {
                    fi.Attributes = FileAttributes.Archive | FileAttributes.ReadOnly;
                    fi.Length = entry.Size;
                    fi.FileName = Path.GetFileName(entry.Key) ?? throw new InvalidOperationException("entry.key is null");
                }

                if (!string.IsNullOrEmpty(fi.FileName)) files.Add(fi);
            }

            foreach (var dirPathKey in implicitChildDirs)
            {
                var name = dirPathKey.Split('/').LastOrDefault(static s => !string.IsNullOrEmpty(s));
                if (!string.IsNullOrEmpty(name))
                {
                    var (ct, lwt, lat) = dirTimeSnapshot[dirPathKey];
                    files.Add(new FileInformation
                    {
                        FileName = name,
                        Attributes = FileAttributes.Directory,
                        LastWriteTime = lwt,
                        CreationTime = ct,
                        LastAccessTime = lat
                    });
                }
            }

            files = files.Where(static f => !string.IsNullOrEmpty(f.FileName)).GroupBy(static f => f.FileName, StringComparer.OrdinalIgnoreCase).Select(static g => g.First()).ToList();
        }
        catch (Exception ex)
        {
            _logErrorAction(ex, $"Error in FindFiles for path '{fileName}'.");
            return DokanResult.Error;
        }

        return DokanResult.Success;
    }

    public NtStatus GetVolumeInformation(out string volumeLabel, out FileSystemFeatures features,
        out string fileSystemName, out uint maximumComponentLength, IDokanFileInfo info)
    {
        volumeLabel = VolumeLabel;
        features = FileSystemFeatures.ReadOnlyVolume | FileSystemFeatures.CasePreservedNames | FileSystemFeatures.UnicodeOnDisk;
        fileSystemName = "ZipFS";
        maximumComponentLength = 255;
        return DokanResult.Success;
    }

    public NtStatus Mounted(string mountPoint, IDokanFileInfo info)
    {
        return DokanResult.Success;
    }

    public NtStatus Unmounted(IDokanFileInfo info)
    {
        return DokanResult.Success;
    }

    public void Cleanup(string fileName, IDokanFileInfo info)
    {
        // This is called when a file handle is closed.
        // Do NOT dispose or null the context here - ReadFile might still be called.
        // The stream will be properly disposed in CloseFile when the file object is destroyed.
    }

    public void CloseFile(string fileName, IDokanFileInfo info)
    {
        // Dispose the stream and clear the context when file object is destroyed
        if (info.Context is IDisposable disposableContext)
        {
            disposableContext.Dispose();
        }

        info.Context = null;
    }

    public NtStatus GetDiskFreeSpace(out long freeBytesAvailable, out long totalNumberOfBytes, out long totalNumberOfFreeBytes, IDokanFileInfo info)
    {
        totalNumberOfBytes = _sourceArchiveStream.CanSeek ? _sourceArchiveStream.Length : 0;
        freeBytesAvailable = 0;
        totalNumberOfFreeBytes = 0;
        return DokanResult.Success;
    }

    // Read-only operations:
    public NtStatus WriteFile(string fileName, byte[] buffer, out int bytesWritten, long offset, IDokanFileInfo info)
    {
        bytesWritten = 0;
        return DokanResult.AccessDenied;
    }

    public NtStatus FlushFileBuffers(string fileName, IDokanFileInfo info)
    {
        return DokanResult.AccessDenied;
    }

    public NtStatus SetFileAttributes(string fileName, FileAttributes attributes, IDokanFileInfo info)
    {
        return DokanResult.AccessDenied;
    }

    public NtStatus SetFileTime(string fileName, DateTime? creationTime, DateTime? lastAccessTime, DateTime? lastWriteTime, IDokanFileInfo info)
    {
        return DokanResult.AccessDenied;
    }

    public NtStatus DeleteFile(string fileName, IDokanFileInfo info)
    {
        return DokanResult.AccessDenied;
    }

    public NtStatus DeleteDirectory(string fileName, IDokanFileInfo info)
    {
        return DokanResult.AccessDenied;
    }

    public NtStatus MoveFile(string oldName, string newName, bool replace, IDokanFileInfo info)
    {
        return DokanResult.AccessDenied;
    }

    public NtStatus SetEndOfFile(string fileName, long length, IDokanFileInfo info)
    {
        return DokanResult.AccessDenied;
    }

    public NtStatus SetAllocationSize(string fileName, long length, IDokanFileInfo info)
    {
        return DokanResult.AccessDenied;
    }

    public NtStatus LockFile(string fileName, long offset, long length, IDokanFileInfo info)
    {
        return DokanResult.Success;
    }

    public NtStatus UnlockFile(string fileName, long offset, long length, IDokanFileInfo info)
    {
        return DokanResult.Success;
    }

    public NtStatus FindStreams(string fileName, out IList<FileInformation> streams, IDokanFileInfo info)
    {
        streams = Array.Empty<FileInformation>();
        return DokanResult.NotImplemented;
    }

    public NtStatus FindFilesWithPattern(string fileName, string searchPattern, out IList<FileInformation> files, IDokanFileInfo info)
    {
        // Validate path length before processing
        var pathValidationResult = ValidatePathLength(fileName, nameof(FindFilesWithPattern));
        if (pathValidationResult != DokanResult.Success)
        {
            files = new List<FileInformation>();
            return pathValidationResult;
        }

        var result = FindFiles(fileName, out var allFiles, info);
        if (result != DokanResult.Success)
        {
            files = new List<FileInformation>();
            return result;
        }

        if (searchPattern is "*" or "*.*")
        {
            files = allFiles;
            return DokanResult.Success;
        }

        try
        {
            files = allFiles.Where(f => IsMatchSimple(f.FileName, searchPattern)).ToList();
        }
        catch (ArgumentException ex)
        {
            _logErrorAction(ex, $"Invalid search pattern '{searchPattern}' in FindFilesWithPattern for path '{fileName}'.");
            files = new List<FileInformation>();
            return DokanResult.InvalidParameter;
        }

        return DokanResult.Success;
    }

    private static bool IsMatchSimple(string input, string pattern)
    {
        if (pattern.Equals("*", StringComparison.Ordinal) || pattern.Equals("*.*", StringComparison.Ordinal)) return true;

        var regexPattern = "^" + Regex.Escape(pattern).Replace("\\*", ".*").Replace("\\?", ".") + "$";
        return Regex.IsMatch(input, regexPattern, RegexOptions.IgnoreCase);
    }

    public NtStatus GetFileSecurity(string fileName, out FileSystemSecurity? security, AccessControlSections sections, IDokanFileInfo info)
    {
        security = null;

        // Validate path length before processing
        var pathValidationResult = ValidatePathLength(fileName, nameof(GetFileSecurity));
        if (pathValidationResult != DokanResult.Success)
            return pathValidationResult;

        try
        {
            var fs = new FileSecurity();
            fs.AddAccessRule(new FileSystemAccessRule(new SecurityIdentifier(WellKnownSidType.WorldSid, null), FileSystemRights.ReadAndExecute, AccessControlType.Allow));
            fs.SetOwner(new SecurityIdentifier(WellKnownSidType.WorldSid, null));
            fs.SetGroup(new SecurityIdentifier(WellKnownSidType.WorldSid, null));
            security = fs;
            return DokanResult.Success;
        }
        catch (Exception ex)
        {
            _logErrorAction(ex, $"Error in GetFileSecurity for '{fileName}'.");
            return DokanResult.Error;
        }
    }

    public NtStatus SetFileSecurity(string fileName, FileSystemSecurity security, AccessControlSections sections, IDokanFileInfo info)
    {
        return DokanResult.AccessDenied;
    }

    /// <summary>
    /// Disposes the resources used by this instance.
    /// </summary>
    /// <remarks>
    /// <para><strong>Note:</strong> This method disposes the internal archive and cleans up temporary files,
    /// but does NOT dispose the source archive stream that was passed to the constructor. The caller is
    /// responsible for disposing the source stream after this instance has been disposed.</para>
    /// </remarks>
    public void Dispose()
    {
        // Dispose the archive (which may dispose its own internal streams)
        // Note: We do NOT dispose _sourceArchiveStream as the caller owns it
        _archive.Dispose();

        // When the drive is unmounted, clean up all temporary files that were created.
        lock (_archiveLock)
        {
            foreach (var tempFile in _largeFileCache.Values)
            {
                try
                {
                    if (File.Exists(tempFile))
                    {
                        File.Delete(tempFile);
                    }
                }
                catch (Exception ex)
                {
                    _logErrorAction(ex, $"Failed to delete temp file on dispose: {tempFile}");
                }
            }

            _largeFileCache.Clear();
        }

        // Clean up extracted executables
        lock (_executableLock)
        {
            foreach (var execFile in _executableCache.Values)
            {
                try
                {
                    if (File.Exists(execFile))
                    {
                        File.Delete(execFile);
                        Console.WriteLine($"Cleaned up extracted executable: '{execFile}'");
                    }
                }
                catch (Exception ex)
                {
                    _logErrorAction(ex, $"Failed to delete extracted executable on dispose: {execFile}");
                }
            }

            _executableCache.Clear();
        }

        // Clean up the unique temporary directory for this instance
        try
        {
            if (Directory.Exists(_tempDirectoryPath))
            {
                Directory.Delete(_tempDirectoryPath, true);
            }
        }
        catch (Exception ex)
        {
            _logErrorAction(ex, $"Failed to delete temp directory on dispose: {_tempDirectoryPath}");
        }

        GC.SuppressFinalize(this);
    }

    /// <summary>
    /// A MemoryStream wrapper that tracks memory usage and decrements the counter when disposed.
    /// Used to prevent unbounded memory consumption when many small files are opened.
    /// </summary>
    private class TrackedMemoryStream : MemoryStream
    {
        private readonly ZipFs _owner;
        private readonly int _size;
        private bool _disposed;

        public TrackedMemoryStream(byte[] buffer, ZipFs owner) : base(buffer, false)
        {
            _owner = owner;
            _size = buffer.Length;
            _disposed = false;
        }

        protected override void Dispose(bool disposing)
        {
            if (!_disposed)
            {
                if (disposing)
                {
                    // Decrement the memory usage counter
                    lock (_owner._memoryLock)
                    {
                        _owner._currentMemoryUsage -= _size;
                        if (_owner._currentMemoryUsage < 0)
                        {
                            _owner._currentMemoryUsage = 0; // Safety check
                        }
                    }
                }

                _disposed = true;
            }

            base.Dispose(disposing);
        }
    }
}
