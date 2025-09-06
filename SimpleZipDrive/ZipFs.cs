using System.Security.AccessControl;
using System.Security.Principal;
using System.Text.RegularExpressions;
using DokanNet;
using ICSharpCode.SharpZipLib.Zip;
using FileAccess = DokanNet.FileAccess;

namespace SimpleZipDrive;

public class ZipFs : IDokanOperations, IDisposable
{
    private readonly Stream _sourceZipStream;
    private readonly ZipFile _zipFile;
    private readonly Dictionary<string, ZipEntry> _zipEntries = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, DateTime> _directoryCreationTimes = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, DateTime> _directoryLastWriteTimes = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, DateTime> _directoryLastAccessTimes = new(StringComparer.OrdinalIgnoreCase);

    private readonly Action<Exception?, string?> _logErrorAction; // Delegate for logging
    private readonly object _zipLock = new(); // **FIX 1: Lock for thread-safe access to _zipFile**

    private const string VolumeLabel = "SimpleZipDrive";
    private static readonly char[] Separator = { '/' };
    private const long MaxMemoryCacheSize = 1000 * 1024 * 1024;

    public ZipFs(Stream zipFileStream, string mountPoint, Action<Exception?, string?> logErrorAction)
    {
        _sourceZipStream = zipFileStream;
        _logErrorAction = logErrorAction ?? throw new ArgumentNullException(nameof(logErrorAction));

        try
        {
            _zipFile = new ZipFile(_sourceZipStream, false);
            InitializeEntries();
            Console.WriteLine($"ZipFs Constructor: Using SharpZipLib. _sourceZipStream.CanSeek = {_sourceZipStream.CanSeek}, _sourceZipStream type = {_sourceZipStream.GetType().FullName}");
        }
        catch (Exception ex)
        {
            _logErrorAction(ex, $"Error during ZipFs construction for mount point '{mountPoint}'.");
            throw;
        }
    }

    private void InitializeEntries()
    {
        try
        {
            lock (_zipLock)
            {
                foreach (ZipEntry entry in _zipFile)
                {
                    if (string.IsNullOrEmpty(entry.Name))
                    {
                        _logErrorAction?.Invoke(null, $"Skipping invalid ZIP entry with null/empty name: {entry.Name}");
                        continue;
                    }

                    var normalizedPath = NormalizePath(entry.Name);
                    _zipEntries[normalizedPath] = entry;

                    var currentPath = "";
                    var parts = normalizedPath.Split(Separator, StringSplitOptions.RemoveEmptyEntries);
                    for (var i = 0; i < parts.Length - (entry.IsDirectory ? 0 : 1); i++)
                    {
                        currentPath += "/" + parts[i];
                        if (_directoryCreationTimes.ContainsKey(currentPath)) continue;

                        _directoryCreationTimes[currentPath] = entry.DateTime;
                        _directoryLastWriteTimes[currentPath] = entry.DateTime;
                        _directoryLastAccessTimes[currentPath] = DateTime.Now;
                    }
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

    public NtStatus CreateFile(
        string fileName,
        FileAccess access,
        FileShare share,
        FileMode mode,
        FileOptions options,
        FileAttributes attributes,
        IDokanFileInfo info)
    {
        var normalizedPath = NormalizePath(fileName);

        if (info.Context is IDisposable existingContextDisposable)
        {
            existingContextDisposable.Dispose();
            info.Context = null;
        }

        if (_zipEntries.TryGetValue(normalizedPath, out var entry))
        {
            if (entry.IsDirectory)
            {
                info.IsDirectory = true;
                return mode switch
                {
                    FileMode.Open or FileMode.OpenOrCreate or FileMode.Create => DokanResult.Success,
                    FileMode.CreateNew => DokanResult.FileExists,
                    _ => DokanResult.AccessDenied
                };
            }
            else
            {
                info.IsDirectory = false;
                var canRead = access.HasFlag(FileAccess.GenericRead) || access.HasFlag(FileAccess.ReadData);
                var result = mode switch
                {
                    FileMode.Open => (entry.Size >= 0) ? DokanResult.Success : DokanResult.FileNotFound,
                    FileMode.OpenOrCreate or FileMode.Create => DokanResult.Success,
                    FileMode.CreateNew => DokanResult.FileExists,
                    _ => DokanResult.AccessDenied
                };

                if (result != DokanResult.Success || !canRead) return result;

                if (entry.Size > MaxMemoryCacheSize)
                {
                    // For large files, use a random access stream that can handle seeks
                    try
                    {
                        // Create a stream accessor that safely accesses the ZipFile with locking
                        void StreamAccessor(ZipEntry zipEntry, Action<Stream> action)
                        {
                            lock (_zipLock)
                            {
                                using var entryStream = _zipFile.GetInputStream(zipEntry);
                                action(entryStream);
                            }
                        }

                        var randomAccessStream = new RandomAccessZipStream(entry, StreamAccessor);
                        info.Context = randomAccessStream;
                    }
                    catch (Exception ex)
                    {
                        _logErrorAction(ex, $"ZipFs.CreateFile: EXCEPTION creating random access stream for large entry '{normalizedPath}'.");
                        return DokanResult.Error;
                    }
                }
                else
                {
                    // For smaller files, cache the entire decompressed file in memory for speed.
                    try
                    {
                        byte[] entryBytes;
                        lock (_zipLock) // **FIX 1: Thread-safe access**
                        {
                            using var entryStream = _zipFile.GetInputStream(entry);
                            if (entry.Size == 0)
                            {
                                entryBytes = Array.Empty<byte>();
                            }
                            else
                            {
                                using var tempMs = new MemoryStream((int)entry.Size);
                                entryStream.CopyTo(tempMs);
                                entryBytes = tempMs.ToArray();
                            }
                        }

                        if (entryBytes.Length != entry.Size)
                        {
                            _logErrorAction(new InvalidDataException($"Mismatch reading entry '{normalizedPath}'. Expected {entry.Size}, got {entryBytes.Length}."),
                                "ZipFs.CreateFile: Entry read mismatch.");
                        }

                        var memoryStream = new MemoryStream(entryBytes);
                        info.Context = memoryStream;
                    }
                    catch (Exception ex)
                    {
                        _logErrorAction(ex, $"ZipFs.CreateFile: EXCEPTION caching entry '{normalizedPath}'.");
                        info.Context = null;
                        return DokanResult.Error;
                    }
                }

                return result;
            }
        }
        else if (_directoryCreationTimes.ContainsKey(normalizedPath) || normalizedPath == "/")
        {
            info.IsDirectory = true;
            return mode switch
            {
                FileMode.Open or FileMode.OpenOrCreate or FileMode.Create => DokanResult.Success,
                FileMode.CreateNew => DokanResult.FileExists,
                _ => DokanResult.AccessDenied
            };
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
        var normalizedPath = NormalizePath(fileName);

        if (info.Context is not Stream stream)
        {
            _logErrorAction(new InvalidOperationException($"ReadFile called for '{normalizedPath}' but info.Context was not a Stream."), "ZipFs.ReadFile: Invalid context.");
            return DokanResult.Error;
        }

        try
        {
            // The RandomAccessZipStream and MemoryStream both handle seeking properly
            if (stream.CanSeek)
            {
                if (offset >= stream.Length) return DokanResult.Success; // EOF

                stream.Position = offset;
            }
            else
            {
                // This should not happen with our new implementation but keep as a fallback
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
        var normalizedPath = NormalizePath(fileName);
        fileInfo = new FileInformation();

        if (_zipEntries.TryGetValue(normalizedPath, out var entry))
        {
            if (entry.IsDirectory)
            {
                fileInfo.Attributes = FileAttributes.Directory;
                fileInfo.FileName = Path.GetFileName(entry.Name.TrimEnd('/'));
                fileInfo.LastWriteTime = entry.DateTime;
                fileInfo.CreationTime = entry.DateTime;
                fileInfo.LastAccessTime = DateTime.Now;
                info.IsDirectory = true;
            }
            else
            {
                fileInfo.Attributes = FileAttributes.Archive | FileAttributes.ReadOnly;
                fileInfo.FileName = Path.GetFileName(entry.Name);
                fileInfo.Length = entry.Size;
                fileInfo.LastWriteTime = entry.DateTime;
                fileInfo.CreationTime = entry.DateTime;
                fileInfo.LastAccessTime = DateTime.Now;
                info.IsDirectory = false;
            }

            return DokanResult.Success;
        }
        else if (_directoryCreationTimes.ContainsKey(normalizedPath) || normalizedPath == "/")
        {
            fileInfo.Attributes = FileAttributes.Directory;
            fileInfo.FileName = normalizedPath.Split('/').LastOrDefault(s => !string.IsNullOrEmpty(s)) ?? "";
            fileInfo.LastWriteTime = _directoryLastWriteTimes.TryGetValue(normalizedPath, out var lwt) ? lwt : DateTime.Now;
            fileInfo.CreationTime = _directoryCreationTimes.TryGetValue(normalizedPath, out var ct) ? ct : DateTime.Now;
            fileInfo.LastAccessTime = _directoryLastAccessTimes.TryGetValue(normalizedPath, out var lat) ? lat : DateTime.Now;
            info.IsDirectory = true;
            return DokanResult.Success;
        }

        return DokanResult.PathNotFound;
    }

    public NtStatus FindFiles(string fileName, out IList<FileInformation> files, IDokanFileInfo info)
    {
        files = new List<FileInformation>();
        var normalizedPath = NormalizePath(fileName);

        var isExplicitDirEntry = _zipEntries.TryGetValue(normalizedPath, out var dirEntry) && dirEntry.IsDirectory;
        var isImplicitDir = _directoryCreationTimes.ContainsKey(normalizedPath) || normalizedPath == "/";

        if (!isExplicitDirEntry && !isImplicitDir) return DokanResult.PathNotFound;

        var searchPrefix = normalizedPath.TrimEnd('/') + (normalizedPath == "/" ? "" : "/");
        if (normalizedPath == "/")
        {
            searchPrefix = "/";
        }

        try
        {
            var childEntries = _zipEntries
                .Where(kvp =>
                {
                    var path = kvp.Key;
                    if (path.Equals(searchPrefix, StringComparison.OrdinalIgnoreCase)) return false;
                    if (!path.StartsWith(searchPrefix, StringComparison.OrdinalIgnoreCase)) return false;

                    var remainder = path.Substring(searchPrefix.Length);
                    return !remainder.Contains('/') || (remainder.EndsWith('/') && remainder.Count(c => c == '/') == 1);
                })
                .Select(kvp => kvp.Value)
                .ToList();

            foreach (var entry in childEntries)
            {
                var fi = new FileInformation
                {
                    LastWriteTime = entry.DateTime,
                    CreationTime = entry.DateTime,
                    LastAccessTime = DateTime.Now
                };
                if (entry.IsDirectory)
                {
                    fi.Attributes = FileAttributes.Directory;
                    var tempFullName = entry.Name.TrimEnd('/');
                    fi.FileName = Path.GetFileName(tempFullName);
                }
                else
                {
                    fi.Attributes = FileAttributes.Archive | FileAttributes.ReadOnly;
                    fi.Length = entry.Size;
                    fi.FileName = Path.GetFileName(entry.Name);
                }

                if (!string.IsNullOrEmpty(fi.FileName)) files.Add(fi);
            }

            var implicitChildDirs = _directoryCreationTimes.Keys
                .Where(k =>
                {
                    if (k.Equals(searchPrefix, StringComparison.OrdinalIgnoreCase)) return false;
                    if (!k.StartsWith(searchPrefix, StringComparison.OrdinalIgnoreCase)) return false;

                    var remainder = k.Substring(searchPrefix.Length);
                    return !remainder.Contains('/') && !string.IsNullOrEmpty(remainder);
                })
                .Where(k => childEntries.All(e => NormalizePath(e.Name).TrimEnd('/') != k))
                .ToList();

            foreach (var dirPathKey in implicitChildDirs)
            {
                var name = dirPathKey.Split('/').LastOrDefault(s => !string.IsNullOrEmpty(s));
                if (!string.IsNullOrEmpty(name))
                {
                    files.Add(new FileInformation
                    {
                        FileName = name,
                        Attributes = FileAttributes.Directory,
                        LastWriteTime = _directoryLastWriteTimes[dirPathKey],
                        CreationTime = _directoryCreationTimes[dirPathKey],
                        LastAccessTime = _directoryLastAccessTimes[dirPathKey]
                    });
                }
            }

            files = files.Where(static f => !string.IsNullOrEmpty(f.FileName)).GroupBy(f => f.FileName, StringComparer.OrdinalIgnoreCase).Select(g => g.First()).ToList();
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
        if (info.Context is IDisposable disposableContext) disposableContext.Dispose();
        info.Context = null;
    }

    public void CloseFile(string fileName, IDokanFileInfo info)
    {
        if (info.Context is not IDisposable disposableContext) return;

        _logErrorAction(new InvalidOperationException($"Context was still present in CloseFile for '{fileName}'. Disposing."), "ZipFs.CloseFile: Unexpected context.");
        disposableContext.Dispose();
        info.Context = null;
    }

    public NtStatus GetDiskFreeSpace(out long freeBytesAvailable, out long totalNumberOfBytes, out long totalNumberOfFreeBytes, IDokanFileInfo info)
    {
        totalNumberOfBytes = _sourceZipStream.CanSeek ? _sourceZipStream.Length : 0;
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

    public class RandomAccessZipStream : Stream
    {
        private readonly ZipEntry _entry;
        private readonly Action<ZipEntry, Action<Stream>> _streamAccessor; // Callback to safely access ZipFile
        private readonly long _entrySize;
        private long _currentPosition;
        private readonly Dictionary<long, byte[]> _cachedBlocks = new();
        private const int BlockSize = 64 * 1024; // 64KB blocks

        public RandomAccessZipStream(ZipEntry entry, Action<ZipEntry, Action<Stream>> streamAccessor)
        {
            _entry = entry;
            _streamAccessor = streamAccessor;
            _entrySize = entry.Size;
            _currentPosition = 0;
        }

        public override bool CanRead => true;
        public override bool CanSeek => true;
        public override bool CanWrite => false;
        public override long Length => _entrySize;

        public override long Position
        {
            get => _currentPosition;
            set => _currentPosition = value;
        }

        public override int Read(byte[] buffer, int offset, int count)
        {
            if (_currentPosition >= _entrySize)
                return 0;

            int bytesRead;
            var bytesToRead = (int)Math.Min(count, _entrySize - _currentPosition);

            // For small reads within a reasonable range, use block caching
            if (bytesToRead <= BlockSize * 4) // Up to 256KB
            {
                bytesRead = ReadWithCaching(buffer, offset, bytesToRead);
            }
            else
            {
                // For large reads, create a fresh stream and skip to position
                bytesRead = ReadDirect(buffer, offset, bytesToRead);
            }

            _currentPosition += bytesRead;
            return bytesRead;
        }

        private int ReadWithCaching(byte[] buffer, int offset, int count)
        {
            var totalBytesRead = 0;
            var currentPosition = _currentPosition;

            while (totalBytesRead < count && currentPosition < _entrySize)
            {
                var blockStart = (currentPosition / BlockSize) * BlockSize;
                var blockOffset = (int)(currentPosition - blockStart);
                var bytesFromThisBlock = Math.Min(count - totalBytesRead, BlockSize - blockOffset);

                // Get or create a block
                if (!_cachedBlocks.TryGetValue(blockStart, out var blockData))
                {
                    blockData = LoadBlock(blockStart);
                    if (blockData != null)
                    {
                        _cachedBlocks[blockStart] = blockData;
                    }
                    else
                    {
                        break; // Error loading block
                    }
                }

                var bytesAvailableInBlock = Math.Min(blockData.Length - blockOffset, bytesFromThisBlock);
                if (bytesAvailableInBlock > 0)
                {
                    Array.Copy(blockData, blockOffset, buffer, offset + totalBytesRead, bytesAvailableInBlock);
                    totalBytesRead += bytesAvailableInBlock;
                    currentPosition += bytesAvailableInBlock;
                }
                else
                {
                    break;
                }
            }

            return totalBytesRead;
        }

        private byte[]? LoadBlock(long blockStart)
        {
            try
            {
                byte[]? result = null;
                Exception? exception = null;

                _streamAccessor(_entry, stream =>
                {
                    try
                    {
                        // Skip to the block start position
                        if (blockStart > 0)
                        {
                            stream.Seek(blockStart, SeekOrigin.Begin);
                        }

                        var blockSize = (int)Math.Min(BlockSize, _entrySize - blockStart);
                        var blockData = new byte[blockSize];
                        var totalRead = 0;

                        while (totalRead < blockSize)
                        {
                            var read = stream.Read(blockData, totalRead, blockSize - totalRead);
                            if (read == 0) break;

                            totalRead += read;
                        }

                        result = blockData;
                    }
                    catch (Exception ex)
                    {
                        exception = ex;
                    }
                });

                if (exception != null)
                    throw exception;

                return result;
            }
            catch
            {
                return null;
            }
        }

        private int ReadDirect(byte[] buffer, int offset, int count)
        {
            try
            {
                var bytesRead = 0;
                Exception? exception = null;

                _streamAccessor(_entry, stream =>
                {
                    try
                    {
                        // Skip to the current position
                        if (_currentPosition > 0)
                        {
                            stream.Seek(_currentPosition, SeekOrigin.Begin);
                        }

                        bytesRead = stream.Read(buffer, offset, count);
                    }
                    catch (Exception ex)
                    {
                        exception = ex;
                    }
                });

                if (exception != null)
                    throw exception;

                return bytesRead;
            }
            catch
            {
                return 0;
            }
        }

        public override long Seek(long offset, SeekOrigin origin)
        {
            var newPosition = origin switch
            {
                SeekOrigin.Begin => offset,
                SeekOrigin.Current => _currentPosition + offset,
                SeekOrigin.End => _entrySize + offset,
                _ => throw new ArgumentException("Invalid seek origin")
            };

            if (newPosition < 0 || newPosition > _entrySize)
                throw new IOException("Seek position is out of range");

            _currentPosition = newPosition;
            return _currentPosition;
        }

        public override void SetLength(long value)
        {
            throw new NotSupportedException();
        }

        public override void Write(byte[] buffer, int offset, int count)
        {
            throw new NotSupportedException();
        }

        public override void Flush()
        {
            // No-op for read-only stream
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                _cachedBlocks.Clear();
            }

            base.Dispose(disposing);
        }
    }

    public void Dispose()
    {
        _zipFile?.Close();
        GC.SuppressFinalize(this);
    }
}
