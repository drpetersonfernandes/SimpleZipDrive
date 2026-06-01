using System.Security.AccessControl;
using System.Security.Principal;
using DokanNet;
using SharpCompress.Common;
using SharpCompress.Compressors.Deflate;
using DokanFileAccess = DokanNet.FileAccess;

namespace SimpleZipDrive;

public class ZipFs : IDokanOperations, IDisposable
{
    private readonly Action<Exception?, string?> _logErrorAction;

    public ZipFs(Stream archiveStream, string mountPoint, Action<Exception?, string?> logErrorAction, Func<string?> passwordProvider, string archiveType, long maxMemorySize = ZipFileSystemCore.DefaultMaxMemorySize)
    {
        Core = new ZipFileSystemCore(archiveStream, mountPoint, logErrorAction, passwordProvider, archiveType, maxMemorySize);
        _logErrorAction = logErrorAction;
    }

    public long CurrentMemoryUsage
    {
        get => Core.CurrentMemoryUsage;
        internal set => Core.CurrentMemoryUsage = value;
    }

    public long MaxTotalMemoryCache => Core.MaxTotalMemoryCache;
    internal ZipFileSystemCore Core { get; }

    private NtStatus ValidatePathLength(string path, string operationName)
    {
        return Core.ValidatePathLength(path, operationName) ? DokanResult.Success : DokanResult.Error;
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
                var entry = node.Entry!;
                var stream = Core.OpenEntryStream(entry, normalizedPath);
                if (stream == null)
                {
                    _logErrorAction(new InvalidOperationException($"ZipFs.CreateFile: Context was not a Stream for file '{normalizedPath}' after caching attempt."), "ZipFs.CreateFile: Context invalid post-caching.");
                    return DokanResult.Error;
                }

                info.Context = stream;
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
            catch (ZlibException zlibEx)
            {
                var contextMessage = $"ZipFs.CreateFile: Deflate decompression error for '{normalizedPath}' ({node.Entry!.Size / 1024.0:F1} KB). The zip entry uses a compression method that may not be fully supported by the SharpCompress library.";
                ZipFileSystemCore.LogMessage($"{AppTheme.Warning} Decompression Error: Cannot read '{normalizedPath}'.");
                ZipFileSystemCore.LogMessage("This file uses a compression method that is not fully compatible with SimpleZipDrive's decompression library.");
                ZipFileSystemCore.LogMessage("The archive file itself is likely fine - this is a library limitation, not file corruption.");
                ZipFileSystemCore.LogMessage($"{AppTheme.Bullet}Try extracting this file directly with WinRAR or 7-Zip instead.");
                _logErrorAction(zlibEx, contextMessage);
                Core.AddFailedEntry(normalizedPath);
                (info.Context as IDisposable)?.Dispose();
                info.Context = null;
                return DokanResult.Error;
            }
            catch (ArgumentOutOfRangeException argEx)
            {
                var contextMessage = $"ZipFs.CreateFile: Invalid data offset for '{normalizedPath}' ({node.Entry!.Size / 1024.0:F1} KB). The zip archive appears to be corrupted or truncated — the entry header points to an invalid file position.";
                ZipFileSystemCore.LogMessage($"{AppTheme.Warning} Corruption Error: Cannot read '{normalizedPath}'. The archive file may be damaged or incomplete.");
                _logErrorAction(argEx, contextMessage);
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
                var contextMessage = $"ZipFs.CreateFile: Data error (corrupted or unsupported compression) for '{normalizedPath}' ({node.Entry!.Size / 1024.0:F1} KB). The archive entry may be damaged or uses an unsupported compression method.";
                _logErrorAction(ex, contextMessage);
                ZipFileSystemCore.LogMessage($"{AppTheme.Warning} Decompression Error: Cannot read '{normalizedPath}'. The file data appears to be corrupted or uses an unsupported compression method.");
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

        var normalizedPath = ZipFsHelpers.NormalizePath(fileName);

        if (info.Context is not Stream stream)
        {
            _logErrorAction(
                new InvalidOperationException($"ReadFile called for '{normalizedPath}' but info.Context was null. This indicates CloseFile was already called."),
                "ZipFs.ReadFile: Context is null - handle already cleaned up.");
            return DokanResult.InvalidHandle;
        }

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

    public NtStatus GetVolumeInformation(out string volumeLabel, out FileSystemFeatures features,
        out string fileSystemName, out uint maximumComponentLength, IDokanFileInfo info)
    {
        DiagnosticLogger.Log("  GetVolumeInformation: ENTER");
        volumeLabel = ZipFileSystemCore.VolumeLabel;
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
    }

    public void CloseFile(string fileName, IDokanFileInfo info)
    {
        if (info.Context is IDisposable disposableContext)
        {
            disposableContext.Dispose();
        }

        info.Context = null;
    }

    public NtStatus GetDiskFreeSpace(out long freeBytesAvailable, out long totalNumberOfBytes, out long totalNumberOfFreeBytes, IDokanFileInfo info)
    {
        totalNumberOfBytes = Core.TotalSize;
        freeBytesAvailable = 0;
        totalNumberOfFreeBytes = 0;
        return DokanResult.Success;
    }

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

    public NtStatus SetFileSecurity(string fileName, FileSystemSecurity security, AccessControlSections sections, IDokanFileInfo info)
    {
        return DokanResult.AccessDenied;
    }

    public void Dispose()
    {
        Core.Dispose();
        GC.SuppressFinalize(this);
    }
}
