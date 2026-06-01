using System.Security.AccessControl;
using System.Security.Principal;
using SharpCompress.Archives;
using SharpCompress.Archives.Rar;
using SharpCompress.Archives.SevenZip;
using SharpCompress.Archives.Zip;
using SharpCompress.Common;
using SharpCompress.Readers;

namespace SimpleZipDrive.Core;

/// <summary>
/// Shared core filesystem logic for both Dokan and WinFsp ZipFs wrappers.
/// Handles archive parsing, entry lookup, caching, throttling, and disposal.
/// </summary>
public class ZipFileSystemCore : IDisposable
{
    private readonly Stream _sourceArchiveStream;
    private readonly IArchive _archive;
    internal readonly Dictionary<string, IArchiveEntry> ArchiveEntries = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, DateTime> _directoryCreationTimes = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, DateTime> _directoryLastWriteTimes = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, DateTime> _directoryLastAccessTimes = new(StringComparer.OrdinalIgnoreCase);

    private readonly Action<Exception?, string?> _logErrorAction;
    private readonly object _archiveLock = new();

    // Cache for large files extracted to disk.
    internal readonly Dictionary<string, string> LargeFileCache = new(StringComparer.OrdinalIgnoreCase);

    // Cache for entries that failed to decompress.
    private readonly HashSet<string> _failedEntries = new(StringComparer.OrdinalIgnoreCase);

    // Memory throttling for small files.
    internal readonly long MaxTotalMemoryCache;
    internal long CurrentMemoryUsage;
    private readonly object _memoryLock = new();

    private readonly Func<string?> _passwordProvider;
    private int _disposedInt;

    public const string DefaultVolumeLabel = "SimpleZipDrive";
    public const long DefaultMaxMemorySize = 512L * 1024 * 1024;

    public string VolumeLabel { get; }

    /// <summary>
    /// Initializes a new instance of the <see cref="ZipFileSystemCore"/> class.
    /// </summary>
    public ZipFileSystemCore(
        Stream archiveStream,
        string mountPoint,
        Action<Exception?, string?> logErrorAction,
        Func<string?> passwordProvider,
        string archiveType,
        long maxMemorySize = DefaultMaxMemorySize,
        string? volumeLabel = null)
    {
        ZipFsHelpers.EnsureCleanupPerformed();

        _sourceArchiveStream = archiveStream;
        _logErrorAction = logErrorAction;
        _passwordProvider = passwordProvider;
        ArchiveType = archiveType.ToLowerInvariant();
        MaxMemorySize = maxMemorySize;
        VolumeLabel = volumeLabel ?? DefaultVolumeLabel;
        var availableMemory = GC.GetGCMemoryInfo().TotalAvailableMemoryBytes;
        MaxTotalMemoryCache = (long)(availableMemory * 0.90);

        var tempDirName = ZipFsHelpers.GenerateTempDirectoryName();
        TempDirectoryPath = Path.Combine(Path.GetTempPath(), "SimpleZipDrive", tempDirName);
        ZipFsHelpers.RegisterCurrentTempDirectory(tempDirName);

        try
        {
            Directory.CreateDirectory(TempDirectoryPath);

            if (archiveStream.CanSeek)
            {
                archiveStream.Position = 0;
            }

            _archive = OpenArchive(archiveStream);
            InitializeEntries();

            DiagnosticLogger.LogSection("ZipFs CONSTRUCTED");
            DiagnosticLogger.Log($"  Archive type: {ArchiveType}");
            DiagnosticLogger.Log($"  Mount point: {mountPoint}");
            DiagnosticLogger.Log($"  Total entries: {ArchiveEntries.Count}");
            DiagnosticLogger.Log($"  Implicit directories: {_directoryCreationTimes.Count}");
            DiagnosticLogger.Log($"  Max memory cache: {maxMemorySize / 1024.0 / 1024.0:F0} MB");
            DiagnosticLogger.Log($"  Max total memory: {MaxTotalMemoryCache / 1024.0 / 1024.0:F0} MB");
            DiagnosticLogger.Log($"  Temp directory: {TempDirectoryPath}");
            DiagnosticLogger.Log($"  Source stream CanSeek: {archiveStream.CanSeek}");
            DiagnosticLogger.Log($"  Source stream Length: {(archiveStream.CanSeek ? archiveStream.Length / 1024.0 / 1024.0 : -1):F2} MB");
        }
        catch (Exception ex)
        {
            DiagnosticLogger.LogSection("ZipFs CONSTRUCTION FAILED");
            DiagnosticLogger.Log(ex, $"Archive type: {ArchiveType}, Mount: {mountPoint}");
            _logErrorAction(ex, $"Error during ZipFs construction for mount point '{mountPoint}'.");
            throw;
        }
    }

    public bool IsDisposed => Volatile.Read(ref _disposedInt) != 0;
    public string ArchiveType { get; }

    public string TempDirectoryPath { get; }

    public long MaxMemorySize { get; }

    public long TotalSize => _sourceArchiveStream.CanSeek ? _sourceArchiveStream.Length : 0;

    private IArchive OpenArchive(Stream stream)
    {
        try
        {
            var archiveWithoutPassword = ArchiveType switch
            {
                "zip" => ZipArchive.OpenArchive(stream, new ReaderOptions()),
                "7z" => SevenZipArchive.OpenArchive(stream, new ReaderOptions()),
                "rar" => RarArchive.OpenArchive(stream, new ReaderOptions()),
                _ => throw new NotSupportedException($"Archive type '{ArchiveType}' is not supported.")
            };

            bool hasEncryptedEntries;
            try
            {
                hasEncryptedEntries = archiveWithoutPassword.Entries.Any(static e => e.IsEncrypted);
            }
            catch (Exception entryEx) when (ZipFsHelpers.IsPasswordRequiredException(entryEx))
            {
                archiveWithoutPassword.Dispose();
                throw;
            }
            catch (Exception entryEx) when (!ZipFsHelpers.IsPasswordRequiredException(entryEx))
            {
                archiveWithoutPassword.Dispose();
                throw new InvalidOperationException(
                    "The archive file appears to be corrupted, incomplete, or uses an unsupported format/feature that could not be parsed.", entryEx);
            }

            if (!hasEncryptedEntries)
            {
                return archiveWithoutPassword;
            }

            archiveWithoutPassword.Dispose();
        }
        catch (Exception ex) when (ZipFsHelpers.IsPasswordRequiredException(ex))
        {
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

        if (stream.CanSeek)
        {
            stream.Position = 0;
        }

        var password = _passwordProvider();

        return ArchiveType switch
        {
            "zip" => ZipArchive.OpenArchive(stream, new ReaderOptions { Password = password }),
            "7z" => SevenZipArchive.OpenArchive(stream, new ReaderOptions { Password = password }),
            "rar" => RarArchive.OpenArchive(stream, new ReaderOptions { Password = password }),
            _ => throw new NotSupportedException($"Archive type '{ArchiveType}' is not supported.")
        };
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

                var normalizedPath = ZipFsHelpers.NormalizePath(entry.Key);
                ArchiveEntries[normalizedPath] = entry;

                var currentPath = "";
                var parts = normalizedPath.Split('/', StringSplitOptions.RemoveEmptyEntries);
                for (var i = 0; i < parts.Length - (ZipFsHelpers.IsDirectory(entry) ? 0 : 1); i++)
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
                                     "Archive type: " + ArchiveType + ". " +
                                     "Entries loaded before error: " + ArchiveEntries.Count + ". " +
                                     "Exception: " + exceptionTypeName + ": " + ex.Message;
                _logErrorAction?.Invoke(ex, contextMessage);
                throw new InvalidOperationException(contextMessage, ex);
            }

            _logErrorAction?.Invoke(ex, "Error during ZipFs.InitializeEntries. Archive type: " + ArchiveType + ", Entries loaded: " + ArchiveEntries.Count + ".");
            throw;
        }
    }

    public bool IsStoredEntry(IArchiveEntry entry)
    {
        if (ArchiveType != "zip")
            return false;

        if (entry.IsDirectory || entry.IsEncrypted || entry.IsSolid || entry.Size <= 0)
            return false;

        return entry switch
        {
            ZipArchiveEntry ze => ze.CompressionType == CompressionType.None,
            _ => entry.CompressedSize == entry.Size
        };
    }

    public bool IsFailedEntry(string normalizedPath)
    {
        lock (_archiveLock)
        {
            return _failedEntries.Contains(normalizedPath);
        }
    }

    public void AddFailedEntry(string normalizedPath)
    {
        lock (_archiveLock)
        {
            _failedEntries.Add(normalizedPath);
        }
    }

    /// <summary>
    /// Gets an entry node for the given normalized path, or null if not found.
    /// </summary>
    public EntryNode? GetEntryNode(string normalizedPath)
    {
        IArchiveEntry? entry;
        bool isImplicitDir;
        var dirCreationTime = DateTime.Now;
        var dirLastWriteTime = DateTime.Now;
        var dirLastAccessTime = DateTime.Now;

        lock (_archiveLock)
        {
            ArchiveEntries.TryGetValue(normalizedPath, out entry);
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
            var canonicalPath = ZipFsHelpers.NormalizePath(entry.Key);
            if (ZipFsHelpers.IsDirectory(entry))
            {
                return new EntryNode
                {
                    NormalizedPath = normalizedPath,
                    CanonicalPath = canonicalPath,
                    IsDir = true,
                    Entry = entry,
                    FileSize = 0,
                    CreationTime = entry.CreatedTime ?? DateTime.Now,
                    LastWriteTime = entry.LastModifiedTime ?? DateTime.Now,
                    LastAccessTime = DateTime.Now
                };
            }
            else
            {
                return new EntryNode
                {
                    NormalizedPath = normalizedPath,
                    CanonicalPath = canonicalPath,
                    IsDir = false,
                    Entry = entry,
                    FileSize = entry.Size,
                    CreationTime = entry.CreatedTime ?? DateTime.Now,
                    LastWriteTime = entry.LastModifiedTime ?? DateTime.Now,
                    LastAccessTime = DateTime.Now
                };
            }
        }
        else if (isImplicitDir)
        {
            return new EntryNode
            {
                NormalizedPath = normalizedPath,
                CanonicalPath = normalizedPath,
                IsDir = true,
                Entry = null,
                FileSize = 0,
                CreationTime = dirCreationTime,
                LastWriteTime = dirLastWriteTime,
                LastAccessTime = dirLastAccessTime
            };
        }

        return null;
    }

    /// <summary>
    /// Resolves a raw file name to a normalized path and entry node.
    /// </summary>
    public bool TryResolvePath(string fileName, out string normalizedPath)
    {
        normalizedPath = ZipFsHelpers.NormalizePath(fileName);
        normalizedPath = ZipFsHelpers.ResolveSpecialPaths(normalizedPath);
        return true;
    }

    /// <summary>
    /// Lists the immediate children of a directory as <see cref="EntryNode"/> items.
    /// Returns an empty list if the path is not a directory.
    /// </summary>
    public List<EntryNode> ListDirectory(string normalizedPath)
    {
        var result = new List<EntryNode>();
        var seenFileNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        var isExplicitDirEntry = ArchiveEntries.TryGetValue(normalizedPath, out var dirEntry) && ZipFsHelpers.IsDirectory(dirEntry);
        var isImplicitDir = _directoryCreationTimes.ContainsKey(normalizedPath) || normalizedPath == "/";

        if (!isExplicitDirEntry && !isImplicitDir)
        {
            return result;
        }

        Thread.MemoryBarrier();

        var searchPrefix = normalizedPath == "/" ? "/" : normalizedPath.TrimEnd('/') + "/";

        foreach (var kvp in ArchiveEntries)
        {
            var path = kvp.Key;
            if (path.Equals(searchPrefix, StringComparison.OrdinalIgnoreCase)) continue;
            if (!path.StartsWith(searchPrefix, StringComparison.OrdinalIgnoreCase)) continue;

            var remainder = path.Substring(searchPrefix.Length);
            var slashIndex = remainder.IndexOf('/');
            if (slashIndex != -1)
            {
                if (!(remainder.EndsWith('/') && slashIndex == remainder.Length - 1))
                    continue;
            }

            var entry = kvp.Value;
            string? fileNameOnly = null;
            var isDir = ZipFsHelpers.IsDirectory(entry);

            if (isDir)
            {
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
                var canonicalPath = ZipFsHelpers.NormalizePath(entry.Key);
                result.Add(new EntryNode
                {
                    NormalizedPath = searchPrefix + fileNameOnly,
                    CanonicalPath = canonicalPath,
                    IsDir = isDir,
                    Entry = entry,
                    FileSize = isDir ? 0 : entry.Size,
                    CreationTime = entry.CreatedTime ?? DateTime.Now,
                    LastWriteTime = entry.LastModifiedTime ?? DateTime.Now,
                    LastAccessTime = DateTime.Now
                });
            }
        }

        List<KeyValuePair<string, DateTime>> dirSnapshot;
        lock (_archiveLock)
        {
            dirSnapshot = _directoryCreationTimes.ToList();
        }

        foreach (var dirKvp in dirSnapshot)
        {
            var dirPathKey = dirKvp.Key;
            if (dirPathKey.Equals(searchPrefix, StringComparison.OrdinalIgnoreCase)) continue;
            if (!dirPathKey.StartsWith(searchPrefix, StringComparison.OrdinalIgnoreCase)) continue;

            var remainder = dirPathKey.Substring(searchPrefix.Length);
            if (remainder.Contains('/') || string.IsNullOrEmpty(remainder)) continue;

            var name = dirPathKey.Split('/').LastOrDefault(static s => !string.IsNullOrEmpty(s));
            if (!string.IsNullOrEmpty(name) && seenFileNames.Add(name))
            {
                DateTime ct, lwt, lat;
                lock (_archiveLock)
                {
                    _directoryCreationTimes.TryGetValue(dirPathKey, out ct);
                    _directoryLastWriteTimes.TryGetValue(dirPathKey, out lwt);
                    _directoryLastAccessTimes.TryGetValue(dirPathKey, out lat);
                }

                var implicitPath = searchPrefix + name;
                result.Add(new EntryNode
                {
                    NormalizedPath = implicitPath,
                    CanonicalPath = implicitPath,
                    IsDir = true,
                    Entry = null,
                    FileSize = 0,
                    CreationTime = ct,
                    LastWriteTime = lwt,
                    LastAccessTime = lat
                });
            }
        }

        return result;
    }

    /// <summary>
    /// Opens a stream for reading an archive entry with caching.
    /// Returns null if the entry is in the failed list or if stream creation fails silently.
    /// May throw exceptions on extraction errors.
    /// </summary>
    public Stream? OpenEntryStream(IArchiveEntry entry, string normalizedPath)
    {
        if (IsFailedEntry(normalizedPath))
        {
            return null;
        }

        var entrySize = entry.Size;

        // Stored (uncompressed) entry fast path.
        if (IsStoredEntry(entry) && _sourceArchiveStream.CanSeek)
        {
            Stream? storedStream = null;
            lock (_archiveLock)
            {
                try
                {
                    using var entryStream = entry.OpenEntryStream();
                    var dataStart = _sourceArchiveStream.Position;
                    storedStream = new StoredEntryStream(_sourceArchiveStream, dataStart, entrySize, _archiveLock);
                }
                catch (Exception storedEx)
                {
                    ErrorLoggerStatic.ReportSilentException(storedEx, $"ZipFs.OpenEntryStream: StoredEntryStream creation failed for '{normalizedPath}'", true);
                }
            }

            if (storedStream != null)
            {
                LogMessage($"Stored entry detected: '{normalizedPath}' ({entrySize / 1024.0 / 1024.0:F2} MB). Using direct-read mode (no cache).");
                LogMessage("");
                return storedStream;
            }
        }

        // Large file: cache to disk.
        if (entrySize >= MaxMemorySize || entrySize < 0)
        {
            return OpenDiskCachedStream(entry, normalizedPath, entrySize, true);
        }

        // Small file: check memory limit.
        bool useDiskCache;
        lock (_memoryLock)
        {
            var projectedMemoryUsage = CurrentMemoryUsage + entrySize;
            useDiskCache = projectedMemoryUsage > MaxTotalMemoryCache;
        }

        if (useDiskCache)
        {
            LogMessage($"Memory limit approaching. Using disk cache for small file '{normalizedPath}' ({entrySize / 1024.0 / 1024.0:F2} MB).");
            return OpenDiskCachedStream(entry, normalizedPath, entrySize, false);
        }

        // Small file: cache in memory.
        byte[] entryBytes;
        lock (_archiveLock)
        {
            using var entryStream = entry.OpenEntryStream();
            var capacity = entrySize > 0 ? (int)Math.Min(entrySize, int.MaxValue) : 4096;
            using var tempMs = new MemoryStream(capacity);
            entryStream.CopyTo(tempMs);
            entryBytes = tempMs.ToArray();
        }

        lock (_memoryLock)
        {
            CurrentMemoryUsage += entryBytes.Length;
        }

        return new TrackedMemoryStream(entryBytes, _memoryLock, size =>
        {
            lock (_memoryLock)
            {
                CurrentMemoryUsage -= size;
                if (CurrentMemoryUsage < 0)
                {
                    CurrentMemoryUsage = 0;
                }
            }
        });
    }

    private FileStream? OpenDiskCachedStream(IArchiveEntry entry, string normalizedPath, long entrySize, bool isLargeFile)
    {
        string? cachedPath = null;
        lock (_archiveLock)
        {
            if (LargeFileCache.TryGetValue(normalizedPath, out var path))
            {
                cachedPath = path;
            }
        }

        if (cachedPath != null)
        {
            LogMessage($"Reusing existing temporary cache for '{normalizedPath}'.");
        }
        else
        {
            if (isLargeFile)
            {
                LogMessage($"Large file detected: '{normalizedPath}' ({entrySize / 1024.0 / 1024.0:F2} MB). Extracting to temporary disk cache...");
                LogMessage("");
            }

            var newTempFilePath = CreateSecureTempFile();

            if (entrySize >= 0)
            {
                try
                {
                    var tempDrivePathRoot = Path.GetPathRoot(newTempFilePath) ?? "C:\\";
                    var tempDrive = new DriveInfo(tempDrivePathRoot);
                    if (tempDrive.AvailableFreeSpace < entrySize)
                    {
                        try
                        {
                            File.Delete(newTempFilePath);
                        }
                        catch (Exception ex)
                        {
                            ErrorLoggerStatic.ReportSilentException(ex, $"ZipFs.OpenDiskCachedStream: Failed to delete temp file '{newTempFilePath}' during disk space check", true);
                        }

                        var errorMessage = $"Insufficient disk space to extract file '{normalizedPath}' ({entrySize / 1024.0 / 1024.0:F2} MB).";
                        _logErrorAction(new IOException(errorMessage), "ZipFs.OpenDiskCachedStream: Disk space check failed.");
                        return null;
                    }
                }
                catch (Exception driveEx)
                {
                    _logErrorAction(driveEx, $"Error checking disk space for file extraction of '{normalizedPath}'.");
                }
            }

            lock (_archiveLock)
            {
                if (LargeFileCache.TryGetValue(normalizedPath, out var existingPath))
                {
                    cachedPath = existingPath;
                    try
                    {
                        File.Delete(newTempFilePath);
                    }
                    catch
                    {
                        /* best effort */
                    }
                }
                else
                {
                    using var entryStream = entry.OpenEntryStream();
                    using var tempFileStream = new FileStream(newTempFilePath, FileMode.Truncate, FileAccess.Write, FileShare.None);
                    entryStream.CopyTo(tempFileStream);
                    LargeFileCache[normalizedPath] = newTempFilePath;
                    cachedPath = newTempFilePath;
                }
            }

            LogMessage($"Extraction complete for '{normalizedPath}'. Temp file: '{cachedPath}'");
        }

        if (string.IsNullOrEmpty(cachedPath))
        {
            _logErrorAction(new InvalidOperationException($"ZipFs.OpenDiskCachedStream: cachedPath is null/empty for file '{normalizedPath}' after caching attempt."), "ZipFs.OpenDiskCachedStream: Disk caching failed silently.");
            return null;
        }

        try
        {
            return new FileStream(cachedPath, FileMode.Open, FileAccess.Read, FileShare.Read);
        }
        catch (Exception fsEx)
        {
            _logErrorAction(fsEx, $"ZipFs.OpenDiskCachedStream: Failed to open cached temp file '{cachedPath}' for reading.");
            return null;
        }
    }

    /// <summary>
    /// Reads from a cached entry stream, handling StoredEntryStream and seekable/non-seekable streams.
    /// </summary>
    public int ReadStream(Stream stream, long offset, byte[] buffer, int bufferOffset, int count)
    {
        if (stream is StoredEntryStream stored)
        {
            return stored.ReadAt(offset, buffer, bufferOffset, count);
        }

        if (stream.CanSeek)
        {
            if (offset >= stream.Length)
            {
                return 0;
            }

            stream.Position = offset;
        }
        else
        {
            if (offset != stream.Position)
            {
                throw new InvalidOperationException("Non-sequential read requested for non-seekable stream.");
            }
        }

        return stream.Read(buffer, bufferOffset, count);
    }

    /// <summary>
    /// Validates path length and logs an error if it exceeds limits.
    /// </summary>
    public bool ValidatePathLength(string path, string operationName)
    {
        if (!ZipFsHelpers.IsPathLengthValid(path))
        {
            var isExtended = path.StartsWith(ZipFsHelpers.ExtendedPathPrefix, StringComparison.Ordinal);
            var maxLength = isExtended ? ZipFsHelpers.MaxPathExtended : ZipFsHelpers.MaxPath;
            var pathType = isExtended ? "extended-length" : "standard";

            _logErrorAction(
                new PathTooLongException($"Path exceeds maximum length for {pathType} paths ({maxLength} characters)."),
                $"ZipFs.{operationName}: Path length validation failed - {path.Length} characters.");

            return false;
        }

        return true;
    }

    internal static void LogMessage(string message)
    {
        try
        {
            var loggingService = ServiceProvider.TryGet<ILoggingService>();
            loggingService?.Log(message);
        }
        catch (Exception ex)
        {
            ErrorLoggerStatic.ReportSilentException(ex, "ZipFs.LogMessage: Logging service error", true);
        }
    }

    /// <summary>
    /// Creates a temporary file with restricted permissions accessible only to the current user.
    /// </summary>
    public string CreateSecureTempFile()
    {
        var tempFilePath = Path.Combine(TempDirectoryPath, Guid.NewGuid().ToString("N") + ".tmp");

        File.Create(tempFilePath).Dispose();

        try
        {
            var fileInfo = new FileInfo(tempFilePath);

            var fileSecurity = fileInfo.GetAccessControl();

            fileSecurity.SetAccessRuleProtection(true, false);

            var existingRules = fileSecurity.GetAccessRules(true, true, typeof(SecurityIdentifier));
            foreach (FileSystemAccessRule rule in existingRules)
            {
                fileSecurity.RemoveAccessRule(rule);
            }

            var currentUser = WindowsIdentity.GetCurrent();
            var currentUserSid = currentUser.User ?? throw new InvalidOperationException("Unable to get current user SID");

            var accessRule = new FileSystemAccessRule(
                currentUserSid,
                FileSystemRights.FullControl,
                AccessControlType.Allow);
            fileSecurity.AddAccessRule(accessRule);

            fileInfo.SetAccessControl(fileSecurity);
        }
        catch (PlatformNotSupportedException ex)
        {
            ErrorLoggerStatic.ReportSilentException(ex, $"ZipFs.CreateSecureTempFile: Platform not supported for ACL on '{tempFilePath}'", true);
        }
        catch (InvalidOperationException ex)
        {
            ErrorLoggerStatic.ReportSilentException(ex, $"ZipFs.CreateSecureTempFile: Invalid operation setting ACL on '{tempFilePath}'", true);
        }

        return tempFilePath;
    }

    public void DumpEntries(int maxEntries = 100)
    {
        try
        {
            DiagnosticLogger.LogHeader("ENTRY DUMP");
            DiagnosticLogger.Log($"  Total entries: {ArchiveEntries.Count}");
            DiagnosticLogger.Log($"  Implicit directories: {_directoryCreationTimes.Count}");
            DiagnosticLogger.Log($"  Failed entries: {_failedEntries.Count}");

            var count = 0;
            lock (_archiveLock)
            {
                foreach (var kvp in ArchiveEntries.OrderBy(static k => k.Key))
                {
                    if (count >= maxEntries)
                    {
                        DiagnosticLogger.Log($"  ... ({ArchiveEntries.Count - maxEntries} more entries)");
                        break;
                    }

                    var entry = kvp.Value;
                    var isDir = ZipFsHelpers.IsDirectory(entry);
                    var typeLabel = isDir ? "DIR" : "FILE";
                    var sizeStr = isDir ? "" : $" ({entry.Size / 1024.0:F1} KB)";
                    DiagnosticLogger.Log($"  [{typeLabel}] {kvp.Key}{sizeStr}");
                    count++;
                }
            }

            if (_directoryCreationTimes.Count > 0)
            {
                DiagnosticLogger.Log("  --- Implicit directories ---");
                count = 0;
                lock (_archiveLock)
                {
                    foreach (var kvp in _directoryCreationTimes.OrderBy(static k => k.Key))
                    {
                        if (count >= maxEntries) break;

                        DiagnosticLogger.Log($"  [IMPLICIT] {kvp.Key}");
                        count++;
                    }
                }
            }

            if (_failedEntries.Count > 0)
            {
                DiagnosticLogger.Log("  --- Failed entries ---");
                string[] failedSnapshot;
                lock (_archiveLock)
                {
                    failedSnapshot = _failedEntries.ToArray();
                }

                foreach (var failed in failedSnapshot)
                {
                    DiagnosticLogger.Log($"  [FAILED] {failed}");
                }
            }

            DiagnosticLogger.LogHeader("END ENTRY DUMP");
        }
        catch (Exception ex)
        {
            DiagnosticLogger.Log(ex, "DumpEntries failed");
            _logErrorAction(ex, "ZipFs.DumpEntries failed");
        }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposedInt, 1) != 0)
            return;

        DiagnosticLogger.LogHeader("ZipFs DISPOSE");

        _archive.Dispose();
        _sourceArchiveStream.Dispose();

        lock (_archiveLock)
        {
            foreach (var tempFile in LargeFileCache.Values)
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
                    try
                    {
                        _logErrorAction(ex, $"Failed to delete temp file on dispose: {tempFile}");
                    }
                    catch
                    {
                        // Best-effort logging during disposal; ignore failures.
                    }
                }
            }

            LargeFileCache.Clear();
        }

        try
        {
            if (Directory.Exists(TempDirectoryPath))
            {
                Directory.Delete(TempDirectoryPath, true);
            }
        }
        catch (Exception ex)
        {
            try
            {
                _logErrorAction(ex, $"Failed to delete working directory on dispose: {TempDirectoryPath}");
            }
            catch
            {
                // Best-effort logging during disposal; ignore failures.
            }
        }

        lock (_memoryLock)
        {
            CurrentMemoryUsage = 0;
        }

        DiagnosticLogger.LogHeader("ZipFs DISPOSE complete");
        GC.SuppressFinalize(this);
    }
}

/// <summary>
/// Represents a resolved filesystem entry (file or directory) within the archive.
/// </summary>
public sealed class EntryNode
{
    public string NormalizedPath { get; set; } = null!;
    public string CanonicalPath { get; set; } = null!;
    public bool IsDir { get; set; }
    public IArchiveEntry? Entry { get; set; }
    public long FileSize { get; set; }
    public DateTime CreationTime { get; set; }
    public DateTime LastWriteTime { get; set; }
    public DateTime LastAccessTime { get; set; }
}
