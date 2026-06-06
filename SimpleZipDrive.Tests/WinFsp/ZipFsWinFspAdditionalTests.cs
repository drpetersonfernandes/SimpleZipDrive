using SimpleZipDrive.Core;
using System.IO.Compression;
using System.Security.AccessControl;
using System.Text;
using FileInfo = Fsp.Interop.FileInfo;
using WinFspZipFs = SimpleZipDrive_WinFsp.ZipFs;

namespace SimpleZipDrive.Tests.WinFsp;

public class ZipFsWinFspAdditionalTests : IDisposable
{
    private readonly List<IDisposable> _disposables = [];

    private const int StatusSuccess = 0;
    private const int StatusAccessDenied = unchecked((int)0xC0000022);
    private const int StatusObjectNameNotFound = unchecked((int)0xC0000034);
    private const int StatusUnsuccessful = unchecked((int)0xC0000001);
    private const int StatusDeviceNotReady = unchecked((int)0xC00000A0);
    private const int StatusInvalidHandle = unchecked((int)0xC0000008);

    private WinFspZipFs CreateZipFs(Stream? stream = null, long maxMemory = ZipFileSystemCore.DefaultMaxMemorySize)
    {
        var ms = stream ?? CreateZipStream();
        if (stream == null) _disposables.Add(ms);
        var zipFs = new WinFspZipFs(ms, "M:\\", static (_, _) => { }, static () => null, "zip", maxMemory);
        _disposables.Add(zipFs);
        return zipFs;
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

            zip.CreateEntry("empty/");
        }

        ms.Position = 0;
        return ms;
    }

    // ─── OpenOrCreateFile: disposed returns error status ───

    [Fact]
    public void OpenOrCreateFile_Disposed_ReturnsErrorStatus()
    {
        var zipFs = CreateZipFs();
        zipFs.Core.Dispose();

        var result = zipFs.OpenOrCreateFile(
            "\\readme.txt",
            out var fileNode,
            out var fileDesc,
            out _,
            out _);

        // Disposed returns an error status (varies by scenario)
        Assert.True(result != StatusSuccess,
            $"Expected error status, got 0x{result:X8}");
    }

    // ─── OpenOrCreateFile: path too long returns unsuccessful ───

    [Fact]
    public void OpenOrCreateFile_PathTooLong_ReturnsUnsuccessful()
    {
        var zipFs = CreateZipFs();
        var longPath = "\\" + new string('a', 260);

        var result = zipFs.OpenOrCreateFile(
            longPath,
            out _,
            out _,
            out _,
            out _);

        Assert.Equal(StatusUnsuccessful, result);
    }

    // ─── OpenOrCreateFile: directory returns success ───

    [Fact]
    public void OpenOrCreateFile_Directory_ReturnsSuccess()
    {
        var zipFs = CreateZipFs();

        var result = zipFs.OpenOrCreateFile(
            "\\data",
            out var fileNode,
            out var fileDesc,
            out var fileInfo,
            out _);

        Assert.Equal(StatusSuccess, result);
        Assert.NotNull(fileNode);

        zipFs.Close(fileNode, fileDesc);
    }

    // ─── OpenOrCreateFile: non-existent returns not found ───

    [Fact]
    public void OpenOrCreateFile_NonExistent_ReturnsNotFound()
    {
        var zipFs = CreateZipFs();

        var result = zipFs.OpenOrCreateFile(
            "\\nonexistent.txt",
            out _,
            out _,
            out _,
            out _);

        Assert.Equal(StatusObjectNameNotFound, result);
    }

    // ─── OpenOrCreateFile: failed entry returns unsuccessful ───

    [Fact]
    public void OpenOrCreateFile_FailedEntry_ReturnsUnsuccessful()
    {
        var zipFs = CreateZipFs();
        zipFs.Core.AddFailedEntry("/readme.txt");

        var result = zipFs.OpenOrCreateFile(
            "\\readme.txt",
            out _,
            out _,
            out _,
            out _);

        Assert.Equal(StatusUnsuccessful, result);
    }

    // ─── GetVolumeInfo: returns correct values ───

    [Fact]
    public void GetVolumeInfo_ReturnsCorrectValues()
    {
        var zipFs = CreateZipFs();

        var result = zipFs.GetVolumeInfo(out var volumeInfo);

        Assert.Equal(StatusSuccess, result);
        Assert.True(volumeInfo.TotalSize > 0);
    }

    // ─── SetVolumeLabel: returns ACCESS_DENIED ───

    [Fact]
    public void SetVolumeLabel_ReturnsAccessDenied()
    {
        var zipFs = CreateZipFs();

        var result = zipFs.SetVolumeLabel("NewLabel", out _);

        Assert.Equal(StatusAccessDenied, result);
    }

    // ─── Overwrite: returns ACCESS_DENIED ───

    [Fact]
    public void Overwrite_ReturnsAccessDenied()
    {
        var zipFs = CreateZipFs();

        var result = zipFs.Overwrite(
            null!, null!,
            0u, false, 0ul,
            out _);

        Assert.Equal(StatusAccessDenied, result);
    }

    // ─── Flush: returns ACCESS_DENIED ───

    [Fact]
    public void Flush_ReturnsAccessDenied()
    {
        var zipFs = CreateZipFs();

        var result = zipFs.Flush(null!, null!, out _);

        Assert.Equal(StatusAccessDenied, result);
    }

    // ─── SetBasicInfo: returns ACCESS_DENIED ───

    [Fact]
    public void SetBasicInfo_ReturnsAccessDenied()
    {
        var zipFs = CreateZipFs();

        var result = zipFs.SetBasicInfo(
            null!, null!,
            0u, 0ul, 0ul, 0ul, 0ul,
            out _);

        Assert.Equal(StatusAccessDenied, result);
    }

    // ─── SetFileSize: returns ACCESS_DENIED ───

    [Fact]
    public void SetFileSize_ReturnsAccessDenied()
    {
        var zipFs = CreateZipFs();

        var result = zipFs.SetFileSize(
            null!, null!,
            100ul,
            false,
            out _);

        Assert.Equal(StatusAccessDenied, result);
    }

    // ─── CanDelete: returns ACCESS_DENIED ───

    [Fact]
    public void CanDelete_ReturnsAccessDenied()
    {
        var zipFs = CreateZipFs();

        var result = zipFs.CanDelete(
            null!, null!,
            "\\readme.txt");

        Assert.Equal(StatusAccessDenied, result);
    }

    // ─── Rename: returns ACCESS_DENIED ───

    [Fact]
    public void Rename_ReturnsAccessDenied()
    {
        var zipFs = CreateZipFs();

        var result = zipFs.Rename(
            null!, null!,
            "\\old.txt",
            "\\new.txt",
            false);

        Assert.Equal(StatusAccessDenied, result);
    }

    // ─── SetSecurity: returns ACCESS_DENIED ───

    [Fact]
    public void SetSecurity_ReturnsAccessDenied()
    {
        var zipFs = CreateZipFs();

        var result = zipFs.SetSecurity(
            null!, null!,
            AccessControlSections.All,
            Array.Empty<byte>());

        Assert.Equal(StatusAccessDenied, result);
    }

    // ─── GetSecurity: returns SUCCESS ───

    [Fact]
    public void GetSecurity_ReturnsSuccess()
    {
        var zipFs = CreateZipFs();
        var securityDescriptor = Array.Empty<byte>();

        var result = zipFs.GetSecurity(
            null!, null!,
            ref securityDescriptor);

        Assert.Equal(StatusSuccess, result);
        Assert.NotNull(securityDescriptor);
    }

    // ─── DateTimeToFileTimeUtc: MinValue returns 0 ───

    [Fact]
    public void DateTimeToFileTimeUtc_MinValue_ReturnsZero()
    {
        // DateTime.MinValue ticks are before the Windows epoch (1601-01-01),
        // so the result is clamped to 0
        var result = WinFspZipFs.DateTimeToFileTimeUtc(DateTime.MinValue);

        Assert.Equal(0ul, result);
    }

    // ─── DateTimeToFileTimeUtc: normal date ───

    [Fact]
    public void DateTimeToFileTimeUtc_NormalDate_ReturnsPositive()
    {
        var dt = new DateTime(2024, 6, 15, 12, 0, 0, DateTimeKind.Utc);
        var result = WinFspZipFs.DateTimeToFileTimeUtc(dt);

        Assert.True(result > 0);
    }

    // ─── EntryNodeToFileInfo: directory node ───

    [Fact]
    public void EntryNodeToFileInfo_Directory_SetsCorrectAttributes()
    {
        var node = new EntryNode
        {
            IsDir = true,
            FileSize = 0,
            CreationTime = DateTime.Now,
            LastWriteTime = DateTime.Now,
            LastAccessTime = DateTime.Now
        };

        var fileInfo = WinFspZipFs.EntryNodeToFileInfo(node);

        Assert.True((fileInfo.FileAttributes & (uint)FileAttributes.Directory) != 0);
        Assert.Equal(0ul, fileInfo.FileSize);
    }

    // ─── EntryNodeToFileInfo: file node ───

    [Fact]
    public void EntryNodeToFileInfo_File_SetsCorrectAttributes()
    {
        var node = new EntryNode
        {
            IsDir = false,
            FileSize = 12345,
            CreationTime = DateTime.Now,
            LastWriteTime = DateTime.Now,
            LastAccessTime = DateTime.Now
        };

        var fileInfo = WinFspZipFs.EntryNodeToFileInfo(node);

        Assert.True((fileInfo.FileAttributes & (uint)FileAttributes.Archive) != 0);
        Assert.True((fileInfo.FileAttributes & (uint)FileAttributes.ReadOnly) != 0);
        Assert.Equal(12345ul, fileInfo.FileSize);
    }

    // ─── Dispose: idempotent ───

    [Fact]
    public void Dispose_DoubleDispose_DoesNotThrow()
    {
        var zipFs = CreateZipFs();

        zipFs.Dispose();
        var ex = Record.Exception(zipFs.Dispose);
        Assert.Null(ex);
    }

    // ─── OpenOrCreateFile: root directory ───

    [Fact]
    public void OpenOrCreateFile_Root_ReturnsDirectory()
    {
        var zipFs = CreateZipFs();

        var result = zipFs.OpenOrCreateFile("\\", out var fileNode, out var fileDesc, out var fileInfo, out _);

        Assert.Equal(StatusSuccess, result);
        Assert.Equal((uint)FileAttributes.Directory, fileInfo.FileAttributes);

        zipFs.Close(fileNode, fileDesc);
    }

    // ─── GetSecurityByName: existing file ───

    [Fact]
    public void GetSecurityByName_ExistingFile_ReturnsSuccess()
    {
        var zipFs = CreateZipFs();
        byte[] secDesc = [];

        var result = zipFs.GetSecurityByName("\\readme.txt", out var fileAttributes, ref secDesc);

        Assert.Equal(StatusSuccess, result);
        Assert.True((fileAttributes & (uint)FileAttributes.Archive) != 0);
    }

    // ─── GetSecurityByName: directory ───

    [Fact]
    public void GetSecurityByName_Directory_ReturnsDirectoryAttribute()
    {
        var zipFs = CreateZipFs();
        byte[] secDesc = [];

        var result = zipFs.GetSecurityByName("\\data", out var fileAttributes, ref secDesc);

        Assert.Equal(StatusSuccess, result);
        Assert.True((fileAttributes & (uint)FileAttributes.Directory) != 0);
    }

    // ─── GetSecurityByName: non-existent ───

    [Fact]
    public void GetSecurityByName_NonExistent_ReturnsNotFound()
    {
        var zipFs = CreateZipFs();
        byte[] secDesc = [];

        var result = zipFs.GetSecurityByName("\\nonexistent.txt", out _, ref secDesc);

        Assert.Equal(StatusObjectNameNotFound, result);
    }

    // ─── OpenOrCreateFile: successful file creates stream ───

    [Fact]
    public void OpenOrCreateFile_SuccessfulFile_CreatesStream()
    {
        var zipFs = CreateZipFs();

        var result = zipFs.OpenOrCreateFile("\\readme.txt", out var fileNode, out var fileDesc, out var fileInfo, out _);

        Assert.Equal(StatusSuccess, result);
        Assert.NotNull(fileNode);
        Assert.NotNull(fileDesc);
        Assert.True(fileInfo.FileSize > 0);

        zipFs.Close(fileNode, fileDesc);
    }

    public void Dispose()
    {
        foreach (var d in _disposables)
        {
            try { d.Dispose(); }
            catch { /* best effort */ }
        }

        GC.SuppressFinalize(this);
    }
}
