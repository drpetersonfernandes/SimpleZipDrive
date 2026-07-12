using System.Security.AccessControl;
using System.Security.Principal;
using DokanNet;
using SharpCompress.Common;
using SharpCompress.Compressors.Deflate;
using SharpCompress.Compressors.ZStandard;
using DokanFileAccess = DokanNet.FileAccess;

namespace SimpleZipDrive;

/// <summary>
/// Dokan-based virtual filesystem that exposes archive entries as read-only files and directories.
/// Delegates core logic to <see cref="ZipFileSystemCore"/>.
/// </summary>
public class ZipFs : IDokanOperations, IDisposable
{
    private readonly Action<Exception?, string?> _logErrorAction;

    /// <summary>
    /// Initializes a new instance of the <see cref="ZipFs"/> class.
    /// </summary>
    /// <param name="archiveStream">Seekable stream containing the archive data.</param>
    /// <param name="mountPoint">Dokan mount point (drive letter or folder path).</param>
    /// <param name="logErrorAction">Callback invoked when an error is logged.</param>
    /// <param name="passwordProvider">Function that returns the archive password, or <see langword="null"/> if not encrypted.</param>
    /// <param name="archiveType">Archive format identifier (e.g., "zip", "7z", "rar").</param>
    /// <param name="maxMemorySize">Maximum in-memory cache size per entry in bytes.</param>
    /// <param name="volumeLabel">Optional volume label. Defaults to <see cref="ZipFileSystemCore.DefaultVolumeLabel"/>.</param>
    public ZipFs(Stream archiveStream, string mountPoint, Action<Exception?, string?> logErrorAction, Func<string?> passwordProvider, string archiveType, long maxMemorySize = ZipFileSystemCore.DefaultMaxMemorySize, string? volumeLabel = null)
    {
        Core = new ZipFileSystemCore(archiveStream, mountPoint, logErrorAction, passwordProvider, archiveType, maxMemorySize, volumeLabel);
        _logErrorAction = logErrorAction;
    }

    /// <summary>Gets or sets the current total memory consumed by in-memory cached entries.</summary>
    public long CurrentMemoryUsage
    {
        get => Core.CurrentMemoryUsage;
        internal set => Core.CurrentMemoryUsage = value;
    }

    /// <summary>Gets the maximum total memory that can be used for in-memory caching.</summary>
    public long MaxTotalMemoryCache => Core.MaxTotalMemoryCache;

    internal ZipFileSystemCore Core { get; }

    private NtStatus ValidatePathLength(string path, string operationName)
    {
        return Core.ValidatePathLength(path, operationName) ? DokanResult.Success : DokanResult.Error;
    }

    /// <inheritdoc cref="IDokanOperations.CreateFile"/>
    public NtStatus CreateFile(
        string fileName,
        DokanFileAccess access,
        FileShare share,
        FileMode mode,
        FileOptions options,
        FileAttributes attributes,
        IDokanFileInfo info)
    {
        DiagnosticLogger.Log($"  CreateFile: ENTER \"{fileName}\" mode={mode}, access={access}");

        var pathValidationResult = ValidatePathLength(fileName, nameof(CreateFile));
        if (pathValidationResult != DokanResult.Success)
            return pathValidationResult;

        if (Core.IsDisposed)
            return DokanResult.NotReady;

        Core.TryResolvePath(fileName, out var normalizedPath);

        var node = Core.GetEntryNode(normalizedPath);
        bool isImplicitDir;
        if (node == null)
        {
            isImplicitDir = normalizedPath == "/";
        }
        else
        {
            isImplicitDir = node.IsDir;
        }

        if (node is { IsDir: false })
        {
            // It's a file
            info.IsDirectory = false;

            if (Core.IsFailedEntry(normalizedPath))
            {
                return DokanResult.Error;
            }

            switch (mode)
            {
                case FileMode.Open:
                case FileMode.OpenOrCreate:
                case FileMode.Create:
                    break;
                case FileMode.CreateNew:
                    return DokanResult.FileExists;
                default:
                    return DokanResult.AccessDenied;
            }

            try
            {
                var entry = node.Entry;
                if (entry != null)
                {
                    var stream = Core.OpenEntryStream(entry, normalizedPath);
                    if (stream == null)
                    {
                        // Race condition: entry may have been marked as failed by a concurrent thread
                        // between the IsFailedEntry check above and the OpenEntryStream call.
                        if (Core.IsFailedEntry(normalizedPath))
                        {
                            return DokanResult.Error;
                        }

                        _logErrorAction(new InvalidOperationException($"ZipFs.CreateFile: OpenEntryStream returned null for '{normalizedPath}' but entry is not in the failed list."), "ZipFs.CreateFile: Unexpected null stream.");
                        return DokanResult.Error;
                    }

                    info.Context = stream;
                }

                return DokanResult.Success;
            }
            catch (CryptographicException cryptoEx)
            {
                var contextMessage = $"ZipFs.CreateFile: Password error for '{normalizedPath}'. The provided password may be incorrect or missing.";
                ZipFileSystemCore.LogMessage($"{AppTheme.Warning} Password Error: Could not decrypt '{normalizedPath}'.");
                _logErrorAction(cryptoEx, contextMessage);
                (info.Context as IDisposable)?.Dispose();
                info.Context = null;
                return DokanResult.Error;
            }
            catch (IOException ioEx) when ((uint)ioEx.HResult == 0x80070015)
            {
                var msg = $"CRITICAL ERROR: The source drive containing the archive file is no longer ready. " +
                          $"Please check the connection to drive '{Path.GetPathRoot(Core.TempDirectoryPath)}'.";
                ZipFileSystemCore.LogMessage($"{AppTheme.Critical} {msg}");
                _logErrorAction(ioEx, $"ZipFs.CreateFile: Source drive not ready for '{normalizedPath}'");
                (info.Context as IDisposable)?.Dispose();
                info.Context = null;
                return DokanResult.NotReady;
            }
            catch (IOException ioEx) when ((uint)ioEx.HResult == 0x800703EE || (uint)ioEx.HResult == 0x80070037)
            {
                ZipFileSystemCore.LogMessage($"{AppTheme.Section("SOURCE FILE ACCESS ERROR")}");
                ZipFileSystemCore.LogMessage("Error: The source archive file is no longer accessible.");
                ZipFileSystemCore.LogMessage($"Details: {ioEx.Message}");
                ZipFileSystemCore.LogMessage("This usually means:");
                ZipFileSystemCore.LogMessage($"{AppTheme.Bullet}The external drive/USB device was disconnected");
                ZipFileSystemCore.LogMessage($"{AppTheme.Bullet}The archive file was modified or deleted after mounting started");
                ZipFileSystemCore.LogMessage($"{AppTheme.Bullet}The source device is no longer available or has errors");
                ZipFileSystemCore.LogMessage("Please verify the drive is connected and the file has not been altered.");
                _logErrorAction(ioEx, $"ZipFs.CreateFile: Source file inaccessible for entry '{normalizedPath}'");
                (info.Context as IDisposable)?.Dispose();
                info.Context = null;
                return DokanResult.Error;
            }
            catch (IOException ioEx)
            {
                // Disk space exhaustion, temp file open failure, or other IO errors during caching.
                ZipFileSystemCore.LogMessage($"{AppTheme.Warning} IO Error: Cannot cache '{normalizedPath}'.");
                ZipFileSystemCore.LogMessage($"Details: {ioEx.Message}");
                _logErrorAction(ioEx, $"ZipFs.CreateFile: IO error caching entry '{normalizedPath}'.");
                (info.Context as IDisposable)?.Dispose();
                info.Context = null;
                return DokanResult.Error;
            }
            catch (ZstdException zstdEx)
            {
                Core.AddFailedEntry(normalizedPath);
                _logErrorAction(zstdEx, $"ZipFs.CreateFile: ZstdException decompressing entry '{normalizedPath}'.");
                ZipFileSystemCore.LogMessage($"{AppTheme.Warning} Decompression Error: Cannot read '{normalizedPath}'. The file data may be corrupted or use an unsupported compression format.");
                (info.Context as IDisposable)?.Dispose();
                info.Context = null;
                return DokanResult.Error;
            }
            catch (ZlibException zlibEx)
            {
                if (node.Entry != null)
                {
                    var contextMessage = $"ZipFs.CreateFile: Deflate decompression error for '{normalizedPath}' ({node.Entry.Size / 1024.0:F1} KB). The zip entry data is corrupted or uses an unsupported compression method.";
                    ZipFileSystemCore.LogMessage($"{AppTheme.Warning} Decompression Error: Cannot read '{normalizedPath}'.");
                    ZipFileSystemCore.LogMessage("The compressed data in this file could not be decompressed.");
                    ZipFileSystemCore.LogMessage("This may indicate file corruption or an incompatible compression method.");
                    ZipFileSystemCore.LogMessage($"{AppTheme.Bullet}Try extracting this file directly with WinRAR or 7-Zip. If that also fails, the file may be damaged.");
                    _logErrorAction(zlibEx, contextMessage);
                }

                Core.AddFailedEntry(normalizedPath);
                (info.Context as IDisposable)?.Dispose();
                info.Context = null;
                return DokanResult.Error;
            }
            catch (ArgumentOutOfRangeException argEx)
            {
                if (node.Entry != null)
                {
                    var contextMessage = $"ZipFs.CreateFile: Invalid data offset for '{normalizedPath}' ({node.Entry.Size / 1024.0:F1} KB). The zip archive appears to be corrupted or truncated — the entry header points to an invalid file position.";
                    ZipFileSystemCore.LogMessage($"{AppTheme.Warning} Corruption Error: Cannot read '{normalizedPath}'. The archive file may be damaged or incomplete.");
                    _logErrorAction(argEx, contextMessage);
                }

                Core.AddFailedEntry(normalizedPath);
                (info.Context as IDisposable)?.Dispose();
                info.Context = null;
                return DokanResult.Error;
            }
            catch (NullReferenceException nre)
            {
                Core.AddFailedEntry(normalizedPath);
                _logErrorAction(nre, $"ZipFs.CreateFile: NullReferenceException during decompression of '{normalizedPath}' (likely SharpCompress RAR V1 unpacker bug). Entry marked as failed to prevent retries.");
                ZipFileSystemCore.LogMessage($"{AppTheme.Warning} Decompression Error: Cannot read '{normalizedPath}'. The entry may use an unsupported or buggy compression method.");
                (info.Context as IDisposable)?.Dispose();
                info.Context = null;
                return DokanResult.Error;
            }
            catch (Exception ex) when (ZipFsHelpers.IsDataErrorException(ex))
            {
                Core.AddFailedEntry(normalizedPath);
                if (node.Entry != null)
                {
                    var contextMessage = $"ZipFs.CreateFile: Data error (corrupted or unsupported compression) for '{normalizedPath}' ({node.Entry.Size / 1024.0:F1} KB). The archive entry may be damaged or uses an unsupported compression method.";
                    _logErrorAction(ex, contextMessage);
                }

                ZipFileSystemCore.LogMessage($"{AppTheme.Warning} Decompression Error: Cannot read '{normalizedPath}'. The file data appears to be corrupted or uses an unsupported compression method.");
                (info.Context as IDisposable)?.Dispose();
                info.Context = null;
                return DokanResult.Error;
            }
            catch (ArchiveOperationException archiveOpEx)
            {
                Core.AddFailedEntry(normalizedPath);
                _logErrorAction(archiveOpEx, $"ZipFs.CreateFile: ArchiveOperationException during extraction of '{normalizedPath}'. Entry marked as failed.");
                ZipFileSystemCore.LogMessage($"{AppTheme.Warning} Decompression Error: Cannot read '{normalizedPath}'. The file data may be corrupted.");
                (info.Context as IDisposable)?.Dispose();
                info.Context = null;
                return DokanResult.Error;
            }
            catch (Exception ex)
            {
                _logErrorAction(ex, $"ZipFs.CreateFile: EXCEPTION caching entry '{normalizedPath}'.");
                (info.Context as IDisposable)?.Dispose();
                info.Context = null;
                return DokanResult.Error;
            }
        }
        else if (node is { IsDir: true } || isImplicitDir)
        {
            // Directory
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

        return DokanResult.PathNotFound;
    }

    /// <inheritdoc cref="IDokanOperations.ReadFile"/>
    public NtStatus ReadFile(
        string fileName,
        byte[] buffer,
        out int bytesRead,
        long offset,
        IDokanFileInfo info)
    {
        DiagnosticLogger.Log($"  ReadFile: ENTER \"{fileName}\" offset={offset}, length={buffer.Length}");
        bytesRead = 0;

        if (info.IsDirectory)
        {
            return DokanResult.AccessDenied;
        }

        var pathValidationResult = ValidatePathLength(fileName, nameof(ReadFile));
        if (pathValidationResult != DokanResult.Success)
            return pathValidationResult;

        Core.TryResolvePath(fileName, out var normalizedPath);

        // Fast path: the per-handle stream created in CreateFile is available.
        if (info.Context is Stream stream)
        {
            try
            {
                bytesRead = Core.ReadStream(stream, offset, buffer, 0, buffer.Length);
                return DokanResult.Success;
            }
            catch (Exception ex)
            {
                _logErrorAction(ex, $"ZipFs.ReadFile: EXCEPTION reading from stream for '{normalizedPath}', Offset={offset}.");
                return DokanResult.Error;
            }
        }

        // No per-handle stream in the context. During paging I/O this is expected — for
        // example, when Windows Explorer generates thumbnails via memory-mapped reads it
        // issues ReadFile on a file object that was never routed through CreateFile. Rather
        // than failing the read (which corrupts thumbnails/previews), open a transient
        // stream for the entry, satisfy this read, then dispose it. This mirrors the Dokan
        // mirror sample's handling of a missing handle context during paging I/O.
        if (info.PagingIo)
        {
            return ReadFileOnDemand(normalizedPath, buffer, out bytesRead, offset);
        }

        _logErrorAction(
            new InvalidOperationException($"ReadFile called for '{normalizedPath}' but info.Context did not contain a Stream and the read was not paging I/O. The handle may have already been closed."),
            "ZipFs.ReadFile: Context is not a Stream - handle already cleaned up.");
        return DokanResult.InvalidHandle;
    }

    private NtStatus ReadFileOnDemand(string normalizedPath, byte[] buffer, out int bytesRead, long offset)
    {
        bytesRead = 0;

        var node = Core.GetEntryNode(normalizedPath);
        if (node is not { IsDir: false, Entry: not null })
        {
            _logErrorAction(
                new InvalidOperationException($"ReadFile called for '{normalizedPath}' without a handle context and the entry could not be resolved for an on-demand read."),
                "ZipFs.ReadFile: Missing context and entry not found.");
            return DokanResult.InvalidHandle;
        }

        if (Core.IsFailedEntry(normalizedPath))
        {
            return DokanResult.Error;
        }

        Stream? transientStream = null;
        try
        {
            transientStream = Core.OpenEntryStream(node.Entry, normalizedPath);
            if (transientStream == null)
            {
                return DokanResult.Error;
            }

            bytesRead = Core.ReadStream(transientStream, offset, buffer, 0, buffer.Length);
            return DokanResult.Success;
        }
        catch (Exception ex)
        {
            _logErrorAction(ex, $"ZipFs.ReadFile: EXCEPTION during on-demand read for '{normalizedPath}', Offset={offset}.");
            return DokanResult.Error;
        }
        finally
        {
            transientStream?.Dispose();
        }
    }

    /// <inheritdoc cref="IDokanOperations.GetFileInformation"/>
    public NtStatus GetFileInformation(string fileName, out FileInformation fileInfo, IDokanFileInfo info)
    {
        DiagnosticLogger.Log($"  GetFileInformation: ENTER \"{fileName}\"");
        fileInfo = new FileInformation();

        var pathValidationResult = ValidatePathLength(fileName, nameof(GetFileInformation));
        if (pathValidationResult != DokanResult.Success)
            return pathValidationResult;

        Core.TryResolvePath(fileName, out var normalizedPath);

        var node = Core.GetEntryNode(normalizedPath);
        if (node != null)
        {
            if (node.IsDir)
            {
                fileInfo.Attributes = FileAttributes.Directory;
                if (node.Entry?.Key != null)
                {
                    fileInfo.FileName = Path.GetFileName(node.Entry.Key.TrimEnd('/', '\\'));
                }
                else
                {
                    fileInfo.FileName = normalizedPath.Split('/').LastOrDefault(static s => !string.IsNullOrEmpty(s)) ?? "";
                }

                fileInfo.LastWriteTime = node.LastWriteTime;
                fileInfo.CreationTime = node.CreationTime;
                fileInfo.LastAccessTime = node.LastAccessTime;
                info.IsDirectory = true;
            }
            else
            {
                if (node.Entry?.Key == null)
                    return DokanResult.Error;

                fileInfo.Attributes = FileAttributes.Archive | FileAttributes.ReadOnly;
                fileInfo.FileName = Path.GetFileName(node.Entry.Key);
                fileInfo.Length = node.FileSize;
                fileInfo.LastWriteTime = node.LastWriteTime;
                fileInfo.CreationTime = node.CreationTime;
                fileInfo.LastAccessTime = node.LastAccessTime;
                info.IsDirectory = false;
            }

            return DokanResult.Success;
        }

        return DokanResult.PathNotFound;
    }

    /// <inheritdoc cref="IDokanOperations.FindFiles"/>
    public NtStatus FindFiles(string fileName, out IList<FileInformation> files, IDokanFileInfo info)
    {
        DiagnosticLogger.Log($"  FindFiles: ENTER \"{fileName}\"");

        var pathValidationResult = ValidatePathLength(fileName, nameof(FindFiles));
        if (pathValidationResult != DokanResult.Success)
        {
            files = Array.Empty<FileInformation>();
            return pathValidationResult;
        }

        Core.TryResolvePath(fileName, out var normalizedPath);
        var resultFiles = new List<FileInformation>();

        try
        {
            var entries = Core.ListDirectory(normalizedPath);
            if (entries.Count == 0)
            {
                files = Array.Empty<FileInformation>();
                return DokanResult.PathNotFound;
            }

            foreach (var node in entries)
            {
                var name = node.NormalizedPath.Split('/').LastOrDefault(static s => !string.IsNullOrEmpty(s)) ?? "";
                resultFiles.Add(new FileInformation
                {
                    FileName = name,
                    Attributes = node.IsDir ? FileAttributes.Directory : (FileAttributes.Archive | FileAttributes.ReadOnly),
                    Length = node.IsDir ? 0 : node.FileSize,
                    LastWriteTime = node.LastWriteTime,
                    CreationTime = node.CreationTime,
                    LastAccessTime = node.LastAccessTime
                });
            }
        }
        catch (Exception ex)
        {
            _logErrorAction(ex, $"Error in FindFiles for path '{fileName}'.");
            files = Array.Empty<FileInformation>();
            return DokanResult.Error;
        }

        files = resultFiles;
        return DokanResult.Success;
    }

    /// <inheritdoc cref="IDokanOperations.GetVolumeInformation"/>
    public NtStatus GetVolumeInformation(out string volumeLabel, out FileSystemFeatures features,
        out string fileSystemName, out uint maximumComponentLength, IDokanFileInfo info)
    {
        DiagnosticLogger.Log("  GetVolumeInformation: ENTER");
        volumeLabel = Core.VolumeLabel;
        features = FileSystemFeatures.ReadOnlyVolume | FileSystemFeatures.CasePreservedNames | FileSystemFeatures.UnicodeOnDisk;
        fileSystemName = "ZipFS";
        maximumComponentLength = 255;
        return DokanResult.Success;
    }

    /// <inheritdoc cref="IDokanOperations.Mounted"/>
    public NtStatus Mounted(string mountPoint, IDokanFileInfo info)
    {
        return DokanResult.Success;
    }

    /// <inheritdoc cref="IDokanOperations.Unmounted"/>
    public NtStatus Unmounted(IDokanFileInfo info)
    {
        return DokanResult.Success;
    }

    /// <inheritdoc cref="IDokanOperations.Cleanup"/>
    public void Cleanup(string fileName, IDokanFileInfo info)
    {
    }

    /// <inheritdoc cref="IDokanOperations.CloseFile"/>
    public void CloseFile(string fileName, IDokanFileInfo info)
    {
        if (info.Context is IDisposable disposableContext)
        {
            disposableContext.Dispose();
        }

        info.Context = null;
    }

    /// <inheritdoc cref="IDokanOperations.GetDiskFreeSpace"/>
    public NtStatus GetDiskFreeSpace(out long freeBytesAvailable, out long totalNumberOfBytes, out long totalNumberOfFreeBytes, IDokanFileInfo info)
    {
        totalNumberOfBytes = Core.TotalSize;
        freeBytesAvailable = 0;
        totalNumberOfFreeBytes = 0;
        return DokanResult.Success;
    }

    /// <inheritdoc cref="IDokanOperations.WriteFile"/>
    public NtStatus WriteFile(string fileName, byte[] buffer, out int bytesWritten, long offset, IDokanFileInfo info)
    {
        bytesWritten = 0;
        return DokanResult.AccessDenied;
    }

    /// <inheritdoc cref="IDokanOperations.FlushFileBuffers"/>
    public NtStatus FlushFileBuffers(string fileName, IDokanFileInfo info)
    {
        return DokanResult.AccessDenied;
    }

    /// <inheritdoc cref="IDokanOperations.SetFileAttributes"/>
    public NtStatus SetFileAttributes(string fileName, FileAttributes attributes, IDokanFileInfo info)
    {
        return DokanResult.AccessDenied;
    }

    /// <inheritdoc cref="IDokanOperations.SetFileTime"/>
    public NtStatus SetFileTime(string fileName, DateTime? creationTime, DateTime? lastAccessTime, DateTime? lastWriteTime, IDokanFileInfo info)
    {
        return DokanResult.AccessDenied;
    }

    /// <inheritdoc cref="IDokanOperations.DeleteFile"/>
    public NtStatus DeleteFile(string fileName, IDokanFileInfo info)
    {
        return DokanResult.AccessDenied;
    }

    /// <inheritdoc cref="IDokanOperations.DeleteDirectory"/>
    public NtStatus DeleteDirectory(string fileName, IDokanFileInfo info)
    {
        return DokanResult.AccessDenied;
    }

    /// <inheritdoc cref="IDokanOperations.MoveFile"/>
    public NtStatus MoveFile(string oldName, string newName, bool replace, IDokanFileInfo info)
    {
        return DokanResult.AccessDenied;
    }

    /// <inheritdoc cref="IDokanOperations.SetEndOfFile"/>
    public NtStatus SetEndOfFile(string fileName, long length, IDokanFileInfo info)
    {
        return DokanResult.AccessDenied;
    }

    /// <inheritdoc cref="IDokanOperations.SetAllocationSize"/>
    public NtStatus SetAllocationSize(string fileName, long length, IDokanFileInfo info)
    {
        return DokanResult.AccessDenied;
    }

    /// <inheritdoc cref="IDokanOperations.LockFile"/>
    public NtStatus LockFile(string fileName, long offset, long length, IDokanFileInfo info)
    {
        return DokanResult.Success;
    }

    /// <inheritdoc cref="IDokanOperations.UnlockFile"/>
    public NtStatus UnlockFile(string fileName, long offset, long length, IDokanFileInfo info)
    {
        return DokanResult.Success;
    }

    /// <inheritdoc cref="IDokanOperations.FindStreams"/>
    public NtStatus FindStreams(string fileName, out IList<FileInformation> streams, IDokanFileInfo info)
    {
        streams = Array.Empty<FileInformation>();
        return DokanResult.NotImplemented;
    }

    /// <inheritdoc cref="IDokanOperations.FindFilesWithPattern"/>
    public NtStatus FindFilesWithPattern(string fileName, string searchPattern, out IList<FileInformation> files, IDokanFileInfo info)
    {
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
            files = allFiles.Where(f => ZipFsHelpers.IsMatchSimple(f.FileName, searchPattern)).ToList();
        }
        catch (ArgumentException ex)
        {
            _logErrorAction(ex, $"Invalid search pattern '{searchPattern}' in FindFilesWithPattern for path '{fileName}'.");
            files = new List<FileInformation>();
            return DokanResult.InvalidParameter;
        }

        return DokanResult.Success;
    }

    /// <inheritdoc cref="IDokanOperations.GetFileSecurity"/>
    public NtStatus GetFileSecurity(string fileName, out FileSystemSecurity? security, AccessControlSections sections, IDokanFileInfo info)
    {
        security = null;

        var pathValidationResult = ValidatePathLength(fileName, nameof(GetFileSecurity));
        if (pathValidationResult != DokanResult.Success)
            return pathValidationResult;

        try
        {
            Core.TryResolvePath(fileName, out var normalizedPath);
            var node = Core.GetEntryNode(normalizedPath);
            var isDirectory = node?.IsDir ?? normalizedPath == "/";

            var everyoneSid = new SecurityIdentifier("S-1-1-0");

            if (isDirectory)
            {
                var ds = new DirectorySecurity();
                ds.AddAccessRule(new FileSystemAccessRule(everyoneSid, FileSystemRights.Read,
                    AccessControlType.Allow));
                ds.SetOwner(everyoneSid);
                ds.SetGroup(everyoneSid);
                security = ds;
            }
            else
            {
                var fs = new FileSecurity();
                fs.AddAccessRule(new FileSystemAccessRule(everyoneSid, FileSystemRights.ReadAndExecute,
                    AccessControlType.Allow));
                fs.SetOwner(everyoneSid);
                fs.SetGroup(everyoneSid);
                security = fs;
            }

            return DokanResult.Success;
        }
        catch (Exception ex)
        {
            _logErrorAction(ex, $"Error in GetFileSecurity for '{fileName}'.");
            return DokanResult.Error;
        }
    }

    /// <inheritdoc cref="IDokanOperations.SetFileSecurity"/>
    public NtStatus SetFileSecurity(string fileName, FileSystemSecurity security, AccessControlSections sections, IDokanFileInfo info)
    {
        return DokanResult.AccessDenied;
    }

    /// <summary>
    /// Releases all resources used by the <see cref="ZipFs"/> instance, including the underlying archive and temp files.
    /// </summary>
    public void Dispose()
    {
        Core.Dispose();
        GC.SuppressFinalize(this);
    }
}
