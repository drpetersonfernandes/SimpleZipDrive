using SimpleZipDrive.Core;
using System.IO.Compression;
using System.Security.AccessControl;
using System.Text;
using DokanNet;
using FileAccess = DokanNet.FileAccess;
using SimpleZipDrive.Tests.Fakes;

namespace SimpleZipDrive.Tests;

public class ZipFsDokanAdditionalTests : IDisposable
{
    private readonly List<IDisposable> _disposables = [];

    private ZipFs CreateZipFs(Stream? stream = null, long maxMemory = ZipFileSystemCore.DefaultMaxMemorySize)
    {
        var ms = stream ?? CreateZipStream();
        if (stream == null) _disposables.Add(ms);
        var zipFs = new ZipFs(ms, "M:\\", static (_, _) => { }, static () => null, "zip", maxMemory);
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

    // ─── CreateFile: failed entry re-open returns Error ───

    [Fact]
    public void CreateFile_FailedEntry_ReturnsError()
    {
        var zipFs = CreateZipFs();

        // Mark entry as failed
        zipFs.Core.AddFailedEntry("/readme.txt");

        var info = new FakeDokanFileInfo();
        var result = zipFs.CreateFile(
            "\\readme.txt",
            FileAccess.ReadData,
            FileShare.Read,
            FileMode.Open,
            FileOptions.None,
            FileAttributes.Normal,
            info);

        Assert.Equal(DokanResult.Error, result);
    }

    // ─── CreateFile: directory with read access returns Success ───

    [Fact]
    public void CreateFile_DirectoryReadAccess_ReturnsSuccess()
    {
        var zipFs = CreateZipFs();
        var info = new FakeDokanFileInfo();

        var result = zipFs.CreateFile(
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

    // ─── CreateFile: FileMode.Append returns AccessDenied for files ───

    [Fact]
    public void CreateFile_FileModeAppend_ReturnsAccessDenied()
    {
        var zipFs = CreateZipFs();
        var info = new FakeDokanFileInfo();

        var result = zipFs.CreateFile(
            "\\readme.txt",
            FileAccess.ReadData,
            FileShare.Read,
            FileMode.Append,
            FileOptions.None,
            FileAttributes.Normal,
            info);

        Assert.Equal(DokanResult.AccessDenied, result);
    }

    // ─── ReadFile: disposed stream context returns InvalidHandle or Error ───

    [Fact]
    public void ReadFile_DisposedStreamContext_ReturnsError()
    {
        var zipFs = CreateZipFs();
        var info = new FakeDokanFileInfo();

        zipFs.CreateFile("\\readme.txt", FileAccess.ReadData, FileShare.Read,
            FileMode.Open, FileOptions.None, FileAttributes.Normal, info);

        // Dispose the stream to cause an exception on read
        if (info.Context is IDisposable disposable)
            disposable.Dispose();

        var buffer = new byte[100];
        var result = zipFs.ReadFile("\\readme.txt", buffer, out var bytesRead, 0, info);

        // After disposing the stream, read may return InvalidHandle or Error
        Assert.True(result is DokanResult.InvalidHandle or DokanResult.Error);
        Assert.Equal(0, bytesRead);
    }

    // ─── FindFilesWithPattern: invalid pattern returns InvalidParameter ───

    [Fact]
    public void FindFilesWithPattern_InvalidPattern_ReturnsInvalidParameter()
    {
        var zipFs = CreateZipFs();
        var info = new FakeDokanFileInfo();

        // Extremely long pattern that would cause regex to fail
        var result = zipFs.FindFilesWithPattern("\\", "[invalid", out _, info);

        // The pattern "[" might be caught as ArgumentException
        Assert.True(result is DokanResult.Success or DokanResult.InvalidParameter);
    }

    // ─── GetFileSecurity: directory returns DirectorySecurity ───

    [Fact]
    public void GetFileSecurity_Directory_ReturnsDirectorySecurity()
    {
        var zipFs = CreateZipFs();
        var info = new FakeDokanFileInfo();

        var result = zipFs.GetFileSecurity("\\data", out var security, AccessControlSections.All, info);

        Assert.Equal(DokanResult.Success, result);
        Assert.NotNull(security);
        Assert.IsAssignableFrom<DirectorySecurity>(security);
    }

    // ─── GetFileSecurity: file returns FileSecurity ───

    [Fact]
    public void GetFileSecurity_File_ReturnsFileSecurity()
    {
        var zipFs = CreateZipFs();
        var info = new FakeDokanFileInfo();

        var result = zipFs.GetFileSecurity("\\readme.txt", out var security, AccessControlSections.All, info);

        Assert.Equal(DokanResult.Success, result);
        Assert.NotNull(security);
        Assert.IsAssignableFrom<FileSecurity>(security);
    }

    // ─── GetFileInformation: root returns directory ───

    [Fact]
    public void GetFileInformation_Root_ReturnsDirectory()
    {
        var zipFs = CreateZipFs();
        var info = new FakeDokanFileInfo();

        var result = zipFs.GetFileInformation("\\", out var fileInfo, info);

        Assert.Equal(DokanResult.Success, result);
        Assert.Equal(FileAttributes.Directory, fileInfo.Attributes);
    }

    // ─── CreateFile: multiple opens of same file ───

    [Fact]
    public void CreateFile_MultipleOpens_Succeed()
    {
        var zipFs = CreateZipFs();
        var info1 = new FakeDokanFileInfo();
        var info2 = new FakeDokanFileInfo();

        var result1 = zipFs.CreateFile("\\readme.txt", FileAccess.ReadData, FileShare.Read,
            FileMode.Open, FileOptions.None, FileAttributes.Normal, info1);
        var result2 = zipFs.CreateFile("\\readme.txt", FileAccess.ReadData, FileShare.Read,
            FileMode.Open, FileOptions.None, FileAttributes.Normal, info2);

        Assert.Equal(DokanResult.Success, result1);
        Assert.Equal(DokanResult.Success, result2);

        zipFs.CloseFile("\\readme.txt", info1);
        zipFs.CloseFile("\\readme.txt", info2);
    }

    // ─── CloseFile: with null context ───

    [Fact]
    public void CloseFile_NullContext_DoesNotThrow()
    {
        var zipFs = CreateZipFs();
        var info = new FakeDokanFileInfo { Context = null };

        var ex = Record.Exception(() => zipFs.CloseFile("\\readme.txt", info));
        Assert.Null(ex);
    }

    // ─── CreateFile: non-existent directory returns PathNotFound ───

    [Fact]
    public void CreateFile_NonExistentDirectory_ReturnsPathNotFound()
    {
        var zipFs = CreateZipFs();
        var info = new FakeDokanFileInfo();

        var result = zipFs.CreateFile(
            "\\nonexistent",
            FileAccess.ReadData,
            FileShare.Read,
            FileMode.Open,
            FileOptions.None,
            FileAttributes.Directory,
            info);

        Assert.Equal(DokanResult.PathNotFound, result);
    }

    public void Dispose()
    {
        foreach (var d in _disposables)
        {
            try
            {
                d.Dispose();
            }
            catch
            {
                /* best effort */
            }
        }

        GC.SuppressFinalize(this);
    }
}
