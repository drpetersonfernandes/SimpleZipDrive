using System.Diagnostics.CodeAnalysis;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text.RegularExpressions;
using Fsp;
using Fsp.Interop;
using Microsoft.Win32.SafeHandles;
using SharpCompress.Archives;
using SharpCompress.Archives.Rar;
using SharpCompress.Archives.SevenZip;
using SharpCompress.Archives.Zip;
using SharpCompress.Common;
using SharpCompress.Compressors.Deflate;
using SharpCompress.Readers;

namespace SimpleZipDrive_WinFsp;

[SuppressMessage("ReSharper", "InconsistentNaming")]
public sealed class ZipFs : FileSystemBase, IDisposable
{
    private static readonly ConcurrentDictionary<string, Regex> RegexCache = new();
    private const int MaxRegexCacheSize = 100;

    private readonly Stream _sourceArchiveStream;
    private readonly IArchive _archive;
    private readonly Dictionary<string, IArchiveEntry> _archiveEntries = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, DateTime> _directoryCreationTimes = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, DateTime> _directoryLastWriteTimes = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, DateTime> _directoryLastAccessTimes = new(StringComparer.OrdinalIgnoreCase);

    private readonly Action<Exception?, string?> _logErrorAction;
    private readonly object _archiveLock = new();

    private readonly Dictionary<string, string> _largeFileCache = new(StringComparer.OrdinalIgnoreCase);
    private readonly long _maxMemorySize;

    private readonly HashSet<string> _failedEntries = new(StringComparer.OrdinalIgnoreCase);

    private readonly long _maxTotalMemoryCache;
    private long _currentMemoryUsage;
    private readonly object _memoryLock = new();

    private readonly string _tempDirectoryPath;
    private readonly Func<string?> _passwordProvider;
    private readonly string _archiveType;
    private volatile bool _disposed;

    private const string VolumeLabelText = "SimpleZipDrive";
    private const long DefaultMaxMemorySize = 512L * 1024 * 1024;
    private static readonly char[] Separator = ['/'];

    private const int MaxPath = 260;
    private const int MaxPathExtended = 32767;
    private const string ExtendedPathPrefix = @"\\?\";

    private static int _cleanupPerformed;

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
                    var dirName = Path.GetFileName(dir);
                    var underscoreIndex = dirName.IndexOf('_');
                    if (underscoreIndex <= 0)
                        continue;

                    if (!int.TryParse(dirName.AsSpan(0, underscoreIndex), out var pid))
                        continue;

                    bool processExists;
                    try
                    {
                        _ = System.Diagnostics.Process.GetProcessById(pid);
                        processExists = true;
                    }
                    catch (ArgumentException)
                    {
                        processExists = false;
                    }

                    if (!processExists)
                    {
                        Directory.Delete(dir, true);
                    }
                }
                catch (Exception ex)
                {
                    ErrorLoggerStatic.ReportSilentException(ex, $"ZipFs.CleanupOrphanedTempDirectories: Error cleaning directory '{dir}'", true);
                }
            }
        }
        catch (Exception ex)
        {
            ErrorLoggerStatic.ReportSilentException(ex, "ZipFs.CleanupOrphanedTempDirectories: Error during cleanup", true);
        }
    }

    public ZipFs(Stream archiveStream, string mountPoint, Action<Exception?, string?> logErrorAction, Func<string?> passwordProvider, string archiveType, long maxMemorySize = DefaultMaxMemorySize)
    {
        if (Interlocked.Exchange(ref _cleanupPerformed, 1) == 0)
        {
            CleanupOrphanedTempDirectories();
        }

        _sourceArchiveStream = archiveStream;
        _logErrorAction = logErrorAction;
        _passwordProvider = passwordProvider;
        _archiveType = archiveType.ToLowerInvariant();
        _maxMemorySize = maxMemorySize;
        var availableMemory = GC.GetGCMemoryInfo().TotalAvailableMemoryBytes;
        _maxTotalMemoryCache = (long)(availableMemory * 0.90);
        _tempDirectoryPath = Path.Combine(Path.GetTempPath(), "SimpleZipDrive", $"{Environment.ProcessId}_{Guid.NewGuid():N}");

        try
        {
            Directory.CreateDirectory(_tempDirectoryPath);

            if (archiveStream.CanSeek)
            {
                archiveStream.Position = 0;
            }

            _archive = OpenArchive(archiveStream);
            InitializeEntries();
        }
        catch (Exception ex)
        {
            _logErrorAction(ex, $"Error during ZipFs construction for mount point '{mountPoint}'.");
            throw;
        }
    }

    private IArchive OpenArchive(Stream stream)
    {
        try
        {
            var archiveWithoutPassword = _archiveType switch
            {
                "zip" => ZipArchive.OpenArchive(stream, new ReaderOptions()),
                "7z" => SevenZipArchive.OpenArchive(stream, new ReaderOptions()),
                "rar" => RarArchive.OpenArchive(stream, new ReaderOptions()),
                _ => throw new NotSupportedException($"Archive type '{_archiveType}' is not supported.")
            };

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

            archiveWithoutPassword.Dispose();
        }
        catch (Exception ex) when (IsPasswordRequiredException(ex))
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
        return entry.IsDirectory || (entry.Key != null && (entry.Key.EndsWith('/') || entry.Key.EndsWith('\\')));
    }

    private bool IsStoredEntry(IArchiveEntry entry)
    {
        if (_archiveType != "zip")
            return false;

        if (entry.IsDirectory || entry.IsEncrypted || entry.IsSolid || entry.Size <= 0)
            return false;

        return entry switch
        {
            ZipArchiveEntry ze => ze.CompressionType == CompressionType.None,
            _ => entry.CompressedSize == entry.Size
        };
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

    private static void LogMessage(string message)
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

    private static bool IsPathLengthValid(string path)
    {
        if (string.IsNullOrEmpty(path))
            return true;

        if (path.StartsWith(ExtendedPathPrefix, StringComparison.Ordinal))
        {
            return path.Length <= MaxPathExtended;
        }

        return path.Length <= MaxPath;
    }

    private int ValidatePathLength(string path, string operationName)
    {
        if (!IsPathLengthValid(path))
        {
            var isExtended = path.StartsWith(ExtendedPathPrefix, StringComparison.Ordinal);
            var maxLength = isExtended ? MaxPathExtended : MaxPath;
            var pathType = isExtended ? "extended-length" : "standard";

            _logErrorAction(
                new PathTooLongException($"Path exceeds maximum length for {pathType} paths ({maxLength} characters)."),
                $"ZipFs.{operationName}: Path length validation failed - {path.Length} characters.");

            return STATUS_UNSUCCESSFUL;
        }

        return STATUS_SUCCESS;
    }

    private sealed class EntryNode
    {
        public string NormalizedPath = null!;
        public bool IsDir;
        public IArchiveEntry? Entry;
        public long FileSize;
        public DateTime CreationTime;
        public DateTime LastWriteTime;
        public DateTime LastAccessTime;
    }

    private EntryNode? GetEntryNode(string normalizedPath)
    {
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
                return new EntryNode
                {
                    NormalizedPath = normalizedPath,
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

    public override int Create(
        string FileName,
        uint CreateOptions,
        uint GrantedAccess,
        uint FileAttributes,
        byte[] SecurityDescriptor,
        ulong AllocationSize,
        out object FileNode,
        out object FileDesc,
        out Fsp.Interop.FileInfo FileInfo,
        out string NormalizedName)
    {
        return OpenOrCreateFile(FileName, out FileNode, out FileDesc, out FileInfo, out NormalizedName);
    }

    public override int Open(
        string FileName,
        uint CreateOptions,
        uint GrantedAccess,
        out object FileNode,
        out object FileDesc,
        out Fsp.Interop.FileInfo FileInfo,
        out string NormalizedName)
    {
        return OpenOrCreateFile(FileName, out FileNode, out FileDesc, out FileInfo, out NormalizedName);
    }

    public override int Overwrite(
        object FileNode,
        object FileDesc,
        uint FileAttributes,
        bool ReplaceFileAttributes,
        ulong AllocationSize,
        out Fsp.Interop.FileInfo FileInfo)
    {
        FileInfo = default;
        return STATUS_ACCESS_DENIED;
    }

    private int OpenOrCreateFile(
        string fileName,
        out object fileNode,
        out object fileDesc,
        out Fsp.Interop.FileInfo fileInfo,
        out string normalizedName)
    {
        fileNode = null!;
        fileDesc = null!;
        fileInfo = default;
        normalizedName = fileName;

        var pathValidationResult = ValidatePathLength(fileName, nameof(OpenOrCreateFile));
        if (pathValidationResult != STATUS_SUCCESS)
            return pathValidationResult;

        if (_disposed)
            return STATUS_DEVICE_NOT_READY;

        var normalizedPath = NormalizePath(fileName);
        normalizedName = normalizedPath;

        var node = GetEntryNode(normalizedPath);
        if (node == null)
        {
            if (fileName.Equals("\\", StringComparison.OrdinalIgnoreCase) || normalizedPath == "/")
            {
                node = new EntryNode
                {
                    NormalizedPath = "/",
                    IsDir = true,
                    CreationTime = DateTime.Now,
                    LastWriteTime = DateTime.Now,
                    LastAccessTime = DateTime.Now
                };
            }
            else
            {
                return STATUS_OBJECT_NAME_NOT_FOUND;
            }
        }

        if (node.IsDir)
        {
            fileNode = node;
            fileInfo = EntryNodeToFileInfo(node);
            return STATUS_SUCCESS;
        }

        if (node.Entry == null)
            return STATUS_OBJECT_NAME_NOT_FOUND;

        var entry = node.Entry;

        lock (_archiveLock)
        {
            if (_failedEntries.Contains(normalizedPath))
            {
                return STATUS_UNSUCCESSFUL;
            }
        }

        try
        {
            var entrySize = entry.Size;

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
                    catch
                    {
                        // ignored
                    }
                }

                if (storedStream != null)
                {
                    fileNode = node;
                    fileDesc = storedStream;
                    fileInfo = EntryNodeToFileInfo(node);
                    LogMessage($"Stored entry detected: '{normalizedPath}' ({entrySize / 1024.0 / 1024.0:F2} MB). Using direct-read mode (no cache).");
                    LogMessage("");
                    return STATUS_SUCCESS;
                }
            }

            if (entrySize >= _maxMemorySize || entrySize < 0)
            {
                string? cachedPath = null;
                lock (_archiveLock)
                {
                    if (_largeFileCache.TryGetValue(normalizedPath, out var path))
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
                    LogMessage($"Large file detected: '{normalizedPath}' ({entrySize / 1024.0 / 1024.0:F2} MB). Extracting to temporary disk cache...");
                    LogMessage("");
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
                                    ErrorLoggerStatic.ReportSilentException(ex, $"ZipFs.Create: Failed to delete temp file '{newTempFilePath}' during disk space check", true);
                                }

                                var errorMessage = $"Insufficient disk space to extract large file '{normalizedPath}' ({entrySize / 1024.0 / 1024.0:F2} MB).";
                                _logErrorAction(new IOException(errorMessage), "ZipFs.Create: Disk space check failed.");
                                return STATUS_DISK_FULL;
                            }
                        }
                        catch (Exception driveEx)
                        {
                            _logErrorAction(driveEx, $"Error checking disk space for large file extraction of '{normalizedPath}'.");
                        }
                    }

                    lock (_archiveLock)
                    {
                        if (_largeFileCache.TryGetValue(normalizedPath, out var existingPath))
                        {
                            cachedPath = existingPath;
                            try
                            {
                                File.Delete(newTempFilePath);
                            }
                            catch
                            {
                                // ignored
                            }
                        }
                        else
                        {
                            using var entryStream = entry.OpenEntryStream();
                            using var tempFileStream = new FileStream(newTempFilePath, FileMode.Truncate, FileAccess.Write, FileShare.None);
                            entryStream.CopyTo(tempFileStream);
                            _largeFileCache[normalizedPath] = newTempFilePath;
                            cachedPath = newTempFilePath;
                        }
                    }

                    LogMessage($"Extraction complete for '{normalizedPath}'. Temp file: '{cachedPath}'");
                }

                if (!string.IsNullOrEmpty(cachedPath))
                {
                    try
                    {
                        fileNode = node;
                        fileDesc = new FileStream(cachedPath, FileMode.Open, FileAccess.Read, FileShare.Read);
                        fileInfo = EntryNodeToFileInfo(node);
                        return STATUS_SUCCESS;
                    }
                    catch (Exception fsEx)
                    {
                        _logErrorAction(fsEx, $"ZipFs.Create: Failed to open cached temp file '{cachedPath}' for reading.");
                        return STATUS_UNSUCCESSFUL;
                    }
                }
                else
                {
                    _logErrorAction(new InvalidOperationException($"ZipFs.Create: cachedPath is null/empty for large file '{normalizedPath}' after caching attempt."), "ZipFs.Create: Large file caching failed silently.");
                    return STATUS_UNSUCCESSFUL;
                }
            }
            else
            {
                bool useDiskCache;
                lock (_memoryLock)
                {
                    var projectedMemoryUsage = _currentMemoryUsage + entrySize;
                    useDiskCache = projectedMemoryUsage > _maxTotalMemoryCache;
                }

                if (useDiskCache)
                {
                    LogMessage($"Memory limit approaching. Using disk cache for small file '{normalizedPath}' ({entrySize / 1024.0 / 1024.0:F2} MB).");

                    string? cachedPath = null;
                    lock (_archiveLock)
                    {
                        if (_largeFileCache.TryGetValue(normalizedPath, out var path))
                        {
                            cachedPath = path;
                        }
                    }

                    if (cachedPath == null)
                    {
                        var newTempFilePath = CreateSecureTempFile();

                        lock (_archiveLock)
                        {
                            if (_largeFileCache.TryGetValue(normalizedPath, out var existingPath))
                            {
                                cachedPath = existingPath;
                                try
                                {
                                    File.Delete(newTempFilePath);
                                }
                                catch
                                {
                                    // ignored
                                }
                            }
                            else
                            {
                                using var entryStream = entry.OpenEntryStream();
                                using var tempFileStream = new FileStream(newTempFilePath, FileMode.Truncate, FileAccess.Write, FileShare.None);
                                entryStream.CopyTo(tempFileStream);
                                _largeFileCache[normalizedPath] = newTempFilePath;
                                cachedPath = newTempFilePath;
                            }
                        }
                    }

                    if (!string.IsNullOrEmpty(cachedPath))
                    {
                        try
                        {
                            fileNode = node;
                            fileDesc = new FileStream(cachedPath, FileMode.Open, FileAccess.Read, FileShare.Read);
                            fileInfo = EntryNodeToFileInfo(node);
                            return STATUS_SUCCESS;
                        }
                        catch (Exception fsEx)
                        {
                            _logErrorAction(fsEx, $"ZipFs.Create: Failed to open disk-cached temp file '{cachedPath}' for reading.");
                            return STATUS_UNSUCCESSFUL;
                        }
                    }
                }
                else
                {
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
                        _currentMemoryUsage += entryBytes.Length;
                    }

                    fileNode = node;
                    fileDesc = new TrackedMemoryStream(entryBytes, this);
                    fileInfo = EntryNodeToFileInfo(node);
                    return STATUS_SUCCESS;
                }

                fileNode = null!;
                fileDesc = null!;
                return STATUS_UNSUCCESSFUL;
            }
        }
        catch (CryptographicException cryptoEx)
        {
            var contextMessage = $"ZipFs.Create: Password error for '{normalizedPath}'. The provided password may be incorrect or missing.";
            LogMessage($"{AppTheme.Warning} Password Error: Could not decrypt '{normalizedPath}'.");
            _logErrorAction(cryptoEx, contextMessage);
            fileNode = null!;
            fileDesc = null!;
            return STATUS_UNSUCCESSFUL;
        }
        catch (IOException ioEx) when ((uint)ioEx.HResult == 0x80070015)
        {
            var msg = $"CRITICAL ERROR: The source drive containing the archive file is no longer ready. " +
                      $"Please check the connection to drive '{Path.GetPathRoot(_tempDirectoryPath)}'.";
            LogMessage($"{AppTheme.Critical} {msg}");
            fileNode = null!;
            fileDesc = null!;
            return STATUS_DEVICE_NOT_READY;
        }
        catch (IOException ioEx) when ((uint)ioEx.HResult == 0x800703EE || (uint)ioEx.HResult == 0x80070037)
        {
            LogMessage($"{AppTheme.Section("SOURCE FILE ACCESS ERROR")}");
            LogMessage("Error: The source archive file is no longer accessible.");
            LogMessage($"Details: {ioEx.Message}");
            LogMessage("This usually means:");
            LogMessage($"{AppTheme.Bullet}The external drive/USB device was disconnected");
            LogMessage($"{AppTheme.Bullet}The archive file was modified or deleted after mounting started");
            LogMessage($"{AppTheme.Bullet}The source device is no longer available or has errors");
            LogMessage("Please verify the drive is connected and the file has not been altered.");
            _logErrorAction(ioEx, $"ZipFs.Create: Source file inaccessible for entry '{normalizedPath}'");
            fileNode = null!;
            fileDesc = null!;
            return STATUS_UNSUCCESSFUL;
        }
        catch (ZlibException zlibEx)
        {
            var contextMessage = $"ZipFs.Create: Deflate decompression error for '{normalizedPath}' ({entry.Size / 1024.0:F1} KB).";
            LogMessage($"{AppTheme.Warning} Decompression Error: Cannot read '{normalizedPath}'.");
            _logErrorAction(zlibEx, contextMessage);
            lock (_archiveLock)
            {
                _failedEntries.Add(normalizedPath);
            }

            fileNode = null!;
            fileDesc = null!;
            return STATUS_UNSUCCESSFUL;
        }
        catch (ArgumentOutOfRangeException argEx)
        {
            LogMessage($"{AppTheme.Warning} Corruption Error: Cannot read '{normalizedPath}'. The archive file may be damaged or incomplete.");
            _logErrorAction(argEx, $"ZipFs.Create: Invalid data offset for '{normalizedPath}'.");
            lock (_archiveLock)
            {
                _failedEntries.Add(normalizedPath);
            }

            fileNode = null!;
            fileDesc = null!;
            return STATUS_UNSUCCESSFUL;
        }
        catch (NullReferenceException nre)
        {
            lock (_archiveLock)
            {
                _failedEntries.Add(normalizedPath);
            }

            _logErrorAction(nre, $"ZipFs.Create: NullReferenceException during decompression of '{normalizedPath}'.");
            LogMessage($"{AppTheme.Warning} Decompression Error: Cannot read '{normalizedPath}'.");
            fileNode = null!;
            fileDesc = null!;
            return STATUS_UNSUCCESSFUL;
        }
        catch (Exception ex) when (IsDataErrorException(ex))
        {
            lock (_archiveLock)
            {
                _failedEntries.Add(normalizedPath);
            }

            _logErrorAction(ex, $"ZipFs.Create: Data error for '{normalizedPath}'.");
            LogMessage($"{AppTheme.Warning} Decompression Error: Cannot read '{normalizedPath}'.");
            fileNode = null!;
            fileDesc = null!;
            return STATUS_UNSUCCESSFUL;
        }
        catch (Exception ex)
        {
            _logErrorAction(ex, $"ZipFs.Create: EXCEPTION caching entry '{normalizedPath}'.");
            fileNode = null!;
            fileDesc = null!;
            return STATUS_UNSUCCESSFUL;
        }
    }

    public override int Read(
        object FileNode,
        object FileDesc,
        IntPtr Buffer,
        ulong Offset,
        uint Length,
        out uint BytesTransferred)
    {
        BytesTransferred = 0;

        if (FileNode is EntryNode { IsDir: true })
            return STATUS_ACCESS_DENIED;

        if (FileDesc is not Stream stream)
        {
            return STATUS_INVALID_HANDLE;
        }

        try
        {
            if (stream is StoredEntryStream stored)
            {
                var buffer2 = new byte[Length];
                var bytesRead = stored.ReadAt((long)Offset, buffer2, 0, (int)Length);
                if (bytesRead > 0)
                {
                    System.Runtime.InteropServices.Marshal.Copy(buffer2, 0, Buffer, bytesRead);
                }

                BytesTransferred = (uint)bytesRead;
                return STATUS_SUCCESS;
            }

            if (stream.CanSeek)
            {
                if ((long)Offset >= stream.Length)
                {
                    BytesTransferred = 0;
                    return STATUS_SUCCESS;
                }

                stream.Position = (long)Offset;
            }
            else
            {
                if ((long)Offset != stream.Position)
                {
                    return STATUS_UNSUCCESSFUL;
                }
            }

            var readBuffer = new byte[Length];
            var read = stream.Read(readBuffer, 0, (int)Length);
            if (read > 0)
            {
                System.Runtime.InteropServices.Marshal.Copy(readBuffer, 0, Buffer, read);
            }

            BytesTransferred = (uint)read;
            return STATUS_SUCCESS;
        }
        catch (Exception ex)
        {
            _logErrorAction(ex, $"ZipFs.Read: EXCEPTION reading from stream, Offset={Offset}.");
            return STATUS_UNSUCCESSFUL;
        }
    }

    public override int GetFileInfo(
        object FileNode,
        object FileDesc,
        out Fsp.Interop.FileInfo FileInfo)
    {
        if (FileNode is EntryNode node)
        {
            FileInfo = EntryNodeToFileInfo(node);
            return STATUS_SUCCESS;
        }

        FileInfo = default;
        return STATUS_UNSUCCESSFUL;
    }

    public override int GetVolumeInfo(out VolumeInfo VolumeInfo)
    {
        VolumeInfo = default;
        VolumeInfo.TotalSize = (ulong)(_sourceArchiveStream.CanSeek ? _sourceArchiveStream.Length : 0);
        VolumeInfo.FreeSize = 0;
        VolumeInfo.SetVolumeLabel(VolumeLabelText);
        return STATUS_SUCCESS;
    }

    public override int SetVolumeLabel(string VolumeLabel, out VolumeInfo VolumeInfo)
    {
        VolumeInfo = default;
        return STATUS_ACCESS_DENIED;
    }

    public override int GetSecurityByName(
        string FileName,
        out uint FileAttributes,
        ref byte[] SecurityDescriptor)
    {
        FileAttributes = 0;
        SecurityDescriptor = Array.Empty<byte>();

        var pathValidationResult = ValidatePathLength(FileName, nameof(GetSecurityByName));
        if (pathValidationResult != STATUS_SUCCESS)
            return pathValidationResult;

        var normalizedPath = NormalizePath(FileName);
        var node = GetEntryNode(normalizedPath);

        if (node != null)
        {
            if (node.IsDir)
            {
                FileAttributes = (uint)System.IO.FileAttributes.Directory;
            }
            else
            {
                FileAttributes = (uint)(System.IO.FileAttributes.Archive | System.IO.FileAttributes.ReadOnly);
            }

            return STATUS_SUCCESS;
        }

        if (normalizedPath == "/" || normalizedPath.Equals("\\", StringComparison.OrdinalIgnoreCase))
        {
            FileAttributes = (uint)System.IO.FileAttributes.Directory;
            return STATUS_SUCCESS;
        }

        return STATUS_OBJECT_NAME_NOT_FOUND;
    }

    public override bool ReadDirectoryEntry(
        object FileNode,
        object FileDesc,
        string Pattern,
        string Marker,
        ref object Context,
        out string FileName,
        out Fsp.Interop.FileInfo FileInfo)
    {
        string normalizedPath;
        if (FileNode is EntryNode node)
        {
            normalizedPath = node.NormalizedPath;
        }
        else
        {
            FileName = null!;
            FileInfo = default;
            return false;
        }

        var searchPrefix = normalizedPath.TrimEnd('/') + (normalizedPath == "/" ? "" : "/");
        if (normalizedPath == "/")
        {
            searchPrefix = "/";
        }

        if (Context is not (List<(string Name, Fsp.Interop.FileInfo Info)> entries, int currentIndex))
        {
            lock (_archiveLock)
            {
            }

            var isExplicitDirEntry = _archiveEntries.TryGetValue(normalizedPath, out var dirEntry) && IsDirectory(dirEntry);
            var isImplicitDir = _directoryCreationTimes.ContainsKey(normalizedPath) || normalizedPath == "/";

            if (!isExplicitDirEntry && !isImplicitDir)
            {
                FileName = null!;
                FileInfo = default;
                return false;
            }

            var seenFileNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            entries = new List<(string, Fsp.Interop.FileInfo)>();

            foreach (var kvp in _archiveEntries)
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
                string? fileNameOnly;
                var isDir = IsDirectory(entry);

                if (isDir)
                {
                    if (entry.Key != null)
                    {
                        var tempFullName = entry.Key.TrimEnd('/', '\\');
                        fileNameOnly = Path.GetFileName(tempFullName);
                    }
                    else
                    {
                        fileNameOnly = null;
                    }
                }
                else
                {
                    fileNameOnly = Path.GetFileName(entry.Key);
                }

                if (!string.IsNullOrEmpty(fileNameOnly) && seenFileNames.Add(fileNameOnly))
                {
                    if (!IsNameMatch(fileNameOnly, Pattern))
                        continue;

                    entries.Add((fileNameOnly, new Fsp.Interop.FileInfo
                    {
                        FileAttributes = isDir ? (uint)FileAttributes.Directory : (uint)(FileAttributes.Archive | FileAttributes.ReadOnly),
                        FileSize = isDir ? 0ul : (ulong)entry.Size,
                        CreationTime = DateTimeToFileTimeUtc(entry.CreatedTime ?? DateTime.Now),
                        LastAccessTime = DateTimeToFileTimeUtc(DateTime.Now),
                        LastWriteTime = DateTimeToFileTimeUtc(entry.LastModifiedTime ?? DateTime.Now),
                        ChangeTime = DateTimeToFileTimeUtc(entry.LastModifiedTime ?? DateTime.Now)
                    }));
                }
            }

            foreach (var dirPathKey in _directoryCreationTimes.Keys)
            {
                if (dirPathKey.Equals(searchPrefix, StringComparison.OrdinalIgnoreCase)) continue;
                if (!dirPathKey.StartsWith(searchPrefix, StringComparison.OrdinalIgnoreCase)) continue;

                var remainder = dirPathKey.Substring(searchPrefix.Length);
                if (remainder.Contains('/') || string.IsNullOrEmpty(remainder)) continue;

                var name = dirPathKey.Split('/').LastOrDefault(static s => !string.IsNullOrEmpty(s));
                if (!string.IsNullOrEmpty(name) && seenFileNames.Add(name))
                {
                    if (!IsNameMatch(name, Pattern))
                        continue;

                    _directoryCreationTimes.TryGetValue(dirPathKey, out var ct);
                    _directoryLastWriteTimes.TryGetValue(dirPathKey, out var lwt);
                    _directoryLastAccessTimes.TryGetValue(dirPathKey, out var lat);

                    entries.Add((name, new Fsp.Interop.FileInfo
                    {
                        FileAttributes = (uint)FileAttributes.Directory,
                        FileSize = 0,
                        CreationTime = DateTimeToFileTimeUtc(ct),
                        LastAccessTime = DateTimeToFileTimeUtc(lat),
                        LastWriteTime = DateTimeToFileTimeUtc(lwt),
                        ChangeTime = DateTimeToFileTimeUtc(lwt)
                    }));
                }
            }

            currentIndex = 0;

            if (!string.IsNullOrEmpty(Marker))
            {
                for (var i = 0; i < entries.Count; i++)
                {
                    if (string.Equals(entries[i].Name, Marker, StringComparison.OrdinalIgnoreCase))
                    {
                        currentIndex = i + 1;
                        break;
                    }
                }
            }

            Context = (entries, currentIndex);
        }

        if (currentIndex >= entries.Count)
        {
            FileName = null!;
            FileInfo = default;
            return false;
        }

        var entry2 = entries[currentIndex];
        Context = (entries, currentIndex + 1);

        FileName = entry2.Name;
        FileInfo = entry2.Info;
        return true;
    }

    private static bool IsNameMatch(string name, string pattern)
    {
        if (string.IsNullOrEmpty(pattern) || pattern == "*" || pattern == "*.*")
            return true;

        return IsMatchSimple(name, pattern);
    }

    private static ulong DateTimeToFileTimeUtc(DateTime dt)
    {
        if (dt == DateTime.MinValue)
        {
            dt = new DateTime(1601, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        }

        var utc = dt.ToUniversalTime();
        var fileTime = (utc.Ticks - new DateTime(1601, 1, 1, 0, 0, 0, DateTimeKind.Utc).Ticks);
        return fileTime > 0 ? (ulong)fileTime : 0;
    }

    private static Fsp.Interop.FileInfo EntryNodeToFileInfo(EntryNode node)
    {
        var fi = new Fsp.Interop.FileInfo
        {
            FileAttributes = node.IsDir ? (uint)FileAttributes.Directory : (uint)(FileAttributes.Archive | FileAttributes.ReadOnly),
            FileSize = node.IsDir ? 0ul : (ulong)node.FileSize,
            CreationTime = DateTimeToFileTimeUtc(node.CreationTime),
            LastAccessTime = DateTimeToFileTimeUtc(node.LastAccessTime),
            LastWriteTime = DateTimeToFileTimeUtc(node.LastWriteTime),
            ChangeTime = DateTimeToFileTimeUtc(node.LastWriteTime),
            AllocationSize = node.IsDir ? 0ul : (ulong)((node.FileSize + 4095) / 4096 * 4096)
        };
        return fi;
    }

    public override void Cleanup(
        object FileNode,
        object FileDesc,
        string FileName,
        uint Flags)
    {
    }

    public override void Close(
        object FileNode,
        object FileDesc)
    {
        if (FileDesc is IDisposable disposableContext)
        {
            disposableContext.Dispose();
        }
    }

    public override int Flush(
        object FileNode,
        object FileDesc,
        out Fsp.Interop.FileInfo FileInfo)
    {
        FileInfo = default;
        return STATUS_ACCESS_DENIED;
    }

    public override int SetBasicInfo(
        object FileNode,
        object FileDesc,
        uint FileAttributes,
        ulong CreationTime,
        ulong LastAccessTime,
        ulong LastWriteTime,
        ulong ChangeTime,
        out Fsp.Interop.FileInfo FileInfo)
    {
        FileInfo = default;
        return STATUS_ACCESS_DENIED;
    }

    public override int SetFileSize(
        object FileNode,
        object FileDesc,
        ulong NewSize,
        bool SetAllocationSize,
        out Fsp.Interop.FileInfo FileInfo)
    {
        FileInfo = default;
        return STATUS_ACCESS_DENIED;
    }

    public override int CanDelete(
        object FileNode,
        object FileDesc,
        string FileName)
    {
        return STATUS_ACCESS_DENIED;
    }

    public override int Rename(
        object FileNode,
        object FileDesc,
        string FileName,
        string NewFileName,
        bool ReplaceIfExists)
    {
        return STATUS_ACCESS_DENIED;
    }

    public override int GetSecurity(
        object FileNode,
        object FileDesc,
        ref byte[] SecurityDescriptor)
    {
        SecurityDescriptor = Array.Empty<byte>();
        return STATUS_SUCCESS;
    }

    public override int SetSecurity(
        object FileNode,
        object FileDesc,
        AccessControlSections Sections,
        byte[] SecurityDescriptor)
    {
        return STATUS_ACCESS_DENIED;
    }

    private string CreateSecureTempFile()
    {
        var tempFilePath = Path.Combine(_tempDirectoryPath, Guid.NewGuid().ToString("N") + ".tmp");

        File.Create(tempFilePath).Dispose();

        try
        {
            var fileInfo = new System.IO.FileInfo(tempFilePath);

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
        catch (PlatformNotSupportedException)
        {
        }
        catch (InvalidOperationException)
        {
        }

        return tempFilePath;
    }

    private static bool IsMatchSimple(string input, string pattern)
    {
        if (pattern.Length > 260)
            return false;

        if (pattern.Equals("*", StringComparison.Ordinal) || pattern.Equals("*.*", StringComparison.Ordinal)) return true;

        var regexPattern = "^" + Regex.Escape(pattern).Replace("\\*", ".*").Replace("\\?", ".") + "$";

        var regex = RegexCache.GetOrAdd(regexPattern, static p =>
        {
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

    public void Dispose()
    {
        if (_disposed)
            return;

        lock (_archiveLock)
        {
            _disposed = true;
        }

        _archive.Dispose();
        _sourceArchiveStream.Dispose();

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

        lock (_memoryLock)
        {
            _currentMemoryUsage = 0;
        }
    }

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
                    lock (_owner._memoryLock)
                    {
                        _owner._currentMemoryUsage -= _size;
                        if (_owner._currentMemoryUsage < 0)
                        {
                            _owner._currentMemoryUsage = 0;
                        }
                    }
                }

                _disposed = true;
            }

            base.Dispose(disposing);
        }
    }

    private sealed class StoredEntryStream : Stream
    {
        private readonly Stream _sourceStream;
        private readonly long _dataOffset;
        private readonly object _sourceLock;
        private readonly SafeFileHandle? _fileHandle;
        private long _position;
        private bool _disposed;

        public StoredEntryStream(Stream sourceStream, long dataOffset, long dataLength, object sourceLock)
        {
            _sourceStream = sourceStream;
            _dataOffset = dataOffset;
            Length = dataLength;
            _sourceLock = sourceLock;
            _fileHandle = (sourceStream as FileStream)?.SafeFileHandle;
            _sourceStream.Position = dataOffset;
        }

        public override bool CanRead => true;
        public override bool CanSeek => true;
        public override bool CanWrite => false;
        public override long Length { get; }

        public override long Position
        {
            get => _position;
            set
            {
                if (value < 0 || value > Length)
                    throw new ArgumentOutOfRangeException(nameof(value));

                _position = value;
            }
        }

        public override int Read(byte[] buffer, int offset, int count)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);

            var maxBytes = (int)Math.Min(count, Length - _position);
            if (maxBytes <= 0) return 0;

            if (Monitor.TryEnter(_sourceLock))
            {
                try
                {
                    if (_sourceStream.Position == _dataOffset + _position)
                    {
                        var bytesRead = _sourceStream.Read(buffer, offset, maxBytes);
                        _position += bytesRead;
                        return bytesRead;
                    }
                }
                finally
                {
                    Monitor.Exit(_sourceLock);
                }
            }

            if (_fileHandle != null)
            {
                var bytesRead = RandomAccess.Read(_fileHandle, buffer.AsSpan(offset, maxBytes), _dataOffset + _position);
                _position += bytesRead;
                return bytesRead;
            }

            lock (_sourceLock)
            {
                _sourceStream.Position = _dataOffset + _position;
                var bytesRead = _sourceStream.Read(buffer, offset, maxBytes);
                _position += bytesRead;
                return bytesRead;
            }
        }

        public override int Read(Span<byte> buffer)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);

            var maxBytes = (int)Math.Min(buffer.Length, Length - _position);
            if (maxBytes <= 0) return 0;

            if (Monitor.TryEnter(_sourceLock))
            {
                try
                {
                    if (_sourceStream.Position == _dataOffset + _position)
                    {
                        var bytesRead = _sourceStream.Read(buffer[..maxBytes]);
                        _position += bytesRead;
                        return bytesRead;
                    }
                }
                finally
                {
                    Monitor.Exit(_sourceLock);
                }
            }

            if (_fileHandle != null)
            {
                var bytesRead = RandomAccess.Read(_fileHandle, buffer[..maxBytes], _dataOffset + _position);
                _position += bytesRead;
                return bytesRead;
            }

            lock (_sourceLock)
            {
                _sourceStream.Position = _dataOffset + _position;
                var bytesRead = _sourceStream.Read(buffer[..maxBytes]);
                _position += bytesRead;
                return bytesRead;
            }
        }

        public int ReadAt(long fileOffset, byte[] buffer, int bufferOffset, int count)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);

            if (fileOffset < 0 || fileOffset >= Length)
                return 0;

            var maxBytes = (int)Math.Min(count, Length - fileOffset);
            if (maxBytes <= 0) return 0;

            if (Monitor.TryEnter(_sourceLock))
            {
                try
                {
                    if (_sourceStream.Position == _dataOffset + fileOffset)
                        return _sourceStream.Read(buffer, bufferOffset, maxBytes);
                }
                finally
                {
                    Monitor.Exit(_sourceLock);
                }
            }

            if (_fileHandle != null)
                return RandomAccess.Read(_fileHandle, buffer.AsSpan(bufferOffset, maxBytes), _dataOffset + fileOffset);

            lock (_sourceLock)
            {
                _sourceStream.Position = _dataOffset + fileOffset;
                return _sourceStream.Read(buffer, bufferOffset, maxBytes);
            }
        }

        public override long Seek(long offset, SeekOrigin origin)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);

            _position = origin switch
            {
                SeekOrigin.Begin => offset,
                SeekOrigin.Current => _position + offset,
                SeekOrigin.End => Length + offset,
                _ => throw new ArgumentOutOfRangeException(nameof(origin))
            };

            if (_position < 0 || _position > Length)
                throw new IOException("Seek position out of range");

            return _position;
        }

        public override void Flush()
        {
        }

        public override void SetLength(long value)
        {
            throw new NotSupportedException();
        }

        public override void Write(byte[] buffer, int offset, int count)
        {
            throw new NotSupportedException();
        }

        protected override void Dispose(bool disposing)
        {
            _disposed = true;
            base.Dispose(disposing);
        }
    }
}
