using System.Diagnostics.CodeAnalysis;
using System.Security.AccessControl;
using Fsp;
using Fsp.Interop;
using SharpCompress.Archives;
using SharpCompress.Common;
using SharpCompress.Compressors.Deflate;

namespace SimpleZipDrive_WinFsp;

[SuppressMessage("ReSharper", "InconsistentNaming")]
public sealed class ZipFs : FileSystemBase, IDisposable
{
    private readonly ZipFileSystemCore _core;
    private readonly Action<Exception?, string?> _logErrorAction;

    internal long _currentMemoryUsage
    {
        get => _core.CurrentMemoryUsage;
        set => _core.CurrentMemoryUsage = value;
    }

    internal long _maxTotalMemoryCache => _core.MaxTotalMemoryCache;
    internal Dictionary<string, IArchiveEntry> _archiveEntries => _core.ArchiveEntries;

    internal bool IsStoredEntry(IArchiveEntry entry)
    {
        return _core.IsStoredEntry(entry);
    }

    public ZipFs(Stream archiveStream, string mountPoint, Action<Exception?, string?> logErrorAction, Func<string?> passwordProvider, string archiveType, long maxMemorySize = ZipFileSystemCore.DefaultMaxMemorySize)
    {
        _core = new ZipFileSystemCore(archiveStream, mountPoint, logErrorAction, passwordProvider, archiveType, maxMemorySize);
        _logErrorAction = logErrorAction;

        _core.DumpEntries(30);
    }

    private int ValidatePathLength(string path, string operationName)
    {
        return _core.ValidatePathLength(path, operationName) ? STATUS_SUCCESS : STATUS_UNSUCCESSFUL;
    }

    public override int Init(object Host)
    {
        if (Host is FileSystemHost host)
        {
            host.CasePreservedNames = true;
            host.UnicodeOnDisk = true;
            host.PersistentAcls = false;
            host.PostCleanupWhenModifiedOnly = true;
            host.PassQueryDirectoryPattern = true;
            host.FlushAndPurgeOnCleanup = true;
            host.FileSystemName = "SimpleZipDrive";
        }

        return STATUS_SUCCESS;
    }

    [SuppressMessage("ReSharper", "ConditionalAccessQualifierIsNonNullableAccordingToAPIContract")]
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
        var result = OpenOrCreateFile(FileName, out FileNode, out FileDesc, out FileInfo, out NormalizedName);
        DiagnosticLogger.LogOperation("Create", FileName, result, $"options=0x{CreateOptions:X8}, access=0x{GrantedAccess:X8}, attrs=0x{FileAttributes:X8}, node={FileNode?.GetType().Name}, desc={FileDesc?.GetType().Name}");
        return result;
    }

    [SuppressMessage("ReSharper", "ConditionalAccessQualifierIsNonNullableAccordingToAPIContract")]
    public override int Open(
        string FileName,
        uint CreateOptions,
        uint GrantedAccess,
        out object FileNode,
        out object FileDesc,
        out Fsp.Interop.FileInfo FileInfo,
        out string NormalizedName)
    {
        var result = OpenOrCreateFile(FileName, out FileNode, out FileDesc, out FileInfo, out NormalizedName);
        DiagnosticLogger.LogOperation("Open", FileName, result, $"options=0x{CreateOptions:X8}, access=0x{GrantedAccess:X8}, node={FileNode?.GetType().Name}, desc={FileDesc?.GetType().Name}");
        return result;
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

    internal int OpenOrCreateFile(
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
        {
            DiagnosticLogger.LogOperation("OpenOrCreateFile", fileName, pathValidationResult, "path validation failed");
            return pathValidationResult;
        }

        if (_core.IsDisposed)
        {
            DiagnosticLogger.LogOperation("OpenOrCreateFile", fileName, STATUS_DEVICE_NOT_READY, "disposed");
            return STATUS_DEVICE_NOT_READY;
        }

        _core.TryResolvePath(fileName, out var normalizedPath);
        normalizedPath = ZipFsHelpers.ResolveSpecialPaths(normalizedPath);
        normalizedName = normalizedPath == "/" ? "\\" : normalizedPath.Replace('/', '\\');

        var node = _core.GetEntryNode(normalizedPath);
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
                DiagnosticLogger.LogOperation("OpenOrCreateFile", fileName, STATUS_SUCCESS, "root directory created");
            }
            else
            {
                DiagnosticLogger.LogOperation("OpenOrCreateFile", fileName, STATUS_OBJECT_NAME_NOT_FOUND, "not found");
                return STATUS_OBJECT_NAME_NOT_FOUND;
            }
        }

        if (node.IsDir)
        {
            fileNode = node;
            fileDesc = node;
            fileInfo = EntryNodeToFileInfo(node);
            DiagnosticLogger.LogOperation("OpenOrCreateFile", fileName, STATUS_SUCCESS, "directory");
            return STATUS_SUCCESS;
        }

        if (node.Entry == null)
        {
            DiagnosticLogger.LogOperation("OpenOrCreateFile", fileName, STATUS_OBJECT_NAME_NOT_FOUND, "entry has null Entry");
            return STATUS_OBJECT_NAME_NOT_FOUND;
        }

        var entry = node.Entry;

        if (_core.IsFailedEntry(normalizedPath))
        {
            DiagnosticLogger.LogOperation("OpenOrCreateFile", fileName, STATUS_UNSUCCESSFUL, "in failed entries");
            return STATUS_UNSUCCESSFUL;
        }

        try
        {
            var stream = _core.OpenEntryStream(entry, normalizedPath);
            if (stream == null)
            {
                DiagnosticLogger.LogOperation("OpenOrCreateFile", fileName, STATUS_UNSUCCESSFUL, "stream creation returned null");
                return STATUS_UNSUCCESSFUL;
            }

            fileNode = node;
            fileDesc = stream;
            fileInfo = EntryNodeToFileInfo(node);
            DiagnosticLogger.LogOperation("OpenOrCreateFile", fileName, STATUS_SUCCESS, $"stream created ({stream.GetType().Name})");
            return STATUS_SUCCESS;
        }
        catch (CryptographicException cryptoEx)
        {
            DiagnosticLogger.LogOperation("OpenOrCreateFile", fileName, STATUS_UNSUCCESSFUL, $"CryptographicException: {cryptoEx.Message}");
            var contextMessage = $"ZipFs.Create: Password error for '{normalizedPath}'. The provided password may be incorrect or missing.";
            ZipFileSystemCore.LogMessage($"{AppTheme.Warning} Password Error: Could not decrypt '{normalizedPath}'.");
            _logErrorAction(cryptoEx, contextMessage);
            fileNode = null!;
            fileDesc = null!;
            return STATUS_UNSUCCESSFUL;
        }
        catch (IOException ioEx) when ((uint)ioEx.HResult == 0x80070015)
        {
            DiagnosticLogger.LogOperation("OpenOrCreateFile", fileName, STATUS_DEVICE_NOT_READY, "IOException: source drive not ready (0x80070015)");
            var msg = $"CRITICAL ERROR: The source drive containing the archive file is no longer ready. " +
                      $"Please check the connection to drive '{Path.GetPathRoot(_core.TempDirectoryPath)}'.";
            ZipFileSystemCore.LogMessage($"{AppTheme.Critical} {msg}");
            _logErrorAction(ioEx, $"ZipFs.OpenOrCreateFile: Source drive not ready for '{normalizedPath}'");
            fileNode = null!;
            fileDesc = null!;
            return STATUS_DEVICE_NOT_READY;
        }
        catch (IOException ioEx) when ((uint)ioEx.HResult == 0x800703EE || (uint)ioEx.HResult == 0x80070037)
        {
            DiagnosticLogger.LogOperation("OpenOrCreateFile", fileName, STATUS_UNSUCCESSFUL, $"IOException: source file inaccessible (0x{ioEx.HResult:X8})");
            ZipFileSystemCore.LogMessage($"{AppTheme.Section("SOURCE FILE ACCESS ERROR")}");
            ZipFileSystemCore.LogMessage("Error: The source archive file is no longer accessible.");
            ZipFileSystemCore.LogMessage($"Details: {ioEx.Message}");
            ZipFileSystemCore.LogMessage("This usually means:");
            ZipFileSystemCore.LogMessage($"{AppTheme.Bullet}The external drive/USB device was disconnected");
            ZipFileSystemCore.LogMessage($"{AppTheme.Bullet}The archive file was modified or deleted after mounting started");
            ZipFileSystemCore.LogMessage($"{AppTheme.Bullet}The source device is no longer available or has errors");
            ZipFileSystemCore.LogMessage("Please verify the drive is connected and the file has not been altered.");
            _logErrorAction(ioEx, $"ZipFs.Create: Source file inaccessible for entry '{normalizedPath}'");
            fileNode = null!;
            fileDesc = null!;
            return STATUS_UNSUCCESSFUL;
        }
        catch (ZlibException zlibEx)
        {
            DiagnosticLogger.LogOperation("OpenOrCreateFile", fileName, STATUS_UNSUCCESSFUL, $"ZlibException: {zlibEx.Message}");
            var contextMessage = $"ZipFs.Create: Deflate decompression error for '{normalizedPath}' ({entry.Size / 1024.0:F1} KB).";
            ZipFileSystemCore.LogMessage($"{AppTheme.Warning} Decompression Error: Cannot read '{normalizedPath}'.");
            _logErrorAction(zlibEx, contextMessage);
            _core.AddFailedEntry(normalizedPath);
            fileNode = null!;
            fileDesc = null!;
            return STATUS_UNSUCCESSFUL;
        }
        catch (ArgumentOutOfRangeException argEx)
        {
            DiagnosticLogger.LogOperation("OpenOrCreateFile", fileName, STATUS_UNSUCCESSFUL, $"ArgumentOutOfRangeException: {argEx.Message}");
            ZipFileSystemCore.LogMessage($"{AppTheme.Warning} Corruption Error: Cannot read '{normalizedPath}'. The archive file may be damaged or incomplete.");
            _logErrorAction(argEx, $"ZipFs.Create: Invalid data offset for '{normalizedPath}'.");
            _core.AddFailedEntry(normalizedPath);
            fileNode = null!;
            fileDesc = null!;
            return STATUS_UNSUCCESSFUL;
        }
        catch (NullReferenceException nre)
        {
            DiagnosticLogger.LogOperation("OpenOrCreateFile", fileName, STATUS_UNSUCCESSFUL, $"NullReferenceException: {nre.Message}");
            _core.AddFailedEntry(normalizedPath);
            _logErrorAction(nre, $"ZipFs.Create: NullReferenceException during decompression of '{normalizedPath}'.");
            ZipFileSystemCore.LogMessage($"{AppTheme.Warning} Decompression Error: Cannot read '{normalizedPath}'.");
            fileNode = null!;
            fileDesc = null!;
            return STATUS_UNSUCCESSFUL;
        }
        catch (Exception ex) when (ZipFsHelpers.IsDataErrorException(ex))
        {
            DiagnosticLogger.LogOperation("OpenOrCreateFile", fileName, STATUS_UNSUCCESSFUL, $"DataError: {ex.Message}");
            _core.AddFailedEntry(normalizedPath);
            _logErrorAction(ex, $"ZipFs.Create: Data error for '{normalizedPath}'.");
            ZipFileSystemCore.LogMessage($"{AppTheme.Warning} Decompression Error: Cannot read '{normalizedPath}'.");
            fileNode = null!;
            fileDesc = null!;
            return STATUS_UNSUCCESSFUL;
        }
        catch (Exception ex)
        {
            DiagnosticLogger.LogOperation("OpenOrCreateFile", fileName, STATUS_UNSUCCESSFUL, $"Exception: {ex.GetType().Name}: {ex.Message}");
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
            var readBuffer = new byte[Length];
            var read = _core.ReadStream(stream, (long)Offset, readBuffer, 0, (int)Length);
            if (read > 0)
            {
                System.Runtime.InteropServices.Marshal.Copy(readBuffer, 0, Buffer, read);
            }

            BytesTransferred = (uint)read;
            return STATUS_SUCCESS;
        }
        catch (Exception ex)
        {
            DiagnosticLogger.LogOperation("Read", $"Offset={Offset}, Length={Length}", STATUS_UNSUCCESSFUL, $"{ex.GetType().Name}: {ex.Message}");
            _logErrorAction(ex, $"ZipFs.Read: EXCEPTION reading from stream, Offset={Offset}.");
            return STATUS_UNSUCCESSFUL;
        }
    }

    public override int ReadDirectory(
        object FileNode,
        object FileDesc,
        string Pattern,
        string Marker,
        IntPtr Buffer,
        uint Length,
        out uint BytesTransferred)
    {
        DiagnosticLogger.Log($"  ReadDirectory: ENTER Pattern=\"{Pattern}\" Marker=\"{Marker}\" Length={Length}");
        try
        {
            var result = base.ReadDirectory(FileNode, FileDesc, Pattern, Marker, Buffer, Length, out BytesTransferred);
            DiagnosticLogger.LogOperation("ReadDirectory", $"Pattern=\"{Pattern}\" Marker=\"{Marker}\"", result, $"bytes={BytesTransferred}");
            return result;
        }
        catch (Exception ex)
        {
            DiagnosticLogger.LogOperation("ReadDirectory", $"Pattern=\"{Pattern}\" Marker=\"{Marker}\"", STATUS_UNSUCCESSFUL, $"EXCEPTION: {ex.GetType().Name}: {ex.Message}");
            _logErrorAction(ex, "ZipFs.ReadDirectory: EXCEPTION.");
            BytesTransferred = 0;
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
            DiagnosticLogger.LogOperation("GetFileInfo", node.NormalizedPath, STATUS_SUCCESS, $"IsDir={node.IsDir}, Size={node.FileSize}");
            return STATUS_SUCCESS;
        }

        DiagnosticLogger.LogOperation("GetFileInfo", "?", STATUS_UNSUCCESSFUL, "FileNode is not EntryNode");
        FileInfo = default;
        return STATUS_UNSUCCESSFUL;
    }

    public override int GetDirInfoByName(
        object FileNode,
        object FileDesc,
        string FileName,
        out string NormalizedName,
        out Fsp.Interop.FileInfo FileInfo)
    {
        NormalizedName = FileName;
        FileInfo = default;

        if (FileNode is not EntryNode { IsDir: true } dirNode)
        {
            DiagnosticLogger.LogOperation("GetDirInfoByName", FileName, STATUS_NOT_A_DIRECTORY, "parent is not a directory");
            return STATUS_NOT_A_DIRECTORY;
        }

        var parentPath = dirNode.NormalizedPath;
        var searchPrefix = parentPath == "/" ? "/" : parentPath + "/";
        var normalizedFileName = FileName.Replace('\\', '/');
        var childPath = searchPrefix + normalizedFileName;
        childPath = ZipFsHelpers.ResolveSpecialPaths(childPath);

        var childNode = _core.GetEntryNode(childPath);
        if (childNode == null)
        {
            DiagnosticLogger.LogOperation("GetDirInfoByName", FileName, STATUS_OBJECT_NAME_NOT_FOUND, "not found");
            return STATUS_OBJECT_NAME_NOT_FOUND;
        }

        NormalizedName = childNode.NormalizedPath.Split('/').LastOrDefault(static s => !string.IsNullOrEmpty(s)) ?? FileName;
        FileInfo = EntryNodeToFileInfo(childNode);
        DiagnosticLogger.LogOperation("GetDirInfoByName", FileName, STATUS_SUCCESS, $"resolved to {childNode.NormalizedPath}");
        return STATUS_SUCCESS;
    }

    public override int GetVolumeInfo(out VolumeInfo VolumeInfo)
    {
        VolumeInfo = default;
        VolumeInfo.TotalSize = (ulong)_core.TotalSize;
        VolumeInfo.FreeSize = 0;
        VolumeInfo.SetVolumeLabel(ZipFileSystemCore.VolumeLabel);
        DiagnosticLogger.Log($"  GetVolumeInfo: label={ZipFileSystemCore.VolumeLabel}, size={VolumeInfo.TotalSize / 1024.0 / 1024.0:F2} MB");
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

        try
        {
            var pathValidationResult = ValidatePathLength(FileName, nameof(GetSecurityByName));
            if (pathValidationResult != STATUS_SUCCESS)
                return pathValidationResult;

            _core.TryResolvePath(FileName, out var normalizedPath);
            normalizedPath = ZipFsHelpers.ResolveSpecialPaths(normalizedPath);
            var node = _core.GetEntryNode(normalizedPath);

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

                DiagnosticLogger.LogOperation("GetSecurityByName", FileName, STATUS_SUCCESS, $"found, IsDir={node.IsDir}, Attrs=0x{FileAttributes:X}");
                return STATUS_SUCCESS;
            }

            if (normalizedPath == "/" || normalizedPath.Equals("\\", StringComparison.OrdinalIgnoreCase))
            {
                FileAttributes = (uint)System.IO.FileAttributes.Directory;
                DiagnosticLogger.LogOperation("GetSecurityByName", FileName, STATUS_SUCCESS, "root dir");
                return STATUS_SUCCESS;
            }

            DiagnosticLogger.LogOperation("GetSecurityByName", FileName, STATUS_OBJECT_NAME_NOT_FOUND, "not found");
            return STATUS_OBJECT_NAME_NOT_FOUND;
        }
        catch (Exception ex)
        {
            DiagnosticLogger.LogOperation("GetSecurityByName", FileName, STATUS_UNSUCCESSFUL, $"EXCEPTION: {ex.GetType().Name}: {ex.Message}");
            _logErrorAction(ex, $"ZipFs.GetSecurityByName: EXCEPTION for '{FileName}'.");
            return STATUS_UNSUCCESSFUL;
        }
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
        FileName = null!;
        FileInfo = default;
        DiagnosticLogger.Log($"  ReadDirectoryEntry: ENTER Pattern=\"{Pattern}\" Marker=\"{Marker}\" ContextIsNull={false}");

        try
        {
            string normalizedPath;
            if (FileNode is EntryNode node)
            {
                normalizedPath = node.NormalizedPath;
            }
            else
            {
                DiagnosticLogger.LogOperation("ReadDirectoryEntry", "?", false, "FileNode is not EntryNode");
                return false;
            }

            if (Context is not (List<(string Name, Fsp.Interop.FileInfo Info)> entries, int currentIndex))
            {
                var dirEntries = _core.ListDirectory(normalizedPath);
                if (dirEntries.Count == 0)
                {
                    DiagnosticLogger.LogOperation("ReadDirectoryEntry", normalizedPath, false, "not a directory");
                    return false;
                }

                var seenFileNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                entries = new List<(string, Fsp.Interop.FileInfo)>();

                var dotNode = _core.GetEntryNode(normalizedPath);
                if (dotNode != null)
                {
                    if (string.IsNullOrEmpty(Marker))
                    {
                        entries.Add((".", EntryNodeToFileInfo(dotNode)));
                        seenFileNames.Add(".");
                    }

                    var parentPath = normalizedPath == "/" ? "/" : ZipFsHelpers.GetParentPath(normalizedPath);
                    if (parentPath != null)
                    {
                        var dotdotNode = _core.GetEntryNode(parentPath);
                        if (dotdotNode != null &&
                            (string.IsNullOrEmpty(Marker) || string.Equals(Marker, ".", StringComparison.OrdinalIgnoreCase)))
                        {
                            entries.Add(("..", EntryNodeToFileInfo(dotdotNode)));
                            seenFileNames.Add("..");
                        }
                    }
                }

                foreach (var child in dirEntries)
                {
                    var name = child.NormalizedPath.Split('/').LastOrDefault(static s => !string.IsNullOrEmpty(s)) ?? "";
                    if (!string.IsNullOrEmpty(name) && seenFileNames.Add(name))
                    {
                        if (!ZipFsHelpers.IsNameMatch(name, Pattern))
                            continue;

                        var fileSize = child.IsDir ? 0ul : (ulong)child.FileSize;
                        entries.Add((name, new Fsp.Interop.FileInfo
                        {
                            FileAttributes = child.IsDir ? (uint)FileAttributes.Directory : (uint)(FileAttributes.Archive | FileAttributes.ReadOnly),
                            FileSize = fileSize,
                            AllocationSize = child.IsDir ? 0ul : (ulong)((child.FileSize + 4095) / 4096 * 4096),
                            CreationTime = DateTimeToFileTimeUtc(child.CreationTime),
                            LastAccessTime = DateTimeToFileTimeUtc(child.LastAccessTime),
                            LastWriteTime = DateTimeToFileTimeUtc(child.LastWriteTime),
                            ChangeTime = DateTimeToFileTimeUtc(child.LastWriteTime)
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
                DiagnosticLogger.LogOperation("ReadDirectoryEntry", normalizedPath, true, $"initialized: {entries.Count} entries, marker=\"{Marker}\", pattern=\"{Pattern}\"");
            }

            if (currentIndex >= entries.Count)
            {
                DiagnosticLogger.LogOperation("ReadDirectoryEntry", normalizedPath, false, $"done (returned {currentIndex} of {entries.Count})");
                return false;
            }

            var entry2 = entries[currentIndex];
            Context = (entries, currentIndex + 1);

            FileName = entry2.Name;
            FileInfo = entry2.Info;
            return true;
        }
        catch (Exception ex)
        {
            DiagnosticLogger.LogOperation("ReadDirectoryEntry", "?", false, $"EXCEPTION: {ex.GetType().Name}: {ex.Message}");
            _logErrorAction(ex, "ZipFs.ReadDirectoryEntry: EXCEPTION during directory enumeration.");
            return false;
        }
    }

    public override void Cleanup(
        object FileNode,
        object FileDesc,
        string FileName,
        uint Flags)
    {
        DiagnosticLogger.LogOperation("Cleanup", FileName, STATUS_SUCCESS);
    }

    public override void Close(
        object FileNode,
        object FileDesc)
    {
        var nodePath = (FileNode as EntryNode)?.NormalizedPath ?? "?";
        DiagnosticLogger.LogOperation("Close", nodePath, STATUS_SUCCESS);
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

    internal static ulong DateTimeToFileTimeUtc(DateTime dt)
    {
        if (dt == DateTime.MinValue)
        {
            dt = new DateTime(1601, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        }

        var utc = dt.ToUniversalTime();
        var fileTime = (utc.Ticks - new DateTime(1601, 1, 1, 0, 0, 0, DateTimeKind.Utc).Ticks);
        return fileTime > 0 ? (ulong)fileTime : 0;
    }

    internal static Fsp.Interop.FileInfo EntryNodeToFileInfo(EntryNode node)
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

    public void Dispose()
    {
        _core.Dispose();
    }
}
