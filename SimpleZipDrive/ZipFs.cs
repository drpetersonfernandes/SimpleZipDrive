using System.Collections.Concurrent;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text.RegularExpressions;
using DokanNet;
using SharpCompress.Archives;
using SharpCompress.Archives.Rar;
using SharpCompress.Archives.SevenZip;
using SharpCompress.Archives.Zip;
using SharpCompress.Common;
using SharpCompress.Compressors.Deflate;
using SharpCompress.Readers;
using DokanFileAccess = DokanNet.FileAccess;

namespace SimpleZipDrive;

public class ZipFs : IDokanOperations, IDisposable
{
    // Cache for compiled regex patterns to avoid recompilation overhead
    private static readonly ConcurrentDictionary<string, Regex> RegexCache = new();
    private const int MaxRegexCacheSize = 100; // Limit cache size to prevent unbounded growth

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
    private readonly int _maxMemorySize;

    // Cache for entries that failed to decompress (e.g., SharpCompress RAR unpacker bugs). Prevents repeated costly attempts.
    private readonly HashSet<string> _failedEntries = new(StringComparer.OrdinalIgnoreCase);

    // Memory throttling for small files to prevent unbounded resource consumption
    private const long BytesPerMegabyte = 1024 * 1024;
    private const long MaxTotalMemoryCache = 1024L * BytesPerMegabyte; // 1 GB total limit for all small files
    private long _currentMemoryUsage;
    private readonly object _memoryLock = new();

    private readonly string _tempDirectoryPath;
    private readonly Func<string?> _passwordProvider;
    private readonly string _archiveType;

    private const string VolumeLabel = "SimpleZipDrive";
    private const int DefaultMaxMemorySize = 512 * 1024 * 1024; // 512 MB default for file caching
    private static readonly char[] Separator = ['/'];

    // Windows path length limits
    private const int MaxPath = 260;
    private const int MaxPathExtended = 32767;
    private const string ExtendedPathPrefix = @"\\?\";

    // Static flag to ensure cleanup runs only once
    private static int _cleanupPerformed;

    /// <summary>
    /// Cleans up orphaned temp directories from previous crashed sessions.
    /// Directories associated with non-running processes are deleted.
    /// </summary>
    private static void CleanupOrphanedTempDirectories()
    {
        try
        {
            var baseTempPath = Path.Combine(Path.GetTempPath(), "SimpleZipDrive");
            if (!Directory.Exists(baseTempPath))
                return;

            var tempDirs = Directory.GetDirectories(baseTempPath);
            foreach (var dir in tempDirs)
            {
                try
                {
                    // Extract PID from directory name (format: {PID}_{Guid})
                    var dirName = Path.GetFileName(dir);
                    var underscoreIndex = dirName.IndexOf('_');
                    if (underscoreIndex <= 0)
                        continue;

                    if (!int.TryParse(dirName.AsSpan(0, underscoreIndex), out var pid))
                        continue;

                    // Check if process is still running
                    bool processExists;
                    try
                    {
                        // ReSharper disable once ConditionIsAlwaysTrueOrFalseAccordingToNullableAPIContract
                        processExists = System.Diagnostics.Process.GetProcessById(pid) != null;
                    }
                    catch (ArgumentException)
                    {
                        // Process doesn't exist
                        processExists = false;
                    }

                    if (!processExists)
                    {
                        Directory.Delete(dir, true);
                    }
                }
                catch
                {
                    // Ignore errors for individual directories
                }
            }
        }
        catch
        {
            // Ignore cleanup errors - this is best-effort
        }
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="ZipFs"/> class.
    /// </summary>
    /// <param name="archiveStream">The stream containing the archive data. The caller retains ownership of this stream and is responsible for disposing it.</param>
    /// <param name="mountPoint">The mount point for the virtual drive (used for logging purposes).</param>
    /// <param name="logErrorAction">Action to invoke when logging errors.</param>
    /// <param name="passwordProvider">Function that provides the password for encrypted archives.</param>
    /// <param name="archiveType">The type of archive ("zip", "7z", or "rar").</param>
    /// <param name="maxMemorySize">Maximum memory size in bytes for caching a file in RAM before falling back to disk cache. Defaults to 512 MB.</param>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="logErrorAction"/> is null.</exception>
    /// <exception cref="NotSupportedException">Thrown when the archive type is not supported.</exception>
    /// <remarks>
    /// <para><strong>Important:</strong> The <paramref name="archiveStream"/> is stored by reference but NOT disposed by this instance.
    /// The caller must ensure the stream remains open during the entire lifetime of this instance and dispose it after
    /// this <see cref="ZipFs"/> instance has been disposed.</para>
    /// </remarks>
    public ZipFs(Stream archiveStream, string mountPoint, Action<Exception?, string?> logErrorAction, Func<string?> passwordProvider, string archiveType, int maxMemorySize = DefaultMaxMemorySize)
    {
        // Perform cleanup of orphaned temp directories from previous crashes (only once per app session)
        if (Interlocked.Exchange(ref _cleanupPerformed, 1) == 0)
        {
            CleanupOrphanedTempDirectories();
        }

        _sourceArchiveStream = archiveStream;
        _logErrorAction = logErrorAction;
        _passwordProvider = passwordProvider;
        _archiveType = archiveType.ToLowerInvariant();
        _maxMemorySize = maxMemorySize;
        // Use a unique directory per instance in the system temp folder to avoid collisions between multiple running instances
        _tempDirectoryPath = Path.Combine(Path.GetTempPath(), "SimpleZipDrive", $"{Environment.ProcessId}_{Guid.NewGuid():N}");

        try
        {
            // Ensure the dedicated working directory exists for this session.
            Directory.CreateDirectory(_tempDirectoryPath);

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
            bool hasEncryptedEntries;
            try
            {
                hasEncryptedEntries = archiveWithoutPassword.Entries.Any(static e => e.IsEncrypted);
            }
            catch (Exception entryEx) when (IsPasswordRequiredException(entryEx))
            {
                archiveWithoutPassword.Dispose();
                throw;
            }
            catch (Exception entryEx) when (!IsPasswordRequiredException(entryEx))
            {
                archiveWithoutPassword.Dispose();
                throw new InvalidOperationException(
                    "The archive file appears to be corrupted, incomplete, or uses an unsupported format/feature that could not be parsed.", entryEx);
            }

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
        catch (InvalidOperationException)
        {
            throw;
        }
        catch (NotSupportedException)
        {
            throw;
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException(
                "The archive file appears to be corrupted, incomplete, or uses an unsupported format/feature that could not be parsed.", ex);
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

    private static bool IsDataErrorException(Exception ex)
    {
        // DataErrorException is an internal SharpCompress exception type
        // Check by type name and message content to avoid dependency on internal types
        var exceptionTypeName = ex.GetType().Name;
        return exceptionTypeName.Contains("DataError", StringComparison.OrdinalIgnoreCase) ||
               ex.Message.Contains("Data Error", StringComparison.OrdinalIgnoreCase);
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
            // Check for data corruption indicators (LZMA decompression errors, SharpCompress DataErrorException, etc.)
            var exceptionTypeName = ex.GetType().Name;
            var message = ex.Message;
            var stackTrace = ex.StackTrace ?? "";
            var isDataCorruptionError = ex is IndexOutOfRangeException ||
                                        exceptionTypeName.Contains("DataError", StringComparison.OrdinalIgnoreCase) ||
                                        message.Contains("Data Error", StringComparison.OrdinalIgnoreCase) ||
                                        stackTrace.Contains("SharpCompress.Compressors.LZMA", StringComparison.OrdinalIgnoreCase) ||
                                        stackTrace.Contains("SharpCompress.Archives.Zip.ZipArchive.LoadEntries", StringComparison.OrdinalIgnoreCase);

            if (isDataCorruptionError)
            {
                var contextMessage = "Archive data corruption detected during initialization. The archive file appears to be damaged, incomplete, or uses an unsupported compression method. " +
                                     "Archive type: " + _archiveType + ". " +
                                     "Entries loaded before error: " + _archiveEntries.Count + ". " +
                                     "Exception: " + exceptionTypeName + ": " + ex.Message;
                _logErrorAction?.Invoke(ex, contextMessage);
                throw new InvalidOperationException(contextMessage, ex);
            }

            _logErrorAction?.Invoke(ex, "Error during ZipFs.InitializeEntries. Archive type: " + _archiveType + ", Entries loaded: " + _archiveEntries.Count + ".");
            throw;
        }
    }

    private static bool IsDirectory(IArchiveEntry entry)
    {
        // ReSharper disable once ConditionIsAlwaysTrueAccordingToNullableAPIContract
        return entry.IsDirectory || (entry.Key != null && (entry.Key.EndsWith('/') || entry.Key.EndsWith('\\')));
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
        DokanFileAccess access,
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

                // Block write access to directory handles. Allow only read-related flags.
                if ((access & ~DokanFileAccess.ReadData & ~DokanFileAccess.ReadAttributes & ~DokanFileAccess.Execute) != 0)
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

                // Fast-fail for entries that previously failed decompression
                lock (_archiveLock)
                {
                    if (_failedEntries.Contains(normalizedPath))
                    {
                        return DokanResult.Error;
                    }
                }

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

                    // Hybrid caching - memory for small files, temp disk file for large files
                    if (entrySize >= _maxMemorySize || entrySize < 0)
                    {
                        // --- Large file: Cache to disk ---
                        string? cachedPath;
                        lock (_archiveLock)
                        {
                            _largeFileCache.TryGetValue(normalizedPath, out cachedPath);
                        }

                        if (cachedPath != null)
                        {
                            Console.WriteLine($"Reusing existing temporary cache for '{normalizedPath}'.");
                        }
                        else
                        {
                            Console.WriteLine($"Large file detected: '{normalizedPath}' ({entrySize / 1024.0 / 1024.0:F2} MB). Extracting to temporary disk cache...");
                            var newTempFilePath = CreateSecureTempFile();

                            if (entrySize >= 0)
                            {
                                try
                                {
                                    var tempDrivePathRoot = Path.GetPathRoot(newTempFilePath) ?? "C:\\";
                                    var tempDrive = new DriveInfo(tempDrivePathRoot);
                                    if (tempDrive.AvailableFreeSpace < entrySize)
                                    {
                                        // Clean up the temp file we created before returning error
                                        try
                                        {
                                            File.Delete(newTempFilePath);
                                        }
                                        catch
                                        {
                                            /* Best effort cleanup */
                                        }

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
                            using var tempFileStream = new FileStream(newTempFilePath, FileMode.Truncate, System.IO.FileAccess.Write, FileShare.None);
                            entryStream.CopyTo(tempFileStream);

                            lock (_archiveLock)
                            {
                                _largeFileCache[normalizedPath] = newTempFilePath;
                            }

                            cachedPath = newTempFilePath;
                            Console.WriteLine($"Extraction complete for '{normalizedPath}'. Temp file: '{newTempFilePath}'");
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
                        else
                        {
                            // cachedPath is null/empty - caching failed but wasn't caught by exception
                            _logErrorAction(new InvalidOperationException($"ZipFs.CreateFile: cachedPath is null/empty for large file '{normalizedPath}' after caching attempt."), "ZipFs.CreateFile: Large file caching failed silently.");
                            return DokanResult.Error;
                        }
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
                                _largeFileCache.TryGetValue(normalizedPath, out cachedPath);
                            }

                            if (cachedPath == null)
                            {
                                var newTempFilePath = CreateSecureTempFile();

                                using var entryStream = entry.OpenEntryStream();
                                using var tempFileStream = new FileStream(newTempFilePath, FileMode.Truncate, System.IO.FileAccess.Write, FileShare.None);
                                entryStream.CopyTo(tempFileStream);

                                lock (_archiveLock)
                                {
                                    _largeFileCache[normalizedPath] = newTempFilePath;
                                }

                                cachedPath = newTempFilePath;
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
                            using var entryStream = entry.OpenEntryStream();
                            var capacity = entrySize > 0 ? (int)Math.Min(entrySize, int.MaxValue) : 4096;
                            using var tempMs = new MemoryStream(capacity);
                            entryStream.CopyTo(tempMs);
                            var entryBytes = tempMs.ToArray();

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
                    Console.WriteLine($"\n{AppTheme.Warning} Password Error: Could not decrypt '{normalizedPath}'.");
                    _logErrorAction(cryptoEx, contextMessage);
                    (info.Context as IDisposable)?.Dispose();
                    info.Context = null;
                    return DokanResult.Error;
                }
                catch (IOException ioEx) when ((uint)ioEx.HResult == 0x80070015) // ERROR_NOT_READY
                {
                    var msg = $"CRITICAL ERROR: The source drive containing the archive file is no longer ready. " +
                              $"Please check the connection to drive '{Path.GetPathRoot(_tempDirectoryPath)}'.";
                    Console.WriteLine($"\n{AppTheme.Critical} {msg}");
                    (info.Context as IDisposable)?.Dispose();
                    info.Context = null;
                    return DokanResult.NotReady;
                }
                catch (IOException ioEx) when ((uint)ioEx.HResult == 0x800703EE || (uint)ioEx.HResult == 0x80070037) // ERROR_FILE_INVALID or ERROR_DEV_NOT_EXIST
                {
                    Console.WriteLine($"\n{AppTheme.Section("SOURCE FILE ACCESS ERROR")}");
                    Console.WriteLine("Error: The source archive file is no longer accessible.");
                    Console.WriteLine($"Details: {ioEx.Message}");
                    Console.WriteLine("\nThis usually means:");
                    Console.WriteLine($"{AppTheme.Bullet}The external drive/USB device was disconnected");
                    Console.WriteLine($"{AppTheme.Bullet}The archive file was modified or deleted after mounting started");
                    Console.WriteLine($"{AppTheme.Bullet}The source device is no longer available or has errors");
                    Console.WriteLine("\nPlease verify the drive is connected and the file has not been altered.");
                    _logErrorAction(ioEx, $"ZipFs.CreateFile: Source file inaccessible for entry '{normalizedPath}'");
                    (info.Context as IDisposable)?.Dispose();
                    info.Context = null;
                    return DokanResult.Error;
                }
                catch (ZlibException zlibEx)
                {
                    var contextMessage = $"ZipFs.CreateFile: Deflate decompression error for '{normalizedPath}' ({entry.Size / 1024.0:F1} KB). The zip entry may be corrupted or the source stream returned invalid data.";
                    Console.WriteLine($"\n{AppTheme.Warning} Decompression Error: Cannot read '{normalizedPath}'. The file data appears to be corrupted.");
                    _logErrorAction(zlibEx, contextMessage);
                    lock (_archiveLock)
                    {
                        _failedEntries.Add(normalizedPath);
                    }

                    (info.Context as IDisposable)?.Dispose();
                    info.Context = null;
                    return DokanResult.Error;
                }
                catch (ArgumentOutOfRangeException argEx)
                {
                    var contextMessage = $"ZipFs.CreateFile: Invalid data offset for '{normalizedPath}' ({entry.Size / 1024.0:F1} KB). The zip archive appears to be corrupted or truncated — the entry header points to an invalid file position.";
                    Console.WriteLine($"\n{AppTheme.Warning} Corruption Error: Cannot read '{normalizedPath}'. The archive file may be damaged or incomplete.");
                    _logErrorAction(argEx, contextMessage);
                    lock (_archiveLock)
                    {
                        _failedEntries.Add(normalizedPath);
                    }

                    (info.Context as IDisposable)?.Dispose();
                    info.Context = null;
                    return DokanResult.Error;
                }
                catch (NullReferenceException nre)
                {
                    // SharpCompress RAR unpacker bug: NullReferenceException in UnpackV1.Unpack.Unpack29
                    // Cache the failed entry to avoid repeated expensive decompression attempts
                    lock (_archiveLock)
                    {
                        _failedEntries.Add(normalizedPath);
                    }

                    _logErrorAction(nre, $"ZipFs.CreateFile: NullReferenceException during decompression of '{normalizedPath}' (likely SharpCompress RAR V1 unpacker bug). Entry marked as failed to prevent retries.");
                    Console.WriteLine($"\n{AppTheme.Warning} Decompression Error: Cannot read '{normalizedPath}'. The entry may use an unsupported or buggy compression method.");
                    (info.Context as IDisposable)?.Dispose();
                    info.Context = null;
                    return DokanResult.Error;
                }
                catch (Exception ex) when (IsDataErrorException(ex))
                {
                    // SharpCompress LZMA/7z DataErrorException: Corrupted or unsupported compressed data
                    // Cache the failed entry to avoid repeated expensive decompression attempts
                    lock (_archiveLock)
                    {
                        _failedEntries.Add(normalizedPath);
                    }

                    var contextMessage = $"ZipFs.CreateFile: Data error (corrupted or unsupported compression) for '{normalizedPath}' ({entry.Size / 1024.0:F1} KB). The archive entry may be damaged or uses an unsupported compression method.";
                    _logErrorAction(ex, contextMessage);
                    Console.WriteLine($"\n{AppTheme.Warning} Decompression Error: Cannot read '{normalizedPath}'. The file data appears to be corrupted or uses an unsupported compression method.");
                    (info.Context as IDisposable)?.Dispose();
                    info.Context = null;
                    return DokanResult.Error;
                }
                catch (Exception ex)
                {
                    // General exception during caching
                    _logErrorAction(ex, $"ZipFs.CreateFile: EXCEPTION caching entry '{normalizedPath}'.");
                    (info.Context as IDisposable)?.Dispose();
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

                if ((access & (DokanFileAccess.WriteData | DokanFileAccess.AppendData)) != 0)
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
                // ReSharper disable once ConditionIsAlwaysTrueAccordingToNullableAPIContract
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
        // Validate path length before processing
        var pathValidationResult = ValidatePathLength(fileName, nameof(FindFiles));
        if (pathValidationResult != DokanResult.Success)
        {
            files = Array.Empty<FileInformation>();
            return pathValidationResult;
        }

        var normalizedPath = NormalizePath(fileName);
        var resultFiles = new List<FileInformation>();
        // Use HashSet for O(1) duplicate checking instead of GroupBy
        var seenFileNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        lock (_archiveLock)
        {
            var isExplicitDirEntry = _archiveEntries.TryGetValue(normalizedPath, out var dirEntry) && IsDirectory(dirEntry);
            var isImplicitDir = _directoryCreationTimes.ContainsKey(normalizedPath) || normalizedPath == "/";

            if (!isExplicitDirEntry && !isImplicitDir)
            {
                files = Array.Empty<FileInformation>();
                return DokanResult.PathNotFound;
            }

            var searchPrefix = normalizedPath.TrimEnd('/') + (normalizedPath == "/" ? "" : "/");
            if (normalizedPath == "/")
            {
                searchPrefix = "/";
            }

            // Process entries directly without creating intermediate collections
            foreach (var kvp in _archiveEntries)
            {
                var path = kvp.Key;
                if (path.Equals(searchPrefix, StringComparison.OrdinalIgnoreCase)) continue;
                if (!path.StartsWith(searchPrefix, StringComparison.OrdinalIgnoreCase)) continue;

                var remainder = path.Substring(searchPrefix.Length);
                // Include direct children (no slashes) or direct child directories (single component ending with /)
                var slashIndex = remainder.IndexOf('/');
                if (slashIndex != -1)
                {
                    // Has slash - only include if it's a directory entry AND the slash is at the end (direct child dir)
                    if (!(remainder.EndsWith('/') && slashIndex == remainder.Length - 1))
                        continue;
                }

                var entry = kvp.Value;
                string? fileNameOnly = null;

                if (IsDirectory(entry))
                {
                    // ReSharper disable once ConditionIsAlwaysTrueAccordingToNullableAPIContract
                    if (entry.Key != null)
                    {
                        var tempFullName = entry.Key.TrimEnd('/', '\\');
                        fileNameOnly = Path.GetFileName(tempFullName);
                    }
                }
                else
                {
                    fileNameOnly = Path.GetFileName(entry.Key);
                }

                if (!string.IsNullOrEmpty(fileNameOnly) && seenFileNames.Add(fileNameOnly))
                {
                    resultFiles.Add(new FileInformation
                    {
                        FileName = fileNameOnly,
                        Attributes = IsDirectory(entry) ? FileAttributes.Directory : (FileAttributes.Archive | FileAttributes.ReadOnly),
                        Length = entry.Size,
                        LastWriteTime = entry.LastModifiedTime ?? DateTime.Now,
                        CreationTime = entry.CreatedTime ?? DateTime.Now,
                        LastAccessTime = DateTime.Now
                    });
                }
            }

            // Process implicit directories directly
            foreach (var dirPathKey in _directoryCreationTimes.Keys)
            {
                if (dirPathKey.Equals(searchPrefix, StringComparison.OrdinalIgnoreCase)) continue;
                if (!dirPathKey.StartsWith(searchPrefix, StringComparison.OrdinalIgnoreCase)) continue;

                var remainder = dirPathKey.Substring(searchPrefix.Length);
                if (remainder.Contains('/') || string.IsNullOrEmpty(remainder)) continue;

                // Skip if already added from entries
                if (seenFileNames.Contains(dirPathKey)) continue;

                var name = dirPathKey.Split('/').LastOrDefault(static s => !string.IsNullOrEmpty(s));
                if (!string.IsNullOrEmpty(name) && seenFileNames.Add(name))
                {
                    _directoryCreationTimes.TryGetValue(dirPathKey, out var ct);
                    _directoryLastWriteTimes.TryGetValue(dirPathKey, out var lwt);
                    _directoryLastAccessTimes.TryGetValue(dirPathKey, out var lat);

                    resultFiles.Add(new FileInformation
                    {
                        FileName = name,
                        Attributes = FileAttributes.Directory,
                        LastWriteTime = lwt,
                        CreationTime = ct,
                        LastAccessTime = lat
                    });
                }
            }
        }

        files = resultFiles;
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
        totalNumberOfBytes = _sourceArchiveStream.CanSeek ? _sourceArchiveStream.Length : long.MaxValue;
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

    private const int MaxSearchPatternLength = 260; // Reasonable limit for search patterns

    /// <summary>
    /// Creates a temporary file with restricted permissions accessible only to the current user.
    /// Returns the path to the created file.
    /// </summary>
    private string CreateSecureTempFile()
    {
        var tempFilePath = Path.Combine(_tempDirectoryPath, Guid.NewGuid().ToString("N") + ".tmp");

        // Create an empty file first
        File.Create(tempFilePath).Dispose();

        // Use FileInfo to get and set access control
        var fileInfo = new FileInfo(tempFilePath);

        // Get the current file security settings
        var fileSecurity = fileInfo.GetAccessControl();

        // Remove any inherited access rules to ensure clean slate
        fileSecurity.SetAccessRuleProtection(true, false);

        // Clear all existing access rules
        var existingRules = fileSecurity.GetAccessRules(true, true, typeof(SecurityIdentifier));
        foreach (FileSystemAccessRule rule in existingRules)
        {
            fileSecurity.RemoveAccessRule(rule);
        }

        // Get the current user's identity
        var currentUser = WindowsIdentity.GetCurrent();
        var currentUserSid = currentUser.User ?? throw new InvalidOperationException("Unable to get current user SID");

        // Grant full control to the current user only
        var accessRule = new FileSystemAccessRule(
            currentUserSid,
            FileSystemRights.FullControl,
            AccessControlType.Allow);
        fileSecurity.AddAccessRule(accessRule);

        // Apply the security settings to the file
        fileInfo.SetAccessControl(fileSecurity);

        return tempFilePath;
    }

    private static bool IsMatchSimple(string input, string pattern)
    {
        // Limit pattern length to prevent regex DoS attacks
        if (pattern.Length > MaxSearchPatternLength)
        {
            return false;
        }

        if (pattern.Equals("*", StringComparison.Ordinal) || pattern.Equals("*.*", StringComparison.Ordinal)) return true;

        var regexPattern = "^" + Regex.Escape(pattern).Replace("\\*", ".*").Replace("\\?", ".") + "$";

        // Use cached compiled regex or create and cache new one
        var regex = RegexCache.GetOrAdd(regexPattern, static p =>
        {
            // Limit cache size - remove oldest entry if at capacity
            if (RegexCache.Count >= MaxRegexCacheSize)
            {
                var oldestKey = RegexCache.Keys.FirstOrDefault();
                if (oldestKey != null)
                {
                    RegexCache.TryRemove(oldestKey, out _);
                }
            }

            return new Regex(p, RegexOptions.IgnoreCase | RegexOptions.Compiled, TimeSpan.FromMilliseconds(100));
        });

        return regex.IsMatch(input);
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
            // Use raw SID string "S-1-1-0" (Everyone/World) to avoid IdentityNotMappedException
            // when the SID translation fails on certain systems
            var everyoneSid = new SecurityIdentifier("S-1-1-0");
            fs.AddAccessRule(new FileSystemAccessRule(everyoneSid, FileSystemRights.ReadAndExecute, AccessControlType.Allow));
            fs.SetOwner(everyoneSid);
            fs.SetGroup(everyoneSid);
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
    /// <para><strong>Note:</strong> This method disposes the internal archive and cleans up extracted files,
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

        // Clean up the unique working directory for this instance
        try
        {
            if (Directory.Exists(_tempDirectoryPath))
            {
                Directory.Delete(_tempDirectoryPath, true);
            }
        }
        catch (Exception ex)
        {
            _logErrorAction(ex, $"Failed to delete working directory on dispose: {_tempDirectoryPath}");
        }

        // Reset memory tracking counters to prevent stale values
        lock (_memoryLock)
        {
            _currentMemoryUsage = 0;
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
