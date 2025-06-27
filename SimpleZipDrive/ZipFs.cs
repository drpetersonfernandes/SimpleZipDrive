using System.IO.Compression;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text.RegularExpressions;
using DokanNet;
using FileAccess = DokanNet.FileAccess;

namespace SimpleZipDrive;

public class ZipFs : IDokanOperations, IDisposable
{
    private readonly Stream _sourceZipStream;
    private readonly ZipArchive _zipArchive;
    private readonly Dictionary<string, ZipArchiveEntry> _zipEntries = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, DateTime> _directoryCreationTimes = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, DateTime> _directoryLastWriteTimes = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, DateTime> _directoryLastAccessTimes = new(StringComparer.OrdinalIgnoreCase);

    private readonly Action<Exception?, string?> _logErrorAction; // Delegate for logging

    private const string VolumeLabel = "SimpleZipDrive";
    private static readonly char[] Separator = new[] { '/' };
    private const long MaxMemoryCacheSize = 1024 * 1024 * 1024; // 1024 MB (1 Gb)

    // Updated constructor to accept the logger action
    public ZipFs(Stream zipFileStream, string mountPoint, Action<Exception?, string?> logErrorAction)
    {
        _sourceZipStream = zipFileStream;
        _logErrorAction = logErrorAction ?? throw new ArgumentNullException(nameof(logErrorAction)); // Store the logger

        try
        {
            _zipArchive = new ZipArchive(_sourceZipStream, ZipArchiveMode.Read, true);
            InitializeEntries();
            Console.WriteLine($"ZipFs Constructor: _sourceZipStream.CanSeek = {_sourceZipStream.CanSeek}, _sourceZipStream type = {_sourceZipStream.GetType().FullName}");
        }
        catch (Exception ex)
        {
            _logErrorAction(ex, $"Error during ZipFs construction for mount point '{mountPoint}'.");
            // Re-throw to ensure the constructor failure is propagated,
            // which will be caught by Program.cs's AttemptMountLifecycle
            throw;
        }
    }

    private void InitializeEntries()
    {
        try // Add try-catch for robustness during initialization
        {
            foreach (var entry in _zipArchive.Entries)
            {
                var normalizedPath = NormalizePath(entry.FullName);
                _zipEntries[normalizedPath] = entry;

                var currentPath = "";
                var parts = normalizedPath.Split(Separator, StringSplitOptions.RemoveEmptyEntries);
                for (var i = 0; i < parts.Length - (IsDirectoryEntry(entry) ? 0 : 1); i++)
                {
                    currentPath += "/" + parts[i];
                    if (_directoryCreationTimes.ContainsKey(currentPath)) continue;

                    _directoryCreationTimes[currentPath] = entry.LastWriteTime.DateTime;
                    _directoryLastWriteTimes[currentPath] = entry.LastWriteTime.DateTime;
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
            _logErrorAction(ex, "Error during ZipFs.InitializeEntries.");
            // Depending on severity, you might want to re-throw or handle differently
            // For now, log and continue, some entries might be missing.
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

    private static bool IsDirectoryEntry(ZipArchiveEntry entry)
    {
        return entry.FullName.EndsWith('/') || entry.FullName.EndsWith('\\') || (entry.Length == 0 && string.IsNullOrEmpty(entry.Name));
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
        // Console.WriteLine($"ZipFs.CreateFile: START - Path='{fileName}' (Norm='{normalizedPath}'), Mode={mode}, Access={access}, Options={options}, IsDirectory={info.IsDirectory}");

        if (info.Context is IDisposable existingContextDisposable)
        {
            // Console.WriteLine($"ZipFs.CreateFile: Warning - info.Context was already set for '{normalizedPath}'. Disposing it.");
            existingContextDisposable.Dispose();
            info.Context = null;
        }

        if (_zipEntries.TryGetValue(normalizedPath, out var entry))
        {
            if (IsDirectoryEntry(entry))
            {
                // Console.WriteLine($"ZipFs.CreateFile: Path '{normalizedPath}' is an explicit directory entry.");
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
                // Console.WriteLine($"ZipFs.CreateFile: Path '{normalizedPath}' is a file entry. Length={entry.Length}.");
                info.IsDirectory = false;
                var canRead = access.HasFlag(FileAccess.GenericRead) || access.HasFlag(FileAccess.ReadData);
                var result = mode switch
                {
                    FileMode.Open => (entry.Length >= 0) ? DokanResult.Success : DokanResult.FileNotFound,
                    FileMode.OpenOrCreate or FileMode.Create => DokanResult.Success,
                    FileMode.CreateNew => DokanResult.FileExists,
                    _ => DokanResult.AccessDenied
                };

                if (result != DokanResult.Success || !canRead) return result;

                // For large files, don't cache in memory.
                // ReadFile will handle reading directly from the zip entry.
                // This is slower for random access but avoids OutOfMemoryException.
                if (entry.Length > MaxMemoryCacheSize)
                {
                    // Console.WriteLine($"ZipFs.CreateFile: Entry '{normalizedPath}' is too large ({entry.Length} bytes) for in-memory cache. Using streaming fallback.");
                    info.Context = null; // Ensure context is null so ReadFile uses its fallback logic.
                }
                else
                {
                    // For smaller files, use the existing in-memory cache logic.
                    try
                    {
                        // Console.WriteLine($"ZipFs.CreateFile: Caching entry '{normalizedPath}' to MemoryStream. Entry length: {entry.Length}");
                        using var entryStream = entry.Open();
                        byte[] entryBytes;
                        if (entry.Length == 0)
                        {
                            entryBytes = Array.Empty<byte>();
                        }
                        else
                        {
                            // The cast to (int) is safe because we've already checked entry.Length <= MaxMemoryCacheSize.
                            using var tempMs = new MemoryStream((int)entry.Length);
                            entryStream.CopyTo(tempMs);
                            entryBytes = tempMs.ToArray();
                        }

                        if (entryBytes.Length != entry.Length)
                        {
                            _logErrorAction(new InvalidDataException($"Mismatch reading entry '{normalizedPath}'. Expected {entry.Length}, got {entryBytes.Length}."),
                                "ZipFs.CreateFile: Entry read mismatch.");
                            // Potentially return an error here if this is critical
                        }

                        var memoryStream = new MemoryStream(entryBytes);
                        info.Context = memoryStream;
                        // Console.WriteLine($"ZipFs.CreateFile: Successfully cached '{normalizedPath}' to MemoryStream ({memoryStream.Length} bytes).");
                    }
                    catch (OutOfMemoryException oomEx)
                    {
                        _logErrorAction(oomEx, $"ZipFs.CreateFile: OutOfMemoryException caching entry '{normalizedPath}'.");
                        info.Context = null;
                        return DokanResult.Error; // Out of memory
                    }
                    catch (Exception ex)
                    {
                        _logErrorAction(ex, $"ZipFs.CreateFile: EXCEPTION caching entry '{normalizedPath}'.");
                        info.Context = null;
                        return DokanResult.Error; // Generic error
                    }
                }

                // Console.WriteLine($"ZipFs.CreateFile: File entry '{normalizedPath}', FileMode={mode}, returning {result}.");
                return result;
            }
        }
        else if (_directoryCreationTimes.ContainsKey(normalizedPath) || normalizedPath == "/")
        {
            // Console.WriteLine($"ZipFs.CreateFile: Path '{normalizedPath}' is an implicit directory.");
            info.IsDirectory = true;
            return mode switch
            {
                FileMode.Open or FileMode.OpenOrCreate or FileMode.Create => DokanResult.Success,
                FileMode.CreateNew => DokanResult.FileExists,
                _ => DokanResult.AccessDenied
            };
        }

        // Console.WriteLine($"ZipFs.CreateFile: Path not found for '{normalizedPath}'. Returning PathNotFound.");
        return DokanResult.PathNotFound;
    }

    public NtStatus ReadFile(
        string fileName,
        byte[] buffer,
        out int bytesRead,
        long offset,
        IDokanFileInfo info)
    {
        var normalizedPath = NormalizePath(fileName);
        bytesRead = 0;
        // Console.WriteLine($"ZipFs.ReadFile: START - Path='{fileName}' (Norm='{normalizedPath}'), Offset={offset}, BufferLength={buffer.Length}");

        if (info.IsDirectory)
        {
            _logErrorAction(new UnauthorizedAccessException($"Attempt to read a directory '{normalizedPath}' as a file."), "ZipFs.ReadFile: Directory read attempt.");
            return DokanResult.AccessDenied;
        }

        if (offset < 0)
        {
            _logErrorAction(new ArgumentOutOfRangeException(nameof(offset), $"Negative offset {offset} requested for '{normalizedPath}'."), "ZipFs.ReadFile: Negative offset.");
            return DokanResult.InvalidParameter;
        }

        if (info.Context is MemoryStream cachedStream)
        {
            // Console.WriteLine($"ZipFs.ReadFile: Using cached MemoryStream for '{normalizedPath}'. Length={cachedStream.Length}.");
            try
            {
                if (offset >= cachedStream.Length) return DokanResult.Success; // EOF

                cachedStream.Position = offset;
                bytesRead = cachedStream.Read(buffer, 0, buffer.Length);
                return DokanResult.Success;
            }
            catch (Exception ex)
            {
                _logErrorAction(ex, $"ZipFs.ReadFile: EXCEPTION reading from cached MemoryStream for '{normalizedPath}', Offset={offset}.");
                return DokanResult.Error;
            }
        }
        else
        {
            // Console.WriteLine($"ZipFs.ReadFile: No cached MemoryStream for '{normalizedPath}'. Attempting direct entry read.");
            if (!_zipEntries.TryGetValue(normalizedPath, out var entry)) return DokanResult.PathNotFound;

            if (IsDirectoryEntry(entry))
            {
                _logErrorAction(new UnauthorizedAccessException($"Fallback read attempt on directory entry '{normalizedPath}'."), "ZipFs.ReadFile (fallback): Directory read attempt.");
                return DokanResult.AccessDenied;
            }

            if (offset >= entry.Length) return DokanResult.Success; // EOF

            try
            {
                using var entryStream = entry.Open();
                if (offset > 0)
                {
                    if (entryStream.CanSeek)
                    {
                        entryStream.Seek(offset, SeekOrigin.Begin);
                    }
                    else
                    {
                        var skipBuffer = new byte[Math.Min(4096, (int)Math.Min(offset, int.MaxValue))]; // Cap skip buffer size
                        long skippedSoFar = 0;
                        while (skippedSoFar < offset)
                        {
                            var toReadThisLoop = (int)Math.Min(skipBuffer.Length, offset - skippedSoFar);
                            var skipped = entryStream.Read(skipBuffer, 0, toReadThisLoop);
                            if (skipped == 0) return DokanResult.Success; // EOF while skipping

                            skippedSoFar += skipped;
                        }
                    }
                }

                bytesRead = entryStream.Read(buffer, 0, buffer.Length);
                return DokanResult.Success;
            }
            catch (Exception ex)
            {
                _logErrorAction(ex, $"ZipFs.ReadFile (fallback): EXCEPTION for '{normalizedPath}', Offset={offset}.");
                return DokanResult.Error;
            }
        }
    }

    public NtStatus GetFileInformation(string fileName, out FileInformation fileInfo, IDokanFileInfo info)
    {
        var normalizedPath = NormalizePath(fileName);
        fileInfo = new FileInformation();

        if (_zipEntries.TryGetValue(normalizedPath, out var entry))
        {
            if (IsDirectoryEntry(entry))
            {
                fileInfo.Attributes = FileAttributes.Directory;
                fileInfo.FileName = entry.Name; // Or derive from path
                fileInfo.LastWriteTime = entry.LastWriteTime.DateTime;
                fileInfo.CreationTime = entry.LastWriteTime.DateTime;
                fileInfo.LastAccessTime = DateTime.Now; // Or entry.LastWriteTime.DateTime
                info.IsDirectory = true;
            }
            else
            {
                fileInfo.Attributes = FileAttributes.Archive | FileAttributes.ReadOnly;
                fileInfo.FileName = entry.Name;
                fileInfo.Length = entry.Length;
                fileInfo.LastWriteTime = entry.LastWriteTime.DateTime;
                fileInfo.CreationTime = entry.LastWriteTime.DateTime;
                fileInfo.LastAccessTime = DateTime.Now; // Or entry.LastWriteTime.DateTime
                info.IsDirectory = false;
                if (info.Context is MemoryStream ms)
                {
                    fileInfo.Length = ms.Length;
                }
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

        var isExplicitDirEntry = _zipEntries.TryGetValue(normalizedPath, out var dirEntry) && IsDirectoryEntry(dirEntry);
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
                    if (path.Equals(searchPrefix, StringComparison.OrdinalIgnoreCase)) return false; // Don't list itself
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
                    LastWriteTime = entry.LastWriteTime.DateTime,
                    CreationTime = entry.LastWriteTime.DateTime,
                    LastAccessTime = DateTime.Now // Or entry.LastWriteTime.DateTime
                };
                if (IsDirectoryEntry(entry))
                {
                    fi.Attributes = FileAttributes.Directory;
                    var tempFullName = entry.FullName.TrimEnd('/');
                    fi.FileName = tempFullName.Split('/').LastOrDefault(static s => !string.IsNullOrEmpty(s)) ?? entry.Name;
                }
                else
                {
                    fi.Attributes = FileAttributes.Archive | FileAttributes.ReadOnly;
                    fi.Length = entry.Length;
                    fi.FileName = entry.Name;
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
                .Where(k => childEntries.All(e => NormalizePath(e.FullName).TrimEnd('/') != k)) // Only if not already listed as explicit
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
            return DokanResult.Error; // Or another appropriate error code
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
        // var normalizedPath = NormalizePath(fileName);
        // Console.WriteLine($"ZipFs.Cleanup: Path='{fileName}' (Norm='{normalizedPath}'), Context? {info.Context != null}");
        if (info.Context is IDisposable disposableContext) disposableContext.Dispose();
        info.Context = null;
    }

    public void CloseFile(string fileName, IDokanFileInfo info)
    {
        // Context should have been disposed in Cleanup.
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
        // Read-only, so locking is trivial
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
        catch (ArgumentException ex) // Regex pattern compilation error
        {
            _logErrorAction(ex, $"Invalid search pattern '{searchPattern}' in FindFilesWithPattern for path '{fileName}'.");
            files = new List<FileInformation>(); // Return empty list on pattern error
            return DokanResult.InvalidParameter; // Or some other error
        }

        return DokanResult.Success;
    }

    private static bool IsMatchSimple(string input, string pattern)
    {
        if (pattern.Equals("*", StringComparison.Ordinal) || pattern.Equals("*.*", StringComparison.Ordinal)) return true;

        var regexPattern = "^" + Regex.Escape(pattern).Replace("\\*", ".*").Replace("\\?", ".") + "$";
        // ArgumentException from Regex.IsMatch is caught by the caller (FindFilesWithPattern)
        return Regex.IsMatch(input, regexPattern, RegexOptions.IgnoreCase);
    }

    public NtStatus GetFileSecurity(string fileName, out FileSystemSecurity? security, AccessControlSections sections, IDokanFileInfo info)
    {
        security = null;
        try
        {
            var fs = new FileSecurity();
            fs.AddAccessRule(new FileSystemAccessRule("Everyone", FileSystemRights.ReadAndExecute, AccessControlType.Allow));
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

    public void Dispose()
    {
        // The ZipArchive was created with leaveOpen: true, so it will not dispose the underlying _sourceZipStream.
        // The _sourceZipStream is managed by an 'await using' block in Program.cs and will be disposed there.
        // We only need to dispose the ZipArchive instance itself.
        _zipArchive?.Dispose();
        GC.SuppressFinalize(this);
    }
}
