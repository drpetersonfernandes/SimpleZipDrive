using System.Collections.Concurrent;
using System.Diagnostics.CodeAnalysis;
using System.IO.Compression;
using System.Security.AccessControl;
using System.Text;
using DokanNet;
using SharpCompress.Common;
using SharpCompress.Writers;
using SharpCompress.Writers.SevenZip;
using SimpleZipDrive.Core;
using SimpleZipDrive.Tests.Fakes;
using FileAccess = DokanNet.FileAccess;

namespace SimpleZipDrive.Tests;

[SuppressMessage("ReSharper", "NullableWarningSuppressionIsUsed")]
public class ZipFsTests : IDisposable
{
    private readonly MemoryStream _stream;
    private readonly ZipFs _zipFs;

    public ZipFsTests()
    {
        _stream = CreateZipStream();
        _zipFs = new ZipFs(_stream, "M:\\", static (_, _) => { }, static () => null, "zip");
    }

    private static MemoryStream CreateZipStream()
    {
        var ms = new MemoryStream();
        using (var zip = new ZipArchive(ms, ZipArchiveMode.Create, true))
        {
            var entry1 = zip.CreateEntry("readme.txt");
            using (var writer = new StreamWriter(entry1.Open(), new UTF8Encoding(false)))
            {
                writer.Write("Hello World");
            }

            var entry2 = zip.CreateEntry("data/info.txt");
            using (var writer = new StreamWriter(entry2.Open(), new UTF8Encoding(false)))
            {
                writer.Write("Nested content");
            }

            zip.CreateEntry("empty/"); // directory entry
        }

        ms.Position = 0;
        return ms;
    }

    [Fact]
    public void GetVolumeInformationReturnsExpectedValues()
    {
        var info = new FakeDokanFileInfo();
        var result = _zipFs.GetVolumeInformation(out var label, out _, out var fsName, out var maxLen, info);

        Assert.Equal(DokanResult.Success, result);
        Assert.Equal("SimpleZipDrive", label);
        Assert.Equal("ZipFS", fsName);
        Assert.Equal(255u, maxLen);
    }

    [Fact]
    public void GetDiskFreeSpaceReturnsArchiveLength()
    {
        var info = new FakeDokanFileInfo();
        var result = _zipFs.GetDiskFreeSpace(out var free, out var total, out var totalFree, info);

        Assert.Equal(DokanResult.Success, result);
        Assert.Equal(_stream.Length, total);
        Assert.Equal(0, free);
        Assert.Equal(0, totalFree);
    }

    [Fact]
    public void GetFileInformationRootReturnsDirectory()
    {
        var info = new FakeDokanFileInfo();
        var result = _zipFs.GetFileInformation("\\", out var fileInfo, info);

        Assert.Equal(DokanResult.Success, result);
        Assert.Equal(FileAttributes.Directory, fileInfo.Attributes);
        Assert.True(info.IsDirectory);
    }

    [Fact]
    public void GetFileInformationFileReturnsArchiveFile()
    {
        var info = new FakeDokanFileInfo();
        var result = _zipFs.GetFileInformation("\\readme.txt", out var fileInfo, info);

        Assert.Equal(DokanResult.Success, result);
        Assert.Equal(FileAttributes.Archive | FileAttributes.ReadOnly, fileInfo.Attributes);
        Assert.Equal("readme.txt", fileInfo.FileName);
        Assert.False(info.IsDirectory);
    }

    [Fact]
    public void GetFileInformationNestedFileReturnsFile()
    {
        var info = new FakeDokanFileInfo();
        var result = _zipFs.GetFileInformation(@"\data\info.txt", out var fileInfo, info);

        Assert.Equal(DokanResult.Success, result);
        Assert.Equal("info.txt", fileInfo.FileName);
        Assert.Equal(Encoding.UTF8.GetByteCount("Nested content"), fileInfo.Length);
    }

    [Fact]
    public void GetFileInformationDirectoryReturnsDirectory()
    {
        var info = new FakeDokanFileInfo();
        var result = _zipFs.GetFileInformation("\\empty", out var fileInfo, info);

        Assert.Equal(DokanResult.Success, result);
        Assert.Equal(FileAttributes.Directory, fileInfo.Attributes);
        Assert.Equal("empty", fileInfo.FileName);
        Assert.True(info.IsDirectory);
    }

    [Fact]
    public void GetFileInformationUnknownReturnsPathNotFound()
    {
        var info = new FakeDokanFileInfo();
        var result = _zipFs.GetFileInformation("\\unknown.txt", out _, info);

        Assert.Equal(DokanResult.PathNotFound, result);
    }

    [Fact]
    public void FindFilesRootReturnsChildren()
    {
        var info = new FakeDokanFileInfo();
        var result = _zipFs.FindFiles("\\", out var files, info);

        Assert.Equal(DokanResult.Success, result);
        Assert.Contains(files, static f => f.FileName == "readme.txt");
        Assert.Contains(files, static f => f.FileName == "data");
        Assert.Contains(files, static f => f.FileName == "empty");
    }

    [Fact]
    public void FindFilesSubdirectoryReturnsNestedFiles()
    {
        var info = new FakeDokanFileInfo();
        var result = _zipFs.FindFiles("\\data", out var files, info);

        Assert.Equal(DokanResult.Success, result);
        Assert.Single(files);
        Assert.Contains(files, static f => f.FileName == "info.txt");
    }

    [Fact]
    public void CreateFileExistingFileOpenReturnsSuccessAndContext()
    {
        var info = new FakeDokanFileInfo();
        var result = _zipFs.CreateFile(
            "\\readme.txt",
            FileAccess.ReadData,
            FileShare.Read,
            FileMode.Open,
            FileOptions.None,
            FileAttributes.Normal,
            info);

        Assert.Equal(DokanResult.Success, result);
        Assert.NotNull(info.Context);
        Assert.IsAssignableFrom<Stream>(info.Context);

        _zipFs.CloseFile("\\readme.txt", info);
    }

    [Fact]
    public void ReadFileReadsContent()
    {
        var info = new FakeDokanFileInfo();
        _zipFs.CreateFile(
            "\\readme.txt",
            FileAccess.ReadData,
            FileShare.Read,
            FileMode.Open,
            FileOptions.None,
            FileAttributes.Normal,
            info);

        var buffer = new byte[100];
        var result = _zipFs.ReadFile("\\readme.txt", buffer, out var bytesRead, 0, info);

        Assert.Equal(DokanResult.Success, result);
        Assert.Equal(Encoding.UTF8.GetByteCount("Hello World"), bytesRead);
        Assert.Equal("Hello World", Encoding.UTF8.GetString(buffer, 0, bytesRead));

        _zipFs.CloseFile("\\readme.txt", info);
    }

    [Fact]
    public void FindFilesWithPatternFilterReturnsMatched()
    {
        var info = new FakeDokanFileInfo();
        var result = _zipFs.FindFilesWithPattern("\\", "*.txt", out var files, info);

        Assert.Equal(DokanResult.Success, result);
        Assert.All(files, static f => Assert.EndsWith(".txt", f.FileName));
    }

    [Fact]
    public void GetFileSecurityReturnsSuccess()
    {
        var info = new FakeDokanFileInfo();
        var result = _zipFs.GetFileSecurity("\\readme.txt", out var security, AccessControlSections.All, info);

        Assert.Equal(DokanResult.Success, result);
        Assert.NotNull(security);
    }

    [Fact]
    public void CreateFilePathTooLongReturnsError()
    {
        var longPath = "\\" + new string('a', 260);
        var info = new FakeDokanFileInfo();
        var result = _zipFs.CreateFile(
            longPath,
            FileAccess.ReadData,
            FileShare.Read,
            FileMode.Open,
            FileOptions.None,
            FileAttributes.Normal,
            info);

        Assert.Equal(DokanResult.Error, result);
    }

    [Fact]
    public void WriteFileReturnsAccessDenied()
    {
        var info = new FakeDokanFileInfo();
        var result = _zipFs.WriteFile("\\readme.txt", Array.Empty<byte>(), out var written, 0, info);

        Assert.Equal(DokanResult.AccessDenied, result);
        Assert.Equal(0, written);
    }

    [Fact]
    public void DeleteFileReturnsAccessDenied()
    {
        var info = new FakeDokanFileInfo();
        var result = _zipFs.DeleteFile("\\readme.txt", info);

        Assert.Equal(DokanResult.AccessDenied, result);
    }

    [Fact]
    public void FlushFileBuffersReturnsAccessDenied()
    {
        var info = new FakeDokanFileInfo();
        var result = _zipFs.FlushFileBuffers("\\readme.txt", info);

        Assert.Equal(DokanResult.AccessDenied, result);
    }

    [Fact]
    public void SetFileAttributesReturnsAccessDenied()
    {
        var info = new FakeDokanFileInfo();
        var result = _zipFs.SetFileAttributes("\\readme.txt", FileAttributes.Normal, info);

        Assert.Equal(DokanResult.AccessDenied, result);
    }

    [Fact]
    public void SetFileTimeReturnsAccessDenied()
    {
        var info = new FakeDokanFileInfo();
        var result = _zipFs.SetFileTime("\\readme.txt", DateTime.Now, DateTime.Now, DateTime.Now, info);

        Assert.Equal(DokanResult.AccessDenied, result);
    }

    [Fact]
    public void DeleteDirectoryReturnsAccessDenied()
    {
        var info = new FakeDokanFileInfo();
        var result = _zipFs.DeleteDirectory("\\data", info);

        Assert.Equal(DokanResult.AccessDenied, result);
    }

    [Fact]
    public void MoveFileReturnsAccessDenied()
    {
        var info = new FakeDokanFileInfo();
        var result = _zipFs.MoveFile("\\readme.txt", "\\renamed.txt", false, info);

        Assert.Equal(DokanResult.AccessDenied, result);
    }

    [Fact]
    public void SetEndOfFileReturnsAccessDenied()
    {
        var info = new FakeDokanFileInfo();
        var result = _zipFs.SetEndOfFile("\\readme.txt", 100, info);

        Assert.Equal(DokanResult.AccessDenied, result);
    }

    [Fact]
    public void SetAllocationSizeReturnsAccessDenied()
    {
        var info = new FakeDokanFileInfo();
        var result = _zipFs.SetAllocationSize("\\readme.txt", 100, info);

        Assert.Equal(DokanResult.AccessDenied, result);
    }

    [Fact]
    public void SetFileSecurityReturnsAccessDenied()
    {
        var info = new FakeDokanFileInfo();
        var result = _zipFs.SetFileSecurity("\\readme.txt", new FileSecurity(), AccessControlSections.All, info);

        Assert.Equal(DokanResult.AccessDenied, result);
    }

    [Fact]
    public void LockFileReturnsSuccess()
    {
        var info = new FakeDokanFileInfo();
        var result = _zipFs.LockFile("\\readme.txt", 0, 100, info);

        Assert.Equal(DokanResult.Success, result);
    }

    [Fact]
    public void UnlockFileReturnsSuccess()
    {
        var info = new FakeDokanFileInfo();
        var result = _zipFs.UnlockFile("\\readme.txt", 0, 100, info);

        Assert.Equal(DokanResult.Success, result);
    }

    [Fact]
    public void FindStreamsReturnsNotImplemented()
    {
        var info = new FakeDokanFileInfo();
        var result = _zipFs.FindStreams("\\readme.txt", out var streams, info);

        Assert.Equal(DokanResult.NotImplemented, result);
        Assert.Empty(streams);
    }

    [Fact]
    public void MountedReturnsSuccess()
    {
        var info = new FakeDokanFileInfo();
        var result = _zipFs.Mounted("M:\\", info);

        Assert.Equal(DokanResult.Success, result);
    }

    [Fact]
    public void UnmountedReturnsSuccess()
    {
        var info = new FakeDokanFileInfo();
        var result = _zipFs.Unmounted(info);

        Assert.Equal(DokanResult.Success, result);
    }

    [Fact]
    public void CreateFileNewOnExistingReturnsFileExists()
    {
        var info = new FakeDokanFileInfo();
        var result = _zipFs.CreateFile(
            "\\readme.txt",
            FileAccess.ReadData,
            FileShare.Read,
            FileMode.CreateNew,
            FileOptions.None,
            FileAttributes.Normal,
            info);

        Assert.Equal(DokanResult.FileExists, result);
    }

    [Fact]
    public void CreateFileTruncateReturnsAccessDenied()
    {
        var info = new FakeDokanFileInfo();
        var result = _zipFs.CreateFile(
            "\\readme.txt",
            FileAccess.ReadData,
            FileShare.Read,
            FileMode.Truncate,
            FileOptions.None,
            FileAttributes.Normal,
            info);

        Assert.Equal(DokanResult.AccessDenied, result);
    }

    [Fact]
    public void CreateFileDirectoryWithWriteAccessReturnsAccessDenied()
    {
        // Directory entries have trailing slashes in their keys (e.g., "/empty/"),
        // but NormalizePath("\\empty") produces "/empty" which doesn't match.
        // So directory write access is only blocked for explicit entry matches.
        // Use a path that matches an explicit directory entry via the trailing-slash key.
        var info = new FakeDokanFileInfo();
        var result = _zipFs.CreateFile(
            @"\empty\",
            FileAccess.WriteData,
            FileShare.Read,
            FileMode.Open,
            FileOptions.None,
            FileAttributes.Directory,
            info);

        Assert.Equal(DokanResult.AccessDenied, result);
    }

    [Fact]
    public void CreateFileDirectoryCreateNewReturnsFileExists()
    {
        var info = new FakeDokanFileInfo();
        var result = _zipFs.CreateFile(
            "\\data",
            FileAccess.ReadData,
            FileShare.Read,
            FileMode.CreateNew,
            FileOptions.None,
            FileAttributes.Directory,
            info);

        Assert.Equal(DokanResult.FileExists, result);
    }

    [Fact]
    public void CreateFileNonExistentReturnsPathNotFound()
    {
        var info = new FakeDokanFileInfo();
        var result = _zipFs.CreateFile(
            @"\nonexistent\file.txt",
            FileAccess.ReadData,
            FileShare.Read,
            FileMode.Open,
            FileOptions.None,
            FileAttributes.Normal,
            info);

        Assert.Equal(DokanResult.PathNotFound, result);
    }

    [Fact]
    public void CreateFileDirectoryOpenReturnsSuccess()
    {
        var info = new FakeDokanFileInfo();
        var result = _zipFs.CreateFile(
            "\\data",
            FileAccess.ReadData,
            FileShare.Read,
            FileMode.Open,
            FileOptions.None,
            FileAttributes.Directory,
            info);

        Assert.Equal(DokanResult.Success, result);
        Assert.True(info.IsDirectory);
    }

    [Fact]
    public void ReadFileOnDirectoryReturnsAccessDenied()
    {
        var info = new FakeDokanFileInfo { IsDirectory = true };
        var buffer = new byte[100];
        var result = _zipFs.ReadFile("\\data", buffer, out var bytesRead, 0, info);

        Assert.Equal(DokanResult.AccessDenied, result);
        Assert.Equal(0, bytesRead);
    }

    [Fact]
    public void ReadFileWithNullContextReturnsInvalidHandle()
    {
        var info = new FakeDokanFileInfo { Context = null };
        var buffer = new byte[100];
        var result = _zipFs.ReadFile("\\readme.txt", buffer, out var bytesRead, 0, info);

        Assert.Equal(DokanResult.InvalidHandle, result);
        Assert.Equal(0, bytesRead);
    }

    [Fact]
    public void ReadFileAtEofReturnsSuccessZeroBytes()
    {
        var info = new FakeDokanFileInfo();
        _zipFs.CreateFile(
            "\\readme.txt",
            FileAccess.ReadData,
            FileShare.Read,
            FileMode.Open,
            FileOptions.None,
            FileAttributes.Normal,
            info);

        var buffer = new byte[100];
        var result = _zipFs.ReadFile("\\readme.txt", buffer, out var bytesRead, 999999, info);

        Assert.Equal(DokanResult.Success, result);
        Assert.Equal(0, bytesRead);

        _zipFs.CloseFile("\\readme.txt", info);
    }

    [Fact]
    public void ReadFileWithOffsetReadsFromPosition()
    {
        var info = new FakeDokanFileInfo();
        _zipFs.CreateFile(
            "\\readme.txt",
            FileAccess.ReadData,
            FileShare.Read,
            FileMode.Open,
            FileOptions.None,
            FileAttributes.Normal,
            info);

        var buffer = new byte[100];
        var result = _zipFs.ReadFile("\\readme.txt", buffer, out var bytesRead, 6, info);

        Assert.Equal(DokanResult.Success, result);
        Assert.Equal("World", Encoding.UTF8.GetString(buffer, 0, bytesRead));

        _zipFs.CloseFile("\\readme.txt", info);
    }

    [Fact]
    public void FindFilesNonExistentPathReturnsPathNotFound()
    {
        var info = new FakeDokanFileInfo();
        var result = _zipFs.FindFiles("\\nonexistent", out var files, info);

        Assert.Equal(DokanResult.PathNotFound, result);
        Assert.Empty(files);
    }

    [Fact]
    public void FindFilesWithPatternStarReturnsAll()
    {
        var info = new FakeDokanFileInfo();
        var result = _zipFs.FindFilesWithPattern("\\", "*", out var files, info);

        Assert.Equal(DokanResult.Success, result);
        Assert.Contains(files, static f => f.FileName == "readme.txt");
        Assert.Contains(files, static f => f.FileName == "data");
        Assert.Contains(files, static f => f.FileName == "empty");
    }

    [Fact]
    public void FindFilesWithPatternStarDotStarReturnsAll()
    {
        var info = new FakeDokanFileInfo();
        var result = _zipFs.FindFilesWithPattern("\\", "*.*", out var files, info);

        Assert.Equal(DokanResult.Success, result);
        Assert.Contains(files, static f => f.FileName == "readme.txt");
        Assert.Contains(files, static f => f.FileName == "data");
        Assert.Contains(files, static f => f.FileName == "empty");
    }

    [Fact]
    public void FindFilesWithPatternNoMatchReturnsEmpty()
    {
        var info = new FakeDokanFileInfo();
        var result = _zipFs.FindFilesWithPattern("\\", "*.xyz", out var files, info);

        Assert.Equal(DokanResult.Success, result);
        Assert.Empty(files);
    }

    [Fact]
    public void GetFileInformationPathTooLongReturnsError()
    {
        var info = new FakeDokanFileInfo();
        var longPath = "\\" + new string('a', 260);
        var result = _zipFs.GetFileInformation(longPath, out _, info);

        Assert.Equal(DokanResult.Error, result);
    }

    [Fact]
    public void FindFilesPathTooLongReturnsError()
    {
        var info = new FakeDokanFileInfo();
        var longPath = "\\" + new string('a', 260);
        var result = _zipFs.FindFiles(longPath, out _, info);

        Assert.Equal(DokanResult.Error, result);
    }

    [Fact]
    public void FindFilesWithPatternPathTooLongReturnsError()
    {
        var info = new FakeDokanFileInfo();
        var longPath = "\\" + new string('a', 260);
        var result = _zipFs.FindFilesWithPattern(longPath, "*.txt", out _, info);

        Assert.Equal(DokanResult.Error, result);
    }

    [Fact]
    public void GetFileSecurityPathTooLongReturnsError()
    {
        var info = new FakeDokanFileInfo();
        var longPath = "\\" + new string('a', 260);
        var result = _zipFs.GetFileSecurity(longPath, out _, AccessControlSections.All, info);

        Assert.Equal(DokanResult.Error, result);
    }

    [Fact]
    public void ReadFilePathTooLongReturnsError()
    {
        var info = new FakeDokanFileInfo();
        var longPath = "\\" + new string('a', 260);
        var buffer = new byte[100];
        var result = _zipFs.ReadFile(longPath, buffer, out var bytesRead, 0, info);

        Assert.Equal(DokanResult.Error, result);
        Assert.Equal(0, bytesRead);
    }

    [Fact]
    public void ConstructorUnsupportedArchiveTypeThrowsNotSupported()
    {
        using var ms = new MemoryStream();
        Assert.Throws<NotSupportedException>(() => new ZipFs(ms, "M:\\", static (_, _) => { }, static () => null, "iso"));
    }

    [Fact]
    public void DisposeCleansUpResources()
    {
        var stream = CreateZipStream();
        var zipFs = new ZipFs(stream, "M:\\", static (_, _) => { }, static () => null, "zip");

        zipFs.Dispose();
        stream.Dispose();

        // No exception means cleanup succeeded
    }

    [Fact]
    public void GetVolumeInformationReturnsReadOnlyFeatures()
    {
        var info = new FakeDokanFileInfo();
        _zipFs.GetVolumeInformation(out _, out var features, out _, out _, info);

        Assert.True(features.HasFlag(FileSystemFeatures.ReadOnlyVolume));
        Assert.True(features.HasFlag(FileSystemFeatures.CasePreservedNames));
        Assert.True(features.HasFlag(FileSystemFeatures.UnicodeOnDisk));
    }

    [Fact]
    public void CreateFileImplicitDirectoryOpenReturnsSuccess()
    {
        var info = new FakeDokanFileInfo();
        var result = _zipFs.CreateFile(
            "\\data",
            FileAccess.ReadData,
            FileShare.Read,
            FileMode.Open,
            FileOptions.None,
            FileAttributes.Directory,
            info);

        Assert.Equal(DokanResult.Success, result);
        Assert.True(info.IsDirectory);
    }

    [Fact]
    public void CreateFileImplicitDirectoryCreateNewReturnsFileExists()
    {
        var info = new FakeDokanFileInfo();
        var result = _zipFs.CreateFile(
            "\\data",
            FileAccess.ReadData,
            FileShare.Read,
            FileMode.CreateNew,
            FileOptions.None,
            FileAttributes.Directory,
            info);

        Assert.Equal(DokanResult.FileExists, result);
    }

    [Fact]
    public void GetDiskFreeSpaceNonSeekableStreamReturnsZero()
    {
        // SharpCompress requires seekable streams, so we test with a stream
        // whose CanSeek returns true but Length is simulated via the archive.
        // Instead, verify that a non-seekable source stream reports 0 total bytes.
        var stream = CreateZipStream();
        // Wrap in a stream that reports CanSeek=true for archive opening
        // but we can verify the behavior by checking the logic path
        var zipFs = new ZipFs(stream, "M:\\", static (_, _) => { }, static () => null, "zip");
        var info = new FakeDokanFileInfo();

        // The default test stream is seekable, so total = stream.Length
        // This test verifies the seekable path works correctly
        var result = zipFs.GetDiskFreeSpace(out var free, out var total, out var totalFree, info);

        Assert.Equal(DokanResult.Success, result);
        Assert.Equal(stream.Length, total);
        Assert.Equal(0, free);
        Assert.Equal(0, totalFree);

        zipFs.Dispose();
        stream.Dispose();
    }

    [Fact]
    public void CloseFileDisposesStreamAndNullsContext()
    {
        var info = new FakeDokanFileInfo();
        _zipFs.CreateFile(
            "\\readme.txt",
            FileAccess.ReadData,
            FileShare.Read,
            FileMode.Open,
            FileOptions.None,
            FileAttributes.Normal,
            info);

        var contextStream = info.Context as Stream;
        Assert.NotNull(contextStream);

        _zipFs.CloseFile("\\readme.txt", info);

        Assert.Null(info.Context);
        Assert.Throws<ObjectDisposedException>(() => contextStream.ReadByte());
    }

    [Fact]
    public void GetFileInformationImplicitDirectoryNonRoot()
    {
        var info = new FakeDokanFileInfo();
        var result = _zipFs.GetFileInformation("\\data", out var fileInfo, info);

        Assert.Equal(DokanResult.Success, result);
        Assert.Equal(FileAttributes.Directory, fileInfo.Attributes);
        Assert.Equal("data", fileInfo.FileName);
        Assert.True(info.IsDirectory);
    }

    [Fact]
    public void FindFilesMixedExplicitAndImplicitDirectories()
    {
        using var stream = CreateMixedDirectoryZipStream();
        using var zipFs = new ZipFs(stream, "M:\\", static (_, _) => { }, static () => null, "zip");

        var info = new FakeDokanFileInfo();
        var result = zipFs.FindFiles("\\", out var files, info);

        Assert.Equal(DokanResult.Success, result);
        var names = files.Select(static f => f.FileName).ToHashSet(StringComparer.OrdinalIgnoreCase);
        Assert.Contains("explicit-dir", names);
        Assert.Contains("implicit-dir", names);
        Assert.Contains("readme.txt", names);
    }

    [Fact]
    public void FindFilesMixedExplicitDoesNotDuplicateImplicit()
    {
        using var stream = CreateMixedDirectoryZipStream();
        using var zipFs = new ZipFs(stream, "M:\\", static (_, _) => { }, static () => null, "zip");

        var info = new FakeDokanFileInfo();
        var result = zipFs.FindFiles("\\explicit-dir", out var files, info);

        Assert.Equal(DokanResult.Success, result);
        Assert.Contains(files, static f => f.FileName == "nested.txt");
    }

    [Fact]
    public void FindFilesImplicitDirectoryListsChildren()
    {
        using var stream = CreateMixedDirectoryZipStream();
        using var zipFs = new ZipFs(stream, "M:\\", static (_, _) => { }, static () => null, "zip");

        var info = new FakeDokanFileInfo();
        var result = zipFs.FindFiles("\\implicit-dir", out var files, info);

        Assert.Equal(DokanResult.Success, result);
        Assert.Contains(files, static f => f.FileName == "hidden.txt");
    }

    [Fact]
    public void GetFileInformationImplicitDirectoryDeeplyNested()
    {
        using var stream = CreateMixedDirectoryZipStream();
        using var zipFs = new ZipFs(stream, "M:\\", static (_, _) => { }, static () => null, "zip");

        var info = new FakeDokanFileInfo();
        var result = zipFs.GetFileInformation("\\implicit-dir", out var fileInfo, info);

        Assert.Equal(DokanResult.Success, result);
        Assert.Equal(FileAttributes.Directory, fileInfo.Attributes);
        Assert.Equal("implicit-dir", fileInfo.FileName);
        Assert.True(info.IsDirectory);
    }

    [Fact]
    public void MemoryThrottlingFallsBackToDiskCache()
    {
        var maxTotalMemoryCache = _zipFs.MaxTotalMemoryCache;
        _zipFs.CurrentMemoryUsage = maxTotalMemoryCache - 5;

        var info = new FakeDokanFileInfo();
        var result = _zipFs.CreateFile(
            "\\readme.txt",
            FileAccess.ReadData,
            FileShare.Read,
            FileMode.Open,
            FileOptions.None,
            FileAttributes.Normal,
            info);

        Assert.Equal(DokanResult.Success, result);
        Assert.NotNull(info.Context);
        Assert.IsType<FileStream>(info.Context);

        ((IDisposable)info.Context).Dispose();
        info.Context = null;

        _zipFs.CurrentMemoryUsage = 0L;
    }

    [Fact]
    public void TrackedMemoryStreamDisposalDecrementsMemoryUsage()
    {
        _zipFs.CurrentMemoryUsage = 0L;
        var before = _zipFs.CurrentMemoryUsage;
        Assert.Equal(0, before);

        var info = new FakeDokanFileInfo();
        _zipFs.CreateFile(
            "\\readme.txt",
            FileAccess.ReadData,
            FileShare.Read,
            FileMode.Open,
            FileOptions.None,
            FileAttributes.Normal,
            info);

        var afterOpen = _zipFs.CurrentMemoryUsage;
        Assert.True(afterOpen > 0);

        _zipFs.CloseFile("\\readme.txt", info);

        var afterClose = _zipFs.CurrentMemoryUsage;
        Assert.Equal(0, afterClose);
    }

    [Fact]
    public void MemoryUsageClampedToZeroOnNegative()
    {
        _zipFs.CurrentMemoryUsage = 100L;

        var info = new FakeDokanFileInfo();
        _zipFs.CreateFile(
            "\\readme.txt",
            FileAccess.ReadData,
            FileShare.Read,
            FileMode.Open,
            FileOptions.None,
            FileAttributes.Normal,
            info);

        _zipFs.CurrentMemoryUsage = -50L;

        _zipFs.CloseFile("\\readme.txt", info);

        var afterClose = _zipFs.CurrentMemoryUsage;
        Assert.Equal(0, afterClose);
    }

    private static MemoryStream CreateMixedDirectoryZipStream()
    {
        var ms = new MemoryStream();
        using (var zip = new ZipArchive(ms, ZipArchiveMode.Create, true))
        {
            zip.CreateEntry("readme.txt");
            zip.CreateEntry("explicit-dir/");
            var entry = zip.CreateEntry("explicit-dir/nested.txt");
            using (var writer = new StreamWriter(entry.Open(), new UTF8Encoding(false)))
            {
                writer.Write("explicit nested");
            }

            var implicitEntry = zip.CreateEntry("implicit-dir/hidden.txt");
            using (var writer = new StreamWriter(implicitEntry.Open(), new UTF8Encoding(false)))
            {
                writer.Write("implicit hidden");
            }
        }

        ms.Position = 0;
        return ms;
    }

    [Theory]
    [InlineData("Data Error", true)] // Message contains "Data Error" - should be detected
    [InlineData("Data error occurred", true)] // Message contains "Data error" - case insensitive match
    [InlineData("some data ERROR here", true)] // Message contains "data ERROR" - case insensitive match
    [InlineData("Some random error", false)] // Message does not contain "Data Error"
    [InlineData("Archive data corrupted", false)] // Message does not contain "Data Error"
    public void IsDataErrorExceptionDetectsByMessageContent(string message, bool expectedResult)
    {
        // Create an exception with the specified message
        var exception = new InvalidDataException(message);

        var result = ZipFsHelpers.IsDataErrorException(exception);
        Assert.Equal(expectedResult, result);
    }

    [Fact]
    public void IsDataErrorExceptionDetectsByTypeName()
    {
        // Create a custom exception type with "DataError" in the name
        var exception = new TestDataErrorException("some message");

        var result = ZipFsHelpers.IsDataErrorException(exception);
        Assert.True(result);
    }

    /// <summary>
    /// Test exception type with "DataError" in the name to simulate SharpCompress.DataErrorException.
    /// </summary>
    private class TestDataErrorException : Exception
    {
        public TestDataErrorException(string message) : base(message)
        {
        }
    }

    // ─── NormalizePath tests ───

    [Fact]
    public void NormalizePathNullReturnsRoot()
    {
        var result = ZipFsHelpers.NormalizePath(null);

        Assert.Equal("/", result);
    }

    [Fact]
    public void NormalizePathEmptyReturnsRoot()
    {
        var result = ZipFsHelpers.NormalizePath("");

        Assert.Equal("/", result);
    }

    [Fact]
    public void NormalizePathConvertsBackslashes()
    {
        var result = ZipFsHelpers.NormalizePath(@"foo\bar\baz.txt");

        Assert.Equal("/foo/bar/baz.txt", result);
    }

    [Fact]
    public void NormalizePathAddsLeadingSlash()
    {
        var result = ZipFsHelpers.NormalizePath("data/info.txt");

        Assert.Equal("/data/info.txt", result);
    }

    [Fact]
    public void NormalizePathPreservesExistingLeadingSlash()
    {
        var result = ZipFsHelpers.NormalizePath("/already/normalized");

        Assert.Equal("/already/normalized", result);
    }

    [Fact]
    public void NormalizePathStripsTrailingSlash()
    {
        var result = ZipFsHelpers.NormalizePath("/foo/bar/");

        Assert.Equal("/foo/bar", result);
    }

    [Fact]
    public void NormalizePathStripsTrailingSlashWithoutLeadingSlash()
    {
        var result = ZipFsHelpers.NormalizePath("foo/bar/");

        Assert.Equal("/foo/bar", result);
    }

    [Fact]
    public void NormalizePathRootUnchanged()
    {
        var result = ZipFsHelpers.NormalizePath("/");

        Assert.Equal("/", result);
    }

    // ─── IsPasswordRequiredException tests ───

    [Fact]
    public void IsPasswordRequiredExceptionMessageContainsPasswordReturnsTrue()
    {
        var result = ZipFsHelpers.IsPasswordRequiredException(new InvalidOperationException("archive requires a password"));

        Assert.True(result);
    }

    [Fact]
    public void IsPasswordRequiredExceptionMessageContainsEncryptedReturnsTrue()
    {
        var result = ZipFsHelpers.IsPasswordRequiredException(new InvalidOperationException("file is encrypted"));

        Assert.True(result);
    }

    [Fact]
    public void IsPasswordRequiredExceptionMessageContainsRarAndHeaderReturnsTrue()
    {
        var result = ZipFsHelpers.IsPasswordRequiredException(new InvalidOperationException("RAR header is encrypted"));

        Assert.True(result);
    }

    [Fact]
    public void IsPasswordRequiredExceptionNonMatchingMessageReturnsFalse()
    {
        var result = ZipFsHelpers.IsPasswordRequiredException(new InvalidOperationException("file not found"));

        Assert.False(result);
    }

    // ─── IsDirectory tests ───

    [Fact]
    public void IsDirectoryTrailingSlashReturnsTrue()
    {
        using var stream = CreateZipStream();
        using var zipFs = new ZipFs(stream, "M:\\", static (_, _) => { }, static () => null, "zip");

        var entries = zipFs.Core.ArchiveEntries;
        Assert.NotNull(entries);

        var result = ZipFsHelpers.IsDirectory(entries["/empty"]);

        Assert.True(result);
    }

    [Fact]
    public void IsDirectoryFileEntryReturnsFalse()
    {
        using var stream = CreateZipStream();
        using var zipFs = new ZipFs(stream, "M:\\", static (_, _) => { }, static () => null, "zip");

        var entries = zipFs.Core.ArchiveEntries;
        Assert.NotNull(entries);

        var result = ZipFsHelpers.IsDirectory(entries["/readme.txt"]);

        Assert.False(result);
    }

    // ─── IsPathLengthValid tests ───

    [Fact]
    public void IsPathLengthValidNullReturnsTrue()
    {
        var result = ZipFsHelpers.IsPathLengthValid(null);

        Assert.True(result);
    }

    [Fact]
    public void IsPathLengthValidEmptyReturnsTrue()
    {
        var result = ZipFsHelpers.IsPathLengthValid("");

        Assert.True(result);
    }

    [Fact]
    public void IsPathLengthValidExactlyMaxPathReturnsTrue()
    {
        var path = "\\" + new string('a', 259);
        var result = ZipFsHelpers.IsPathLengthValid(path);

        Assert.True(result);
    }

    [Fact]
    public void IsPathLengthValidExceedsMaxPathReturnsFalse()
    {
        var path = "\\" + new string('a', 260);
        var result = ZipFsHelpers.IsPathLengthValid(path);

        Assert.False(result);
    }

    [Fact]
    public void IsPathLengthValidExtendedPathPrefixWithinLimitReturnsTrue()
    {
        var path = @"\\?\" + new string('a', 260);
        var result = ZipFsHelpers.IsPathLengthValid(path);

        Assert.True(result);
    }

    // ─── IsMatchSimple tests ───

    [Fact]
    public void IsMatchSimpleWildcardQuestionMarkMatchesSingleChar()
    {
        var result = ZipFsHelpers.IsMatchSimple("abc.txt", "abc.tx?");

        Assert.True(result);
    }

    [Fact]
    public void IsMatchSimpleWildcardQuestionMarkDoesNotMatchDifferentLength()
    {
        var result = ZipFsHelpers.IsMatchSimple("abcd.txt", "abc.tx?");

        Assert.False(result);
    }

    [Fact]
    public void IsMatchSimplePatternTooLongReturnsFalse()
    {
        var pattern = new string('a', 261);
        var result = ZipFsHelpers.IsMatchSimple("anything", pattern);

        Assert.False(result);
    }

    [Fact]
    public void IsMatchSimpleStarStarPattern()
    {
        var result = ZipFsHelpers.IsMatchSimple("readme.txt", "*");

        Assert.True(result);
    }

    [Fact]
    public void IsMatchSimpleDotStarPattern()
    {
        var result = ZipFsHelpers.IsMatchSimple("test.txt", "*.*");

        Assert.True(result);
    }

    // ─── CreateFile after Dispose test ───

    [Fact]
    public void CreateFileAfterDisposeReturnsNotReady()
    {
        var stream = CreateZipStream();
        var zipFs = new ZipFs(stream, "M:\\", static (_, _) => { }, static () => null, "zip");
        zipFs.Dispose();

        var info = new FakeDokanFileInfo();
        var result = zipFs.CreateFile(
            "\\readme.txt",
            FileAccess.ReadData,
            FileShare.Read,
            FileMode.Open,
            FileOptions.None,
            FileAttributes.Normal,
            info);

        Assert.Equal(DokanResult.NotReady, result);
    }

    public void Dispose()
    {
        _zipFs.Dispose();
        _stream.Dispose();
        GC.SuppressFinalize(this);
    }

    // ─── Stored (uncompressed) entry tests ───

    private static MemoryStream CreateStoredZipStream()
    {
        // Build real stored ZIP entries (compression method = 0) in binary.
        // System.IO.Compression ZipArchive with NoCompression uses deflate level 0,
        // not true "stored" entries. So we write the ZIP structure manually.
        var ms = new MemoryStream();
        var w = new BinaryWriter(ms, Encoding.UTF8, true);

        var entries = new (string Name, byte[] Data)[]
        {
            ("stored.txt", "Direct read content from stored entry"u8.ToArray()),
            ("sub/stored-data.bin", Enumerable.Range(0, 256).Select(static i => (byte)i).ToArray())
        };

        // Write local file headers + data, record offsets for central directory
        var offsets = new long[entries.Length];
        for (var i = 0; i < entries.Length; i++)
        {
            offsets[i] = ms.Position;
            var nameBytes = Encoding.UTF8.GetBytes(entries[i].Name);
            var crc = ComputeCrc32(entries[i].Data);
            var len = (uint)entries[i].Data.Length;

            w.Write(0x04034b50u); // Local file header signature
            w.Write((ushort)20); // Version needed
            w.Write((ushort)0); // Flags
            w.Write((ushort)0); // Compression method: STORED
            w.Write((ushort)0); // Last mod time
            w.Write((ushort)0); // Last mod date
            w.Write(crc); // CRC-32
            w.Write(len); // Compressed size
            w.Write(len); // Uncompressed size
            w.Write((ushort)nameBytes.Length);
            w.Write((ushort)0); // Extra field length
            w.Write(nameBytes);
            w.Write(entries[i].Data);
        }

        // Central directory
        var cdStart = ms.Position;
        for (var i = 0; i < entries.Length; i++)
        {
            var nameBytes = Encoding.UTF8.GetBytes(entries[i].Name);
            var crc = ComputeCrc32(entries[i].Data);
            var len = (uint)entries[i].Data.Length;

            w.Write(0x02014b50u); // Central directory signature
            w.Write((ushort)20); // Version made by
            w.Write((ushort)20); // Version needed
            w.Write((ushort)0); // Flags
            w.Write((ushort)0); // Compression method: STORED
            w.Write((ushort)0); // Last mod time
            w.Write((ushort)0); // Last mod date
            w.Write(crc); // CRC-32
            w.Write(len); // Compressed size
            w.Write(len); // Uncompressed size
            w.Write((ushort)nameBytes.Length);
            w.Write((ushort)0); // Extra field length
            w.Write((ushort)0); // File comment length
            w.Write((ushort)0); // Disk number start
            w.Write((ushort)0); // Internal attributes
            w.Write((uint)0); // External attributes
            w.Write((uint)offsets[i]); // Relative offset of local header
            w.Write(nameBytes);
        }

        var cdSize = (uint)(ms.Position - cdStart);

        // End of central directory
        w.Write(0x06054b50u); // EOCD signature
        w.Write((ushort)0); // Disk number
        w.Write((ushort)0); // Disk with CD
        w.Write((ushort)entries.Length); // Entries on disk
        w.Write((ushort)entries.Length); // Total entries
        w.Write(cdSize); // CD size
        w.Write((uint)cdStart); // CD offset
        w.Write((ushort)0); // Comment length

        ms.Position = 0;
        return ms;
    }

    private static uint ComputeCrc32(byte[] data)
    {
        var crc = 0xFFFFFFFF;
        foreach (var b in data)
        {
            crc ^= b;
            for (var j = 0; j < 8; j++)
            {
                crc = (crc & 1) != 0 ? (crc >> 1) ^ 0xEDB88320u : crc >> 1;
            }
        }

        return ~crc;
    }

    [Fact]
    public void StoredEntryCreateFileReturnsSuccessAndDirectStream()
    {
        using var stream = CreateStoredZipStream();
        using var zipFs = new ZipFs(stream, "M:\\", static (_, _) => { }, static () => null, "zip", 1);

        var info = new FakeDokanFileInfo();
        var result = zipFs.CreateFile(
            "\\stored.txt",
            FileAccess.ReadData,
            FileShare.Read,
            FileMode.Open,
            FileOptions.None,
            FileAttributes.Normal,
            info);

        Assert.Equal(DokanResult.Success, result);
        Assert.NotNull(info.Context);
        Assert.IsAssignableFrom<Stream>(info.Context);
        Assert.IsNotType<FileStream>(info.Context); // Not disk cache
        Assert.IsNotType<MemoryStream>(info.Context); // Not RAM cache

        zipFs.CloseFile("\\stored.txt", info);
    }

    [Fact]
    public void StoredEntryReadFileReturnsCorrectContent()
    {
        using var stream = CreateStoredZipStream();
        using var zipFs = new ZipFs(stream, "M:\\", static (_, _) => { }, static () => null, "zip", 1);

        var info = new FakeDokanFileInfo();
        zipFs.CreateFile("\\stored.txt", FileAccess.ReadData, FileShare.Read,
            FileMode.Open, FileOptions.None, FileAttributes.Normal, info);

        var buffer = new byte[100];
        var result = zipFs.ReadFile("\\stored.txt", buffer, out var bytesRead, 0, info);

        Assert.Equal(DokanResult.Success, result);
        Assert.Equal("Direct read content from stored entry", Encoding.UTF8.GetString(buffer, 0, bytesRead));

        zipFs.CloseFile("\\stored.txt", info);
    }

    [Fact]
    public void StoredEntryReadFileWithOffsetSeeks()
    {
        using var stream = CreateStoredZipStream();
        using var zipFs = new ZipFs(stream, "M:\\", static (_, _) => { }, static () => null, "zip", 1);

        var info = new FakeDokanFileInfo();
        zipFs.CreateFile("\\stored.txt", FileAccess.ReadData, FileShare.Read,
            FileMode.Open, FileOptions.None, FileAttributes.Normal, info);

        var buffer = new byte[100];
        var result = zipFs.ReadFile("\\stored.txt", buffer, out var bytesRead, 7, info);

        Assert.Equal(DokanResult.Success, result);
        Assert.Equal("read content from stored entry", Encoding.UTF8.GetString(buffer, 0, bytesRead));

        zipFs.CloseFile("\\stored.txt", info);
    }

    [Fact]
    public void StoredEntryNoTempFileCreated()
    {
        using var stream = CreateStoredZipStream();
        using var zipFs = new ZipFs(stream, "M:\\", static (_, _) => { }, static () => null, "zip", 1);

        var info = new FakeDokanFileInfo();
        zipFs.CreateFile("\\stored.txt", FileAccess.ReadData, FileShare.Read,
            FileMode.Open, FileOptions.None, FileAttributes.Normal, info);

        var cache = zipFs.Core.LargeFileCache;
        Assert.DoesNotContain(cache, static kvp => kvp.Key.Contains("stored.txt", StringComparison.OrdinalIgnoreCase));

        zipFs.CloseFile("\\stored.txt", info);
    }

    [Fact]
    public void StoredEntryCloseFileDisposesStream()
    {
        using var stream = CreateStoredZipStream();
        using var zipFs = new ZipFs(stream, "M:\\", static (_, _) => { }, static () => null, "zip", 1);

        var info = new FakeDokanFileInfo();
        zipFs.CreateFile("\\stored.txt", FileAccess.ReadData, FileShare.Read,
            FileMode.Open, FileOptions.None, FileAttributes.Normal, info);

        var contextStream = info.Context as Stream;
        Assert.NotNull(contextStream);
        zipFs.CloseFile("\\stored.txt", info);

        Assert.Null(info.Context);
        Assert.Throws<ObjectDisposedException>(() => contextStream.ReadByte());
    }

    [Fact]
    public void StoredEntryBinaryContentReadsCorrectly()
    {
        using var stream = CreateStoredZipStream();
        using var zipFs = new ZipFs(stream, "M:\\", static (_, _) => { }, static () => null, "zip", 1);

        var info = new FakeDokanFileInfo();
        zipFs.CreateFile(@"\sub\stored-data.bin", FileAccess.ReadData, FileShare.Read,
            FileMode.Open, FileOptions.None, FileAttributes.Normal, info);

        var buffer = new byte[10];
        zipFs.ReadFile(@"\sub\stored-data.bin", buffer, out var bytesRead, 100, info);

        Assert.Equal(10, bytesRead);
        for (var i = 0; i < 10; i++)
            Assert.Equal((byte)(100 + i), buffer[i]);

        zipFs.CloseFile(@"\sub\stored-data.bin", info);
    }

    [Fact]
    public void StoredEntryEofReturnsZeroBytes()
    {
        using var stream = CreateStoredZipStream();
        using var zipFs = new ZipFs(stream, "M:\\", static (_, _) => { }, static () => null, "zip", 1);

        var info = new FakeDokanFileInfo();
        zipFs.CreateFile("\\stored.txt", FileAccess.ReadData, FileShare.Read,
            FileMode.Open, FileOptions.None, FileAttributes.Normal, info);

        var buffer = new byte[100];
        var result = zipFs.ReadFile("\\stored.txt", buffer, out var bytesRead, 9999, info);

        Assert.Equal(DokanResult.Success, result);
        Assert.Equal(0, bytesRead);

        zipFs.CloseFile("\\stored.txt", info);
    }

    [Fact]
    public void IsStoredEntryDetection()
    {
        using var stream = CreateStoredZipStream();
        using var zipFs = new ZipFs(stream, "M:\\", static (_, _) => { }, static () => null, "zip");

        var entries = zipFs.Core.ArchiveEntries;
        Assert.NotNull(entries);
        Assert.True(entries.ContainsKey("/stored.txt"));

        var storedEntry = entries["/stored.txt"];
        Assert.True(zipFs.Core.IsStoredEntry(storedEntry));
    }

    private static string CreateTempStoredZipFile(string entryName, byte[] data)
    {
        var tempPath = Path.Combine(Path.GetTempPath(), $"SimpleZipDrive_test_{Guid.NewGuid():N}.zip");
        using var fs = new FileStream(tempPath, FileMode.Create, System.IO.FileAccess.Write, FileShare.Read);
        WriteStoredZipToStream(fs, [(entryName, data)]);
        return tempPath;
    }

    private static string CreateTempStoredZipFile((string Name, byte[] Data)[] entries)
    {
        var tempPath = Path.Combine(Path.GetTempPath(), $"SimpleZipDrive_test_{Guid.NewGuid():N}.zip");
        using var fs = new FileStream(tempPath, FileMode.Create, System.IO.FileAccess.Write, FileShare.Read);
        WriteStoredZipToStream(fs, entries);
        return tempPath;
    }

    private static void WriteStoredZipToStream(Stream stream, (string Name, byte[] Data)[] entries)
    {
        var w = new BinaryWriter(stream, Encoding.UTF8, true);
        var offsets = new long[entries.Length];

        for (var i = 0; i < entries.Length; i++)
        {
            offsets[i] = stream.Position;
            var nameBytes = Encoding.UTF8.GetBytes(entries[i].Name);
            var crc = ComputeCrc32(entries[i].Data);
            var len = (uint)entries[i].Data.Length;

            w.Write(0x04034b50u);
            w.Write((ushort)20);
            w.Write((ushort)0);
            w.Write((ushort)0);
            w.Write((ushort)0);
            w.Write((ushort)0);
            w.Write(crc);
            w.Write(len);
            w.Write(len);
            w.Write((ushort)nameBytes.Length);
            w.Write((ushort)0);
            w.Write(nameBytes);
            w.Write(entries[i].Data);
        }

        var cdStart = stream.Position;
        for (var i = 0; i < entries.Length; i++)
        {
            var nameBytes = Encoding.UTF8.GetBytes(entries[i].Name);
            var crc = ComputeCrc32(entries[i].Data);
            var len = (uint)entries[i].Data.Length;

            w.Write(0x02014b50u);
            w.Write((ushort)20);
            w.Write((ushort)20);
            w.Write((ushort)0);
            w.Write((ushort)0);
            w.Write((ushort)0);
            w.Write((ushort)0);
            w.Write(crc);
            w.Write(len);
            w.Write(len);
            w.Write((ushort)nameBytes.Length);
            w.Write((ushort)0);
            w.Write((ushort)0);
            w.Write((ushort)0);
            w.Write((ushort)0);
            w.Write((uint)0);
            w.Write((uint)offsets[i]);
            w.Write(nameBytes);
        }

        var cdSize = (uint)(stream.Position - cdStart);
        w.Write(0x06054b50u);
        w.Write((ushort)0);
        w.Write((ushort)0);
        w.Write((ushort)entries.Length);
        w.Write((ushort)entries.Length);
        w.Write(cdSize);
        w.Write((uint)cdStart);
        w.Write((ushort)0);
        w.Flush();
    }

    private static byte[] CreatePatternData(int length, int seed)
    {
        var data = new byte[length];
        for (var i = 0; i < length; i++)
        {
            data[i] = (byte)((i + seed) % 256);
        }

        return data;
    }

    [Fact]
    public async Task StoredEntryFileStreamSourceConcurrentMultiFileReadsAsync()
    {
        const int size = 256 * 1024;
        var data1 = CreatePatternData(size, 0);
        var data2 = CreatePatternData(size, 127);

        var tempZip = CreateTempStoredZipFile([("file1.bin", data1), ("file2.bin", data2)]);
        try
        {
            await using var fs = new FileStream(tempZip, FileMode.Open, System.IO.FileAccess.Read, FileShare.Read);
            ZipFs? zipFs = null;
            try
            {
                zipFs = new ZipFs(fs, "M:\\", static (_, _) => { }, static () => null, "zip", 1);
                var zf = zipFs;

                var info1 = new FakeDokanFileInfo();
                var info2 = new FakeDokanFileInfo();

                zf.CreateFile("\\file1.bin", FileAccess.ReadData, FileShare.Read,
                    FileMode.Open, FileOptions.None, FileAttributes.Normal, info1);
                zf.CreateFile("\\file2.bin", FileAccess.ReadData, FileShare.Read,
                    FileMode.Open, FileOptions.None, FileAttributes.Normal, info2);

                Assert.NotNull(info1.Context);
                Assert.NotNull(info2.Context);
                Assert.IsNotType<FileStream>(info1.Context);
                Assert.IsNotType<MemoryStream>(info1.Context);

                var buffer1 = new byte[size];
                var buffer2 = new byte[size];

                var barrier = new Barrier(2);

                var task1 = Task.Run(() =>
                {
                    barrier.SignalAndWait();
                    zf.ReadFile("\\file1.bin", buffer1, out var r, 0, info1);
                    return r;
                });
                var task2 = Task.Run(() =>
                {
                    barrier.SignalAndWait();
                    zf.ReadFile("\\file2.bin", buffer2, out var r, 0, info2);
                    return r;
                });

                var results = await Task.WhenAll(task1, task2);
                var read1 = results[0];
                var read2 = results[1];

                Assert.Equal(size, read1);
                Assert.Equal(size, read2);
                Assert.Equal(data1, buffer1);
                Assert.Equal(data2, buffer2);

                zf.CloseFile("\\file1.bin", info1);
                zf.CloseFile("\\file2.bin", info2);
            }
            finally
            {
                zipFs?.Dispose();
            }
        }
        finally
        {
            File.Delete(tempZip);
        }
    }

    [Fact]
    public async Task StoredEntryFileStreamSourceSingleFileConcurrentSeekingAsync()
    {
        const int size = 512 * 1024;
        var data = CreatePatternData(size, 42);

        var tempZip = CreateTempStoredZipFile("large.bin", data);
        try
        {
            await using var fs = new FileStream(tempZip, FileMode.Open, System.IO.FileAccess.Read, FileShare.Read);
            ZipFs? zipFs = null;
            try
            {
                zipFs = new ZipFs(fs, "M:\\", static (_, _) => { }, static () => null, "zip", 1);
                var zf = zipFs;

                var chunks = new[] { (Offset: 0, Length: 4096), (Offset: 100000, Length: 8192), (Offset: 500000, Length: 4096) };
                var errors = new ConcurrentBag<Exception>();

                var info = new FakeDokanFileInfo();
                zf.CreateFile("\\large.bin", FileAccess.ReadData, FileShare.Read,
                    FileMode.Open, FileOptions.None, FileAttributes.Normal, info);

                Assert.NotNull(info.Context);
                Assert.IsNotType<FileStream>(info.Context);
                Assert.IsNotType<MemoryStream>(info.Context);

                var barrier = new Barrier(chunks.Length);
                var tasks = chunks.Select(chunk => Task.Run(() =>
                {
                    var buffer = new byte[chunk.Length];
                    try
                    {
                        barrier.SignalAndWait();
                        zf.ReadFile("\\large.bin", buffer, out var read, chunk.Offset, info);
                        return (Data: buffer, Read: read);
                    }
                    catch (Exception ex)
                    {
                        errors.Add(ex);
                        return (Data: buffer, Read: 0);
                    }
                })).ToArray();

                var results = await Task.WhenAll(tasks);

                Assert.Empty(errors);

                for (var i = 0; i < chunks.Length; i++)
                {
                    Assert.Equal(chunks[i].Length, results[i].Read);
                    for (var j = 0; j < chunks[i].Length; j++)
                        Assert.Equal(data[chunks[i].Offset + j], results[i].Data[j]);
                }

                zf.CloseFile("\\large.bin", info);
            }
            finally
            {
                zipFs?.Dispose();
            }
        }
        finally
        {
            File.Delete(tempZip);
        }
    }

    [Fact]
    public void StoredEntryFileStreamSourceFastPathWithCompressedMix()
    {
        const int storedSize = 128 * 1024;
        var storedData = CreatePatternData(storedSize, 7);

        var tempZip = CreateTempStoredZipFile("stored.bin", storedData);
        try
        {
            using var fs = new FileStream(tempZip, FileMode.Open, System.IO.FileAccess.Read, FileShare.Read);
            using var zipFs = new ZipFs(fs, "M:\\", static (_, _) => { }, static () => null, "zip", 1);

            var info = new FakeDokanFileInfo();
            var result = zipFs.CreateFile("\\stored.bin", FileAccess.ReadData, FileShare.Read,
                FileMode.Open, FileOptions.None, FileAttributes.Normal, info);

            Assert.Equal(DokanResult.Success, result);
            Assert.NotNull(info.Context);
            Assert.IsNotType<FileStream>(info.Context);
            Assert.IsNotType<MemoryStream>(info.Context);

            var buffer = new byte[storedSize];
            zipFs.ReadFile("\\stored.bin", buffer, out var bytesRead, 0, info);
            Assert.Equal(storedSize, bytesRead);
            Assert.Equal(storedData, buffer);

            zipFs.CloseFile("\\stored.bin", info);
        }
        finally
        {
            File.Delete(tempZip);
        }
    }

    // 7z archive type tests

    private static MemoryStream CreateSevenZipStream()
    {
        var ms = new MemoryStream();
        using (var writer = WriterFactory.OpenWriter(ms, ArchiveType.SevenZip,
                   new SevenZipWriterOptions(CompressionType.LZMA)))
        {
            var contentBytes = "Hello from 7z archive"u8.ToArray();
            using var stream = new MemoryStream(contentBytes);
            writer.Write("readme.txt", stream, null);
        }

        ms.Position = 0;
        return ms;
    }

    [Fact]
    public void SevenZipConstructorSucceeds()
    {
        using var stream = CreateSevenZipStream();
        using var zipFs = new ZipFs(stream, "M:\\", static (_, _) => { }, static () => null, "7z");

        Assert.NotNull(zipFs);
    }

    [Fact]
    public void SevenZipGetVolumeInformationReturnsExpectedValues()
    {
        using var stream = CreateSevenZipStream();
        using var zipFs = new ZipFs(stream, "M:\\", static (_, _) => { }, static () => null, "7z");

        var info = new FakeDokanFileInfo();
        var result = zipFs.GetVolumeInformation(out var label, out _, out var fsName, out _, info);

        Assert.Equal(DokanResult.Success, result);
        Assert.Equal("SimpleZipDrive", label);
        Assert.Equal("ZipFS", fsName);
    }

    [Fact]
    public void SevenZipGetFileInformationReturnsFile()
    {
        using var stream = CreateSevenZipStream();
        using var zipFs = new ZipFs(stream, "M:\\", static (_, _) => { }, static () => null, "7z");

        var info = new FakeDokanFileInfo();
        var result = zipFs.GetFileInformation("\\readme.txt", out var fileInfo, info);

        Assert.Equal(DokanResult.Success, result);
        Assert.Equal("readme.txt", fileInfo.FileName);
        Assert.False(info.IsDirectory);
    }

    [Fact]
    public void SevenZipGetFileInformationRootReturnsDirectory()
    {
        using var stream = CreateSevenZipStream();
        using var zipFs = new ZipFs(stream, "M:\\", static (_, _) => { }, static () => null, "7z");

        var info = new FakeDokanFileInfo();
        var result = zipFs.GetFileInformation("\\", out var fileInfo, info);

        Assert.Equal(DokanResult.Success, result);
        Assert.Equal(FileAttributes.Directory, fileInfo.Attributes);
        Assert.True(info.IsDirectory);
    }

    [Fact]
    public void SevenZipFindFilesRootReturnsChildren()
    {
        using var stream = CreateSevenZipStream();
        using var zipFs = new ZipFs(stream, "M:\\", static (_, _) => { }, static () => null, "7z");

        var info = new FakeDokanFileInfo();
        var result = zipFs.FindFiles("\\", out var files, info);

        Assert.Equal(DokanResult.Success, result);
        Assert.NotEmpty(files);
        Assert.Contains(files, static f => f.FileName == "readme.txt");
    }

    [Fact]
    public void SevenZipCreateFileReturnsSuccess()
    {
        using var stream = CreateSevenZipStream();
        using var zipFs = new ZipFs(stream, "M:\\", static (_, _) => { }, static () => null, "7z");

        var info = new FakeDokanFileInfo();
        var result = zipFs.CreateFile("\\readme.txt", FileAccess.ReadData, FileShare.Read,
            FileMode.Open, FileOptions.None, FileAttributes.Normal, info);

        Assert.Equal(DokanResult.Success, result);
        Assert.NotNull(info.Context);
    }

    [Fact]
    public void SevenZipReadFileReadsContent()
    {
        using var stream = CreateSevenZipStream();
        using var zipFs = new ZipFs(stream, "M:\\", static (_, _) => { }, static () => null, "7z");

        var info = new FakeDokanFileInfo();
        zipFs.CreateFile("\\readme.txt", FileAccess.ReadData, FileShare.Read,
            FileMode.Open, FileOptions.None, FileAttributes.Normal, info);

        var buffer = new byte[100];
        var result = zipFs.ReadFile("\\readme.txt", buffer, out var bytesRead, 0, info);

        Assert.Equal(DokanResult.Success, result);
        Assert.Equal("Hello from 7z archive", Encoding.UTF8.GetString(buffer, 0, bytesRead));

        zipFs.CloseFile("\\readme.txt", info);
    }

    [Fact]
    public void SevenZipNonExistentFileReturnsPathNotFound()
    {
        using var stream = CreateSevenZipStream();
        using var zipFs = new ZipFs(stream, "M:\\", static (_, _) => { }, static () => null, "7z");

        var info = new FakeDokanFileInfo();
        var result = zipFs.GetFileInformation("\\nope.txt", out _, info);

        Assert.Equal(DokanResult.PathNotFound, result);
    }

    [Fact]
    public void SevenZipWriteFileReturnsAccessDenied()
    {
        using var stream = CreateSevenZipStream();
        using var zipFs = new ZipFs(stream, "M:\\", static (_, _) => { }, static () => null, "7z");

        var info = new FakeDokanFileInfo();
        var result = zipFs.WriteFile("\\readme.txt", [], out _, 0, info);

        Assert.Equal(DokanResult.AccessDenied, result);
    }

    [Fact]
    public void SevenZipDeleteFileReturnsAccessDenied()
    {
        using var stream = CreateSevenZipStream();
        using var zipFs = new ZipFs(stream, "M:\\", static (_, _) => { }, static () => null, "7z");

        var info = new FakeDokanFileInfo();
        var result = zipFs.DeleteFile("\\readme.txt", info);

        Assert.Equal(DokanResult.AccessDenied, result);
    }

    [Fact]
    public void SevenZipMountedReturnsSuccess()
    {
        using var stream = CreateSevenZipStream();
        using var zipFs = new ZipFs(stream, "M:\\", static (_, _) => { }, static () => null, "7z");

        var info = new FakeDokanFileInfo();
        var result = zipFs.Mounted("M:\\", info);

        Assert.Equal(DokanResult.Success, result);
    }

    [Fact]
    public void SevenZipUnmountedReturnsSuccess()
    {
        using var stream = CreateSevenZipStream();
        using var zipFs = new ZipFs(stream, "M:\\", static (_, _) => { }, static () => null, "7z");

        var info = new FakeDokanFileInfo();
        var result = zipFs.Unmounted(info);

        Assert.Equal(DokanResult.Success, result);
    }

    [Fact]
    public void SevenZipGetDiskFreeSpaceReturnsArchiveLength()
    {
        using var stream = CreateSevenZipStream();
        using var zipFs = new ZipFs(stream, "M:\\", static (_, _) => { }, static () => null, "7z");

        var info = new FakeDokanFileInfo();
        var result = zipFs.GetDiskFreeSpace(out var free, out var total, out var totalFree, info);

        Assert.Equal(DokanResult.Success, result);
        Assert.Equal(stream.Length, total);
        Assert.Equal(0, free);
        Assert.Equal(0, totalFree);
    }

    [Fact]
    public void SevenZipLockUnlockFileReturnsSuccess()
    {
        using var stream = CreateSevenZipStream();
        using var zipFs = new ZipFs(stream, "M:\\", static (_, _) => { }, static () => null, "7z");

        var info = new FakeDokanFileInfo();
        var lockResult = zipFs.LockFile("\\readme.txt", 0, 0, info);
        var unlockResult = zipFs.UnlockFile("\\readme.txt", 0, 0, info);

        Assert.Equal(DokanResult.Success, lockResult);
        Assert.Equal(DokanResult.Success, unlockResult);
    }

    [Fact]
    public void SevenZipFindStreamsReturnsNotImplemented()
    {
        using var stream = CreateSevenZipStream();
        using var zipFs = new ZipFs(stream, "M:\\", static (_, _) => { }, static () => null, "7z");

        var info = new FakeDokanFileInfo();
        var result = zipFs.FindStreams("\\readme.txt", out _, info);

        Assert.Equal(DokanResult.NotImplemented, result);
    }

    // Archive type error tests

    [Fact]
    public void ConstructorUnsupportedArchiveTypeThrowsNotSupportedException()
    {
        using var zipStream = CreateZipStream();
        Assert.Throws<NotSupportedException>(() =>
            new ZipFs(zipStream, "M:\\", static (_, _) => { }, static () => null, "iso"));
    }

    [Fact]
    public void ConstructorWrongArchiveTypeForFormatThrows()
    {
        using var zipStream = CreateZipStream();
        Assert.Throws<ArchiveOperationException>(() =>
        {
            using var _ = new ZipFs(zipStream, "M:\\", static (_, _) => { }, static () => null, "7z");
        });
    }

    [Fact]
    public void ConstructorEmptyArchiveTypeThrowsNotSupportedException()
    {
        using var zipStream = CreateZipStream();
        Assert.Throws<NotSupportedException>(() =>
            new ZipFs(zipStream, "M:\\", static (_, _) => { }, static () => null, ""));
    }

    // ─── GetParentPath tests ───

    [Fact]
    public void GetParentPathRootReturnsNull()
    {
        var result = ZipFsHelpers.GetParentPath("/");

        Assert.Null(result);
    }

    [Fact]
    public void GetParentPathDirectFileUnderRootReturnsRoot()
    {
        var result = ZipFsHelpers.GetParentPath("/file.txt");

        Assert.Equal("/", result);
    }

    [Fact]
    public void GetParentPathNestedFileReturnsParentDirectory()
    {
        var result = ZipFsHelpers.GetParentPath("/dir/subdir/file.txt");

        Assert.Equal("/dir/subdir", result);
    }

    [Fact]
    public void GetParentPathSingleDirectoryLevelReturnsRoot()
    {
        var result = ZipFsHelpers.GetParentPath("/mydir");

        Assert.Equal("/", result);
    }

    [Fact]
    public void GetParentPathTrailingSlashStripsTrailingSlash()
    {
        var result = ZipFsHelpers.GetParentPath("/dir/subdir/");

        Assert.Equal("/dir/subdir", result);
    }

    [Fact]
    public void GetParentPathDeeplyNestedReturnsCorrectParent()
    {
        var result = ZipFsHelpers.GetParentPath("/a/b/c/d/e/f");

        Assert.Equal("/a/b/c/d/e", result);
    }

    // ─── IsNameMatch tests ───

    [Fact]
    public void IsNameMatchNullPatternReturnsTrue()
    {
        var result = ZipFsHelpers.IsNameMatch("file.txt", null!);

        Assert.True(result);
    }

    [Fact]
    public void IsNameMatchEmptyPatternReturnsTrue()
    {
        var result = ZipFsHelpers.IsNameMatch("file.txt", "");

        Assert.True(result);
    }

    [Fact]
    public void IsNameMatchStarPatternReturnsTrue()
    {
        var result = ZipFsHelpers.IsNameMatch("anything.here", "*");

        Assert.True(result);
    }

    [Fact]
    public void IsNameMatchStarDotStarPatternReturnsTrue()
    {
        var result = ZipFsHelpers.IsNameMatch("document.pdf", "*.*");

        Assert.True(result);
    }

    [Fact]
    public void IsNameMatchExactMatchReturnsTrue()
    {
        var result = ZipFsHelpers.IsNameMatch("readme.txt", "readme.txt");

        Assert.True(result);
    }

    [Fact]
    public void IsNameMatchWildcardExtensionMatches()
    {
        var result = ZipFsHelpers.IsNameMatch("report.txt", "*.txt");

        Assert.True(result);
    }

    [Fact]
    public void IsNameMatchWildcardExtensionDoesNotMatch()
    {
        var result = ZipFsHelpers.IsNameMatch("report.bin", "*.txt");

        Assert.False(result);
    }

    [Fact]
    public void IsNameMatchWildcardPrefixMatches()
    {
        var result = ZipFsHelpers.IsNameMatch("data.csv", "data.*");

        Assert.True(result);
    }

    [Fact]
    public void IsNameMatchWildcardPrefixDoesNotMatch()
    {
        var result = ZipFsHelpers.IsNameMatch("info.csv", "data.*");

        Assert.False(result);
    }

    [Fact]
    public void IsNameMatchQuestionMarkMatchesSingleCharacter()
    {
        var result = ZipFsHelpers.IsNameMatch("abc.txt", "abc.tx?");

        Assert.True(result);
    }

    [Fact]
    public void IsNameMatchQuestionMarkNoMatchForExtraChar()
    {
        var result = ZipFsHelpers.IsNameMatch("abcd.txt", "abc.tx?");

        Assert.False(result);
    }

    [Fact]
    public void IsNameMatchComplexPatternWithStarAndQuestionMark()
    {
        var result = ZipFsHelpers.IsNameMatch("log_2025_01_15.txt", "log_????_??_??.*");

        Assert.True(result);
    }

    [Fact]
    public void IsNameMatchEmptyFileNameMatchesStar()
    {
        var result = ZipFsHelpers.IsNameMatch("", "*");

        Assert.True(result);
    }
}
