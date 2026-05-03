using System.IO.Compression;
using System.Security.AccessControl;
using System.Text;
using DokanNet;
using FileAccess = DokanNet.FileAccess;
using SimpleZipDrive.Tests.Fakes;

namespace SimpleZipDrive.Tests;

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

    public void Dispose()
    {
        _zipFs.Dispose();
        _stream.Dispose();
        GC.SuppressFinalize(this);
    }
}
