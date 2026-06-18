using System.Security.AccessControl;
using System.Security.Principal;
using SharpCompress.Archives;
using SharpCompress.Archives.Rar;
using SharpCompress.Archives.SevenZip;
using SharpCompress.Archives.Tar;
using SharpCompress.Archives.Zip;
using SharpCompress.Common;
using SharpCompress.Compressors.Deflate;
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

    // Per-entry semaphores for extraction synchronization (Fix: reduce global lock contention).
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _entryLocks = new(StringComparer.OrdinalIgnoreCase);

    // Cache for entries that failed to decompress.
    private readonly HashSet<string> _failedEntries = new(StringComparer.OrdinalIgnoreCase);

    // Memory throttling for small files.
    internal readonly long MaxTotalMemoryCache;
    internal long CurrentMemoryUsage;
    private readonly object _memoryLock = new();

    private readonly Func<string?> _passwordProvider;
    private readonly SevenZipFallback? _sevenZipFallback;
    private readonly string? _archiveFilePath;
    private int _disposedInt;

    /// <summary>Default volume label displayed in Windows Explorer.</summary>
    public const string DefaultVolumeLabel = "SimpleZipDrive";

    /// <summary>Default maximum size (512 MB) for in-memory caching of a single archive entry.</summary>
    public const long DefaultMaxMemorySize = 512L * 1024 * 1024;

    /// <summary>Gets the volume label displayed for the mounted archive.</summary>
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
        TempDirectoryPath = Path.Combine(ZipFsHelpers.BaseTempPath, tempDirName);
        ZipFsHelpers.RegisterCurrentTempDirectory(tempDirName);

        try
        {
            Directory.CreateDirectory(TempDirectoryPath);

            // Get file path for SevenZip fallback (if stream is a FileStream)
            if (archiveStream is FileStream fs)
            {
                _archiveFilePath = fs.Name;
            }

            if (archiveStream.CanSeek)
            {
                archiveStream.Position = 0;
            }

            _archive = OpenArchive(archiveStream);
            InitializeEntries();

            // Initialize SevenZip fallback if 7z.dll is available
            if (_archiveFilePath != null && SevenZipFallback.IsAvailable())
            {
                _sevenZipFallback = new SevenZipFallback(_archiveFilePath, _passwordProvider);
            }

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

    /// <summary>Gets a value indicating whether this instance has been disposed.</summary>
    public bool IsDisposed => Volatile.Read(ref _disposedInt) != 0;

    /// <summary>Gets the archive type identifier (e.g., "zip", "7z", "rar").</summary>
    public string ArchiveType { get; }

    /// <summary>Gets the path to the temporary directory used for disk-cached entries.</summary>
    public string TempDirectoryPath { get; }

    /// <summary>Gets the maximum size (in bytes) of a single entry that can be cached in memory.</summary>
    public long MaxMemorySize { get; }

    /// <summary>Gets the total size in bytes of the source archive stream, or 0 if the stream is not seekable.</summary>
    public long TotalSize => _sourceArchiveStream.CanSeek ? _sourceArchiveStream.Length : 0;

    private IArchive OpenArchive(Stream stream)
    {
        // Strategy: Try to open and USE the archive without a password first.
        // Only prompt for password if we actually hit a decryption failure.

        try
        {
            var archiveWithoutPassword = ArchiveType switch
            {
                "zip" => ZipArchive.OpenArchive(stream, new ReaderOptions { LeaveStreamOpen = true }),
                "7z" => SevenZipArchive.OpenArchive(stream, new ReaderOptions { LeaveStreamOpen = true }),
                "rar" => RarArchive.OpenArchive(stream, new ReaderOptions { LeaveStreamOpen = true }),
                "tar" => TarArchive.OpenArchive(stream, new ReaderOptions { LeaveStreamOpen = true }),
                _ => throw new NotSupportedException($"Archive type '{ArchiveType}' is not supported.")
            };

            // Try to verify the archive is usable without a password by reading a file entry
            if (IsArchiveUsableWithoutPassword(archiveWithoutPassword))
            {
                return archiveWithoutPassword;
            }

            // Archive is genuinely encrypted - dispose and fall through to password prompt
            archiveWithoutPassword.Dispose();
        }
        catch (InvalidOperationException)
        {
            throw;
        }
        catch (NotSupportedException)
        {
            throw;
        }
        catch (Exception ex) when (!ZipFsHelpers.IsPasswordRequiredException(ex) && !IsCryptoException(ex))
        {
            throw new InvalidOperationException(
                "The archive file appears to be corrupted, incomplete, or uses an unsupported format/feature that could not be parsed.", ex);
        }
        catch (Exception)
        {
            // Password-related or crypto exception during open - fall through to password prompt
        }

        if (stream.CanSeek)
        {
            stream.Position = 0;
        }

        var password = _passwordProvider();

        return ArchiveType switch
        {
            "zip" => ZipArchive.OpenArchive(stream, new ReaderOptions { Password = password, LeaveStreamOpen = true }),
            "7z" => SevenZipArchive.OpenArchive(stream, new ReaderOptions { Password = password, LeaveStreamOpen = true }),
            "rar" => RarArchive.OpenArchive(stream, new ReaderOptions { Password = password, LeaveStreamOpen = true }),
            "tar" => TarArchive.OpenArchive(stream, new ReaderOptions { Password = password, LeaveStreamOpen = true }),
            _ => throw new NotSupportedException($"Archive type '{ArchiveType}' is not supported.")
        };
    }

    private static bool IsCryptoException(Exception ex)
    {
        return ex is System.Security.Cryptography.CryptographicException ||
               ex.GetType().Name.Contains("CryptographicException", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Verifies if an archive can be used without a password by trying to read a file entry.
    /// Returns true if the archive is usable without a password, false if it's genuinely encrypted.
    /// </summary>
    private static bool IsArchiveUsableWithoutPassword(IArchive archive)
    {
        try
        {
            // First check: Does the IsEncrypted flag indicate encryption?
            bool hasEncryptedFlag;
            try
            {
                hasEncryptedFlag = archive.Entries.Any(static e => e.IsEncrypted);
            }
            catch (Exception ex) when (ZipFsHelpers.IsPasswordRequiredException(ex) || IsCryptoException(ex))
            {
                // Enumerating entries itself requires password (e.g., RAR encrypted headers)
                return false;
            }

            if (!hasEncryptedFlag)
            {
                // No entries marked as encrypted - archive is usable
                return true;
            }

            // Second check: Try to actually read a file entry to verify encryption is real
            // Some zip tools incorrectly set the encryption flag
            var testEntry = archive.Entries.FirstOrDefault(e => e is { IsDirectory: false, Size: > 0 });
            if (testEntry == null)
            {
                // No file entries to test - trust the flag
                return false;
            }

            using var entryStream = testEntry.OpenEntryStream();
            var buffer = new byte[Math.Min(1024, testEntry.Size)];
            var bytesRead = entryStream.Read(buffer, 0, buffer.Length);

            // If we can read bytes, the entry is not actually encrypted
            return bytesRead > 0;
        }
        catch (Exception ex) when (ZipFsHelpers.IsPasswordRequiredException(ex) || IsCryptoException(ex))
        {
            // Password-related or crypto exception confirms encryption
            return false;
        }
        catch
        {
            // Other errors - trust the encryption flag
            return false;
        }
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

    /// <summary>
    /// Determines whether the specified archive entry is stored (uncompressed) and can be
    /// read directly from the source stream without extraction.
    /// </summary>
    /// <param name="entry">The archive entry to check.</param>
    /// <returns><see langword="true"/> if the entry is stored uncompressed in a seekable zip archive; otherwise, <see langword="false"/>.</returns>
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

    /// <summary>
    /// Determines whether the specified entry has previously failed to decompress and is
    /// excluded from further open attempts.
    /// </summary>
    /// <param name="normalizedPath">The normalized archive path of the entry.</param>
    /// <returns><see langword="true"/> if the entry is in the failed list; otherwise, <see langword="false"/>.</returns>
    public bool IsFailedEntry(string normalizedPath)
    {
        lock (_archiveLock)
        {
            return _failedEntries.Contains(normalizedPath);
        }
    }

    /// <summary>
    /// Marks an entry as failed so subsequent open attempts return immediately without retrying extraction.
    /// </summary>
    /// <param name="normalizedPath">The normalized archive path of the entry to mark as failed.</param>
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
    /// Returns null only if the entry is in the failed list.
    /// Throws <see cref="IOException"/> on disk space or cache file errors.
    /// May throw other exceptions on extraction errors.
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
        try
        {
            lock (_archiveLock)
            {
                using var entryStream = entry.OpenEntryStream();
                var capacity = entrySize > 0 ? (int)Math.Min(entrySize, int.MaxValue) : 4096;
                using var tempMs = new MemoryStream(capacity);
                entryStream.CopyTo(tempMs);
                entryBytes = tempMs.ToArray();
            }
        }
        catch (Exception ex) when (ex is OutOfMemoryException or ZlibException)
        {
            LogMessage($"Memory cache failed for '{normalizedPath}' ({ex.GetType().Name}): falling back to disk cache.");
            return OpenDiskCachedStream(entry, normalizedPath, entrySize, false);
        }
        catch (Exception ex) when (IsExtractionFailure(ex))
        {
            var fallback = TryFallbackExtraction(normalizedPath, entrySize, false);
            if (fallback != null)
                return fallback;

            LogMessage($"Extraction failed for '{normalizedPath}' ({ex.GetType().Name}), no fallback available.");
            AddFailedEntry(normalizedPath);
            return null;
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

    private FileStream OpenDiskCachedStream(IArchiveEntry entry, string normalizedPath, long entrySize, bool isLargeFile)
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
            var entrySemaphore = _entryLocks.GetOrAdd(normalizedPath, static _ => new SemaphoreSlim(1, 1));
            entrySemaphore.Wait();
            try
            {
                // Double-check after acquiring per-entry lock.
                lock (_archiveLock)
                {
                    if (LargeFileCache.TryGetValue(normalizedPath, out var existingPath))
                    {
                        cachedPath = existingPath;
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

                                var errorMessage = $"Insufficient disk space to extract file '{normalizedPath}' ({entrySize / 1024.0 / 1024.0:F2} MB). Available: {tempDrive.AvailableFreeSpace / 1024.0 / 1024.0:F2} MB, Required: {entrySize / 1024.0 / 1024.0:F2} MB.";
                                throw new IOException(errorMessage);
                            }
                        }
                        catch (IOException)
                        {
                            throw;
                        }
                        catch (Exception driveEx)
                        {
                            _logErrorAction(driveEx, $"Error checking disk space for file extraction of '{normalizedPath}'.");
                        }
                    }

                    // Extract outside the global archive lock — only per-entry lock is held.
                    ExtractEntryToDisk(entry, normalizedPath, newTempFilePath);

                    lock (_archiveLock)
                    {
                        LargeFileCache[normalizedPath] = newTempFilePath;
                    }

                    cachedPath = newTempFilePath;
                    LogMessage($"Extraction complete for '{normalizedPath}'. Temp file: '{cachedPath}'");
                }
            }
            finally
            {
                entrySemaphore.Release();
            }
        }

        if (string.IsNullOrEmpty(cachedPath))
        {
            throw new IOException($"Disk caching failed for file '{normalizedPath}'. The cached path is unexpectedly null after extraction.");
        }

        try
        {
            return new FileStream(cachedPath, FileMode.Open, FileAccess.Read, FileShare.Read);
        }
        catch (Exception fsEx)
        {
            throw new IOException($"Failed to open cached temp file '{cachedPath}' for reading file '{normalizedPath}'.", fsEx);
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

    /// <summary>
    /// Determines whether an exception indicates an extraction failure that should trigger the fallback.
    /// </summary>
    private static bool IsExtractionFailure(Exception ex)
    {
        return ex is ZlibException
                   or ArgumentOutOfRangeException
                   or NullReferenceException
                   or InvalidOperationException
               || ZipFsHelpers.IsDataErrorException(ex);
    }

    /// <summary>
    /// Extracts an entry to a disk file, trying SharpCompress first and falling back to SevenZip on failure.
    /// Throws if both extractors fail.
    /// </summary>
    private void ExtractEntryToDisk(IArchiveEntry entry, string normalizedPath, string tempFilePath)
    {
        try
        {
            using var entryStream = entry.OpenEntryStream();
            using var tempFileStream = new FileStream(tempFilePath, FileMode.Truncate, FileAccess.Write, FileShare.None);
            entryStream.CopyTo(tempFileStream);
        }
        catch (Exception ex) when (IsExtractionFailure(ex))
        {
            if (_sevenZipFallback != null)
            {
                try
                {
                    using var fallbackOutput = new FileStream(tempFilePath, FileMode.Truncate, FileAccess.Write, FileShare.None);
                    if (_sevenZipFallback.TryExtractEntry(normalizedPath, fallbackOutput))
                        return;
                }
                catch (Exception fallbackEx)
                {
                    ErrorLoggerStatic.ReportSilentException(fallbackEx, $"ZipFs.ExtractEntryToDisk: SevenZip fallback failed for '{normalizedPath}'", true);
                }
            }

            CleanupTempFile(tempFilePath);
            throw;
        }
        catch
        {
            CleanupTempFile(tempFilePath);
            throw;
        }
    }

    /// <summary>
    /// Tries to extract an entry using the SevenZip fallback to a memory or disk cached stream.
    /// Returns the stream if successful, null otherwise.
    /// </summary>
    private Stream? TryFallbackExtraction(string normalizedPath, long entrySize, bool isLargeFile)
    {
        if (_sevenZipFallback == null)
            return null;

        try
        {
            // Large file or unknown size: use disk cache
            if (entrySize >= MaxMemorySize || entrySize < 0 || isLargeFile)
            {
                var tempFilePath = CreateSecureTempFile();
                using (var outputStream = new FileStream(tempFilePath, FileMode.Truncate, FileAccess.Write, FileShare.None))
                {
                    if (!_sevenZipFallback.TryExtractEntry(normalizedPath, outputStream))
                    {
                        CleanupTempFile(tempFilePath);
                        return null;
                    }
                }

                lock (_archiveLock)
                {
                    LargeFileCache[normalizedPath] = tempFilePath;
                }

                return new FileStream(tempFilePath, FileMode.Open, FileAccess.Read, FileShare.Read);
            }

            // Small file: use memory cache
            using var ms = new MemoryStream();
            if (!_sevenZipFallback.TryExtractEntry(normalizedPath, ms))
                return null;

            var entryBytes = ms.ToArray();
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
        catch
        {
            return null;
        }
    }

    private static void CleanupTempFile(string tempFilePath)
    {
        try
        {
            File.Delete(tempFilePath);
        }
        catch (Exception cleanupEx)
        {
            ErrorLoggerStatic.ReportSilentException(cleanupEx, $"ZipFs.CleanupTempFile: Failed to delete '{tempFilePath}'", true);
        }
    }

    /// <summary>
    /// Dumps a diagnostic listing of all archive entries, implicit directories, and failed entries
    /// to the <see cref="DiagnosticLogger"/> output.
    /// </summary>
    /// <param name="maxEntries">Maximum number of entries to display per category.</param>
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

    /// <summary>
    /// Releases all resources used by the <see cref="ZipFileSystemCore"/>, including the archive,
    /// source stream, cached temp files, and the temporary directory.
    /// </summary>
    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposedInt, 1) != 0)
            return;

        DiagnosticLogger.LogHeader("ZipFs DISPOSE");

        _sevenZipFallback?.Dispose();
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

        foreach (var semaphore in _entryLocks.Values)
        {
            semaphore.Dispose();
        }

        _entryLocks.Clear();

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
    /// <summary>Gets or sets the normalized forward-slash-separated path (e.g., "/folder/file.txt").</summary>
    public string NormalizedPath { get; set; } = string.Empty;

    /// <summary>Gets or sets the canonical path as it appears in the archive entry key.</summary>
    public string CanonicalPath { get; set; } = string.Empty;

    /// <summary>Gets or sets a value indicating whether this node represents a directory.</summary>
    public bool IsDir { get; set; }

    /// <summary>Gets or sets the underlying archive entry, or <see langword="null"/> for implicit directories.</summary>
    public IArchiveEntry? Entry { get; set; }

    /// <summary>Gets or sets the uncompressed file size in bytes (0 for directories).</summary>
    public long FileSize { get; set; }

    /// <summary>Gets or sets the creation timestamp of this entry.</summary>
    public DateTime CreationTime { get; set; }

    /// <summary>Gets or sets the last-modified timestamp of this entry.</summary>
    public DateTime LastWriteTime { get; set; }

    /// <summary>Gets or sets the last-accessed timestamp of this entry.</summary>
    public DateTime LastAccessTime { get; set; }
}
