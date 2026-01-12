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

    private readonly Action<Exception?, string?> _logErrorAction;
    private readonly object _zipLock = new();

    // Cache for large files extracted to disk. Key: normalized path in ZIP, Value: path to the temp file.
    private readonly Dictionary<string, string?> _largeFileCache = new(StringComparer.OrdinalIgnoreCase);
    private const int MaxMemorySize = 536870912; // 512 MB (512x1024x1024)
    private readonly string _tempDirectoryPath;

    private const string VolumeLabel = "SimpleZipDrive";
    private static readonly char[] Separator = { '/' };

    public ZipFs(Stream zipFileStream, string mountPoint, Action<Exception?, string?> logErrorAction)
    {
        _sourceZipStream = zipFileStream;
        _logErrorAction = logErrorAction ?? throw new ArgumentNullException(nameof(logErrorAction));
        _tempDirectoryPath = Path.Combine(Path.GetTempPath(), "SimpleZipDrive");

        try
        {
            // Ensure the dedicated temporary directory exists for this session.
            Directory.CreateDirectory(_tempDirectoryPath);

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

        if (_zipEntries.TryGetValue(normalizedPath, out var entry))
        {
            if (entry.IsDirectory)
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
                    // Hybrid caching - memory for small files, temp disk file for large files
                    if (entry.Size >= MaxMemorySize)
                    {
                        // --- Large file: Cache to disk ---
                        string? cachedPath;
                        lock (_largeFileCache)
                        {
                            if (!_largeFileCache.TryGetValue(normalizedPath, out cachedPath))
                            {
                                Console.WriteLine($"Large file detected: '{normalizedPath}' ({entry.Size / 1024.0 / 1024.0:F2} MB). Extracting to temporary disk cache...");
                                var newTempFilePath = Path.Combine(_tempDirectoryPath, Guid.NewGuid().ToString("N") + ".tmp");

                                // --- Disk space check ---
                                try
                                {
                                    var tempDrivePathRoot = Path.GetPathRoot(newTempFilePath) ?? "C:\\";
                                    var tempDrive = new DriveInfo(tempDrivePathRoot);
                                    if (tempDrive.AvailableFreeSpace < entry.Size)
                                    {
                                        var errorMessage = $"Insufficient disk space to extract large file '{normalizedPath}' ({entry.Size / 1024.0 / 1024.0:F2} MB).";
                                        _logErrorAction(new IOException(errorMessage), "ZipFs.CreateFile: Disk space check failed.");
                                        return DokanResult.DiskFull;
                                    }
                                }
                                catch (Exception driveEx)
                                {
                                    _logErrorAction(driveEx, $"Error checking disk space for large file extraction of '{normalizedPath}'.");
                                }

                                lock (_zipLock)
                                {
                                    using var entryStream = _zipFile.GetInputStream(entry);
                                    using var tempFileStream = new FileStream(newTempFilePath, FileMode.Create, System.IO.FileAccess.Write, FileShare.None);
                                    entryStream.CopyTo(tempFileStream);
                                }

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
                        // --- Small file: Cache in memory ---
                        byte[] entryBytes;
                        lock (_zipLock)
                        {
                            using var entryStream = _zipFile.GetInputStream(entry);
                            using var tempMs = new MemoryStream((int)Math.Max(0, entry.Size));
                            entryStream.CopyTo(tempMs);
                            entryBytes = tempMs.ToArray();
                        }

                        info.Context = new MemoryStream(entryBytes);
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
                catch (ZipException zipEx)
                {
                    var contextMessage = $"ZipFs.CreateFile: A ZipException occurred while trying to read the file entry '{normalizedPath}'. This often indicates the file's data within the ZIP is corrupt.";
                    _logErrorAction(zipEx, contextMessage);
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

        if (info.IsDirectory)
        {
            return DokanResult.AccessDenied;
        }

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
        var normalizedPath = NormalizePath(fileName);

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

    public void Dispose()
    {
        _zipFile?.Close();

        // When the drive is unmounted, clean up all temporary files that were created.
        lock (_largeFileCache)
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

        GC.SuppressFinalize(this);
    }
}