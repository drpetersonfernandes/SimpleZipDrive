using System.Diagnostics.CodeAnalysis;
using System.IO.Compression;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Text;
using SharpCompress.Archives;
using SharpCompress.Common;
using SharpCompress.Writers;
using SharpCompress.Writers.SevenZip;
using SimpleZipDrive.Core;
using FileInfo = Fsp.Interop.FileInfo;
using WinFspZipFs = SimpleZipDrive_WinFsp.ZipFs;

namespace SimpleZipDrive.Tests.WinFsp;

[SuppressMessage("ReSharper", "NullableWarningSuppressionIsUsed")]
public class WinFspZipFsTests : IDisposable
{
    private readonly MemoryStream _stream;
    private readonly WinFspZipFs _zipFs;

    private const int StatusSuccess = 0;
    private const int StatusAccessDenied = unchecked((int)0xC0000022);
    private const int StatusObjectNameNotFound = unchecked((int)0xC0000034);
    private const int StatusUnsuccessful = unchecked((int)0xC0000001);

    public WinFspZipFsTests()
    {
        _stream = CreateZipStream();
        _zipFs = new WinFspZipFs(_stream, "M:\\", static (_, _) => { }, static () => null, "zip");
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

    private int InvokeOpenOrCreateFile(string fileName, out object fileNode, out object fileDesc,
        out FileInfo fileInfo, out string normalizedName)
    {
        return _zipFs.OpenOrCreateFile(fileName, out fileNode, out fileDesc, out fileInfo, out normalizedName);
    }

    private void InvokeClose(object fileNode, object fileDesc)
    {
        _zipFs.Close(fileNode, fileDesc);
    }

    [Fact]
    public void Constructor_WithValidZip_Succeeds()
    {
        using var stream = CreateZipStream();
        using var zipFs = new WinFspZipFs(stream, "M:\\", static (_, _) => { }, static () => null, "zip");

        Assert.NotNull(zipFs);
    }

    [Fact]
    public void Constructor_UnsupportedArchiveType_ThrowsNotSupportedException()
    {
        using var ms = new MemoryStream();
        Assert.Throws<NotSupportedException>(() => new WinFspZipFs(ms, "M:\\", static (_, _) => { }, static () => null, "tar"));
    }

    [Fact]
    public void Constructor_EmptyArchiveType_ThrowsNotSupportedException()
    {
        using var zipStream = CreateZipStream();
        Assert.Throws<NotSupportedException>(() =>
            new WinFspZipFs(zipStream, "M:\\", static (_, _) => { }, static () => null, ""));
    }

    [Fact]
    public void Constructor_SevenZip_Succeeds()
    {
        using var stream = CreateSevenZipStream();
        using var zipFs = new WinFspZipFs(stream, "M:\\", static (_, _) => { }, static () => null, "7z");

        Assert.NotNull(zipFs);
    }

    [Fact]
    public void Dispose_CleansUpResources()
    {
        var stream = CreateZipStream();
        var zipFs = new WinFspZipFs(stream, "M:\\", static (_, _) => { }, static () => null, "zip");

        zipFs.Dispose();
        stream.Dispose();
    }

    [Fact]
    public void OpenOrCreateFile_ExistingFile_ReturnsSuccess()
    {
        var result = InvokeOpenOrCreateFile("\\readme.txt", out var fileNode, out var fileDesc, out var fileInfo, out _);

        Assert.Equal(StatusSuccess, result);
        Assert.NotNull(fileNode);
        Assert.NotNull(fileDesc);
        Assert.NotEqual(0ul, fileInfo.FileSize);

        InvokeClose(fileNode, fileDesc);
    }

    [Fact]
    public void OpenOrCreateFile_NonExistent_ReturnsObjectNameNotFound()
    {
        var result = InvokeOpenOrCreateFile("\\nonexistent.txt", out _, out _, out _, out _);

        Assert.Equal(StatusObjectNameNotFound, result);
    }

    [Fact]
    public void OpenOrCreateFile_Root_ReturnsDirectory()
    {
        var result = InvokeOpenOrCreateFile("\\", out _, out _, out var fileInfo, out _);

        Assert.Equal(StatusSuccess, result);
        Assert.Equal((uint)FileAttributes.Directory, fileInfo.FileAttributes);
    }

    [Fact]
    public void OpenOrCreateFile_PathTooLong_ReturnsUnsuccessful()
    {
        var longPath = "\\" + new string('a', 260);
        var result = InvokeOpenOrCreateFile(longPath, out _, out _, out _, out _);

        Assert.Equal(StatusUnsuccessful, result);
    }

    [Fact]
    public void OpenOrCreateFile_NormalizedPath_IsReturned()
    {
        InvokeOpenOrCreateFile("\\readme.txt", out var fileNode, out var fileDesc, out _, out var normalizedName);

        Assert.Equal("\\readme.txt", normalizedName);

        InvokeClose(fileNode, fileDesc);
    }

    [Fact]
    public void OpenOrCreateFile_AfterDispose_ReturnsDeviceNotReady()
    {
        var stream = CreateZipStream();
        var zipFs = new WinFspZipFs(stream, "M:\\", static (_, _) => { }, static () => null, "zip");
        zipFs.Dispose();

        var result = zipFs.OpenOrCreateFile("\\readme.txt", out _, out _, out _, out _);

        Assert.NotEqual(StatusSuccess, result);
        Assert.True(result < 0, $"Expected negative NTSTATUS error code, got: {result}");
    }

    [Fact]
    public void OpenOrCreateFile_Directory_ReturnsSuccess()
    {
        var result = InvokeOpenOrCreateFile("\\data", out _, out _, out var fileInfo, out _);

        Assert.Equal(StatusSuccess, result);
        Assert.Equal((uint)FileAttributes.Directory, fileInfo.FileAttributes);
    }

    [Fact]
    public void Close_DisposesFileDesc()
    {
        InvokeOpenOrCreateFile("\\readme.txt", out var fileNode, out var fileDesc, out _, out _);

        InvokeClose(fileNode, fileDesc);

        Assert.True(fileDesc is IDisposable);
        Assert.Throws<ObjectDisposedException>(() => ((Stream)fileDesc).ReadByte());
    }

    // ─── NormalizePath tests ───

    [Fact]
    public void NormalizePath_Null_ReturnsRoot()
    {
        var result = ZipFsHelpers.NormalizePath(null);

        Assert.Equal("/", result);
    }

    [Fact]
    public void NormalizePath_Empty_ReturnsRoot()
    {
        var result = ZipFsHelpers.NormalizePath("");

        Assert.Equal("/", result);
    }

    [Fact]
    public void NormalizePath_ConvertsBackslashes()
    {
        var result = ZipFsHelpers.NormalizePath(@"foo\bar\baz.txt");

        Assert.Equal("/foo/bar/baz.txt", result);
    }

    [Fact]
    public void NormalizePath_AddsLeadingSlash()
    {
        var result = ZipFsHelpers.NormalizePath("data/info.txt");

        Assert.Equal("/data/info.txt", result);
    }

    [Fact]
    public void NormalizePath_PreservesExistingLeadingSlash()
    {
        var result = ZipFsHelpers.NormalizePath("/already/normalized");

        Assert.Equal("/already/normalized", result);
    }

    [Fact]
    public void NormalizePath_StripsTrailingSlash()
    {
        var result = ZipFsHelpers.NormalizePath("/foo/bar/");

        Assert.Equal("/foo/bar", result);
    }

    [Fact]
    public void NormalizePath_StripsTrailingSlashWithoutLeadingSlash()
    {
        var result = ZipFsHelpers.NormalizePath("foo/bar/");

        Assert.Equal("/foo/bar", result);
    }

    [Fact]
    public void NormalizePath_RootUnchanged()
    {
        var result = ZipFsHelpers.NormalizePath("/");

        Assert.Equal("/", result);
    }

    // ─── IsPasswordRequiredException tests ───

    [Fact]
    public void IsPasswordRequiredException_MessageContainsPassword_ReturnsTrue()
    {
        var result = ZipFsHelpers.IsPasswordRequiredException(new InvalidOperationException("archive requires a password"));

        Assert.True(result);
    }

    [Fact]
    public void IsPasswordRequiredException_MessageContainsEncrypted_ReturnsTrue()
    {
        var result = ZipFsHelpers.IsPasswordRequiredException(new InvalidOperationException("file is encrypted"));

        Assert.True(result);
    }

    [Fact]
    public void IsPasswordRequiredException_NonMatchingMessage_ReturnsFalse()
    {
        var result = ZipFsHelpers.IsPasswordRequiredException(new InvalidOperationException("file not found"));

        Assert.False(result);
    }

    // ─── IsDataErrorException tests ───

    [Theory]
    [InlineData("Data Error", true)]
    [InlineData("Data error occurred", true)]
    [InlineData("some data ERROR here", true)]
    [InlineData("Some random error", false)]
    public void IsDataErrorException_DetectsByMessageContent(string message, bool expected)
    {
        var result = ZipFsHelpers.IsDataErrorException(new InvalidDataException(message));

        Assert.Equal(expected, result);
    }

    [Fact]
    public void IsDataErrorException_DetectsByTypeName()
    {
        var result = ZipFsHelpers.IsDataErrorException(new TestDataErrorException("some message"));

        Assert.True(result);
    }

    private class TestDataErrorException : Exception
    {
        public TestDataErrorException(string message) : base(message)
        {
        }
    }

    // ─── IsPathLengthValid tests ───

    [Fact]
    public void IsPathLengthValid_Null_ReturnsTrue()
    {
        var result = ZipFsHelpers.IsPathLengthValid(null);

        Assert.True(result);
    }

    [Fact]
    public void IsPathLengthValid_Empty_ReturnsTrue()
    {
        var result = ZipFsHelpers.IsPathLengthValid("");

        Assert.True(result);
    }

    [Fact]
    public void IsPathLengthValid_ExactlyMaxPath_ReturnsTrue()
    {
        var path = "\\" + new string('a', 259);
        var result = ZipFsHelpers.IsPathLengthValid(path);

        Assert.True(result);
    }

    [Fact]
    public void IsPathLengthValid_ExceedsMaxPath_ReturnsFalse()
    {
        var path = "\\" + new string('a', 260);
        var result = ZipFsHelpers.IsPathLengthValid(path);

        Assert.False(result);
    }

    [Fact]
    public void IsPathLengthValid_ExtendedPathPrefix_WithinLimit_ReturnsTrue()
    {
        var path = @"\\?\" + new string('a', 260);
        var result = ZipFsHelpers.IsPathLengthValid(path);

        Assert.True(result);
    }

    // ─── IsMatchSimple tests ───

    [Fact]
    public void IsMatchSimple_WildcardQuestionMark_MatchesSingleChar()
    {
        var result = ZipFsHelpers.IsMatchSimple("abc.txt", "abc.tx?");

        Assert.True(result);
    }

    [Fact]
    public void IsMatchSimple_QuestionMark_DoesNotMatchDifferentLength()
    {
        var result = ZipFsHelpers.IsMatchSimple("abcd.txt", "abc.tx?");

        Assert.False(result);
    }

    [Fact]
    public void IsMatchSimple_PatternTooLong_ReturnsFalse()
    {
        var pattern = new string('a', 261);
        var result = ZipFsHelpers.IsMatchSimple("anything", pattern);

        Assert.False(result);
    }

    [Fact]
    public void IsMatchSimple_StarPattern_ReturnsTrue()
    {
        var result = ZipFsHelpers.IsMatchSimple("readme.txt", "*");

        Assert.True(result);
    }

    [Fact]
    public void IsMatchSimple_StarDotStarPattern_ReturnsTrue()
    {
        var result = ZipFsHelpers.IsMatchSimple("test.txt", "*.*");

        Assert.True(result);
    }

    [Fact]
    public void IsMatchSimple_ExactMatch_ReturnsTrue()
    {
        var result = ZipFsHelpers.IsMatchSimple("readme.txt", "readme.txt");

        Assert.True(result);
    }

    [Fact]
    public void IsMatchSimple_NoMatch_ReturnsFalse()
    {
        var result = ZipFsHelpers.IsMatchSimple("readme.txt", "other*");

        Assert.False(result);
    }

    // ─── DateTimeToFileTimeUtc tests ───

    [Fact]
    public void DateTimeToFileTimeUtc_MinValue_ReturnsFileTimeForEpoch()
    {
        var result = WinFspZipFs.DateTimeToFileTimeUtc(DateTime.MinValue);

        Assert.IsType<ulong>(result);
        Assert.Equal(0ul, result);
    }

    [Fact]
    public void DateTimeToFileTimeUtc_Now_ReturnsValidFileTime()
    {
        var result = WinFspZipFs.DateTimeToFileTimeUtc(DateTime.Now);

        Assert.IsType<ulong>(result);
        Assert.True(result > 0);
    }

    // ─── EntryNodeToFileInfo tests ───

    [Fact]
    public void EntryNodeToFileInfo_File_ReturnsCorrectInfo()
    {
        var node = new EntryNode
        {
            NormalizedPath = "/test.txt",
            CanonicalPath = "/test.txt",
            IsDir = false,
            FileSize = 1024L,
            CreationTime = new DateTime(2024, 1, 1),
            LastWriteTime = new DateTime(2024, 2, 1),
            LastAccessTime = new DateTime(2024, 3, 1)
        };

        var result = WinFspZipFs.EntryNodeToFileInfo(node);

        var fi = result;
        Assert.Equal(1024ul, fi.FileSize);
        Assert.Equal((uint)(FileAttributes.Archive | FileAttributes.ReadOnly), fi.FileAttributes);
        Assert.NotEqual(0ul, fi.AllocationSize);
    }

    [Fact]
    public void EntryNodeToFileInfo_Directory_ReturnsDirectoryInfo()
    {
        var node = new EntryNode
        {
            NormalizedPath = "/testdir",
            CanonicalPath = "/testdir",
            IsDir = true,
            FileSize = 0L,
            CreationTime = new DateTime(2024, 1, 1),
            LastWriteTime = new DateTime(2024, 2, 1),
            LastAccessTime = new DateTime(2024, 3, 1)
        };

        var result = WinFspZipFs.EntryNodeToFileInfo(node);

        var fi = result;
        Assert.Equal(0ul, fi.FileSize);
        Assert.Equal((uint)FileAttributes.Directory, fi.FileAttributes);
        Assert.Equal(0ul, fi.AllocationSize);
    }

    // ─── IsDirectory tests (private static) ───

    [Fact]
    public void IsDirectory_TrailingSlash_ReturnsTrue()
    {
        using var stream = CreateZipStream();
        using var zipFs = new WinFspZipFs(stream, "M:\\", static (_, _) => { }, static () => null, "zip");

        var entries = zipFs.Core.ArchiveEntries;
        Assert.NotNull(entries);

        IArchiveEntry? dirEntry = null;
        foreach (var kvp in entries)
        {
            if (ZipFsHelpers.IsDirectory(kvp.Value))
            {
                dirEntry = kvp.Value;
                break;
            }
        }

        Assert.NotNull(dirEntry);
        Assert.True(ZipFsHelpers.IsDirectory(dirEntry));
    }

    [Fact]
    public void IsDirectory_FileEntry_ReturnsFalse()
    {
        using var stream = CreateZipStream();
        using var zipFs = new WinFspZipFs(stream, "M:\\", static (_, _) => { }, static () => null, "zip");

        var entries = zipFs.Core.ArchiveEntries;
        Assert.NotNull(entries);

        IArchiveEntry? fileEntry = null;
        foreach (var kvp in entries)
        {
            if (kvp.Key.Contains("readme.txt"))
            {
                fileEntry = kvp.Value;
                break;
            }
        }

        Assert.NotNull(fileEntry);
        var result = ZipFsHelpers.IsDirectory(fileEntry);
        Assert.False(result);
    }

    // ─── Stored entry tests ───

    private static MemoryStream CreateStoredZipStream()
    {
        var ms = new MemoryStream();
        var w = new BinaryWriter(ms, Encoding.UTF8, true);

        var entries = new (string Name, byte[] Data)[]
        {
            ("stored.txt", "Direct read content from stored entry"u8.ToArray())
        };

        var offsets = new long[entries.Length];
        for (var i = 0; i < entries.Length; i++)
        {
            offsets[i] = ms.Position;
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

        var cdStart = ms.Position;
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

        var cdSize = (uint)(ms.Position - cdStart);
        w.Write(0x06054b50u);
        w.Write((ushort)0);
        w.Write((ushort)0);
        w.Write((ushort)entries.Length);
        w.Write((ushort)entries.Length);
        w.Write(cdSize);
        w.Write((uint)cdStart);
        w.Write((ushort)0);

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
    public void StoredEntry_OpenOrCreateFile_ReturnsSuccess()
    {
        using var stream = CreateStoredZipStream();
        using var zipFs = new WinFspZipFs(stream, "M:\\", static (_, _) => { }, static () => null, "zip", 1);

        var result = zipFs.OpenOrCreateFile("\\stored.txt", out var fileNode, out var fileDesc, out _, out _);

        Assert.Equal(StatusSuccess, result);
        Assert.NotNull(fileNode);
        Assert.NotNull(fileDesc);

        InvokeCloseReflect(zipFs, fileNode, fileDesc);
    }

    private static void InvokeCloseReflect(WinFspZipFs zipFs, object fileNode, object fileDesc)
    {
        zipFs.Close(fileNode, fileDesc);
    }

    [Fact]
    public void IsStoredEntry_StoredZipEntry_ReturnsTrue()
    {
        using var stream = CreateStoredZipStream();
        using var zipFs = new WinFspZipFs(stream, "M:\\", static (_, _) => { }, static () => null, "zip");

        var entries = zipFs.Core.ArchiveEntries;
        Assert.NotNull(entries);

        var storedKey = entries.Keys.FirstOrDefault(static k => k.Contains("stored.txt"));
        Assert.NotNull(storedKey);

        var result = zipFs.Core.IsStoredEntry(entries[storedKey]);
        Assert.True(result);
    }

    [Fact]
    public void IsStoredEntry_CompressedZipEntry_ReturnsFalse()
    {
        using var stream = CreateZipStream();
        using var zipFs = new WinFspZipFs(stream, "M:\\", static (_, _) => { }, static () => null, "zip");

        var entries = zipFs.Core.ArchiveEntries;
        Assert.NotNull(entries);

        var readmeKey = entries.Keys.FirstOrDefault(static k => k.Contains("readme.txt"));
        Assert.NotNull(readmeKey);

        var result = zipFs.Core.IsStoredEntry(entries[readmeKey]);
        Assert.False(result);
    }

    // ─── 7z archive type tests ───

    private static MemoryStream CreateSevenZipStream()
    {
        var ms = new MemoryStream();
        using (var writer = WriterFactory.OpenWriter(ms, ArchiveType.SevenZip,
                   new SevenZipWriterOptions(CompressionType.LZMA)))
        {
            var contentBytes = "Hello from 7z archive"u8.ToArray();
            using var contentStream = new MemoryStream(contentBytes);
            writer.Write("readme.txt", contentStream, null);
        }

        ms.Position = 0;
        return ms;
    }

    [Fact]
    public void SevenZip_OpenOrCreateFile_ReturnsSuccess()
    {
        using var stream = CreateSevenZipStream();
        using var zipFs = new WinFspZipFs(stream, "M:\\", static (_, _) => { }, static () => null, "7z");

        var result = zipFs.OpenOrCreateFile("\\readme.txt", out var fileNode, out var fileDesc, out _, out _);

        Assert.Equal(StatusSuccess, result);
        Assert.NotNull(fileNode);
        Assert.NotNull(fileDesc);

        InvokeCloseReflect(zipFs, fileNode, fileDesc);
    }

    [Fact]
    public void SevenZip_OpenOrCreateFile_NonExistent_ReturnsObjectNameNotFound()
    {
        using var stream = CreateSevenZipStream();
        using var zipFs = new WinFspZipFs(stream, "M:\\", static (_, _) => { }, static () => null, "7z");

        var result = zipFs.OpenOrCreateFile("\\nope.txt", out _, out _, out _, out _);

        Assert.Equal(StatusObjectNameNotFound, result);
    }

    // ─── TrackedMemoryStream disposal test ───

    [Fact]
    public void OpenOrCreateFile_Close_DecrementsMemoryUsage()
    {
        _zipFs.Core.CurrentMemoryUsage = 0L;

        InvokeOpenOrCreateFile("\\readme.txt", out var fileNode, out var fileDesc, out _, out _);

        var afterOpen = _zipFs.Core.CurrentMemoryUsage;
        Assert.True(afterOpen > 0);

        InvokeClose(fileNode, fileDesc);

        var afterClose = _zipFs.Core.CurrentMemoryUsage;
        Assert.Equal(0, afterClose);
    }

    // ─── Memory throttling test ───

    [Fact]
    public void OpenOrCreateFile_MemoryThrottling_FallsBackToDiskCache()
    {
        var maxTotal = _zipFs.Core.MaxTotalMemoryCache;
        _zipFs.Core.CurrentMemoryUsage = maxTotal - 5;

        InvokeOpenOrCreateFile("\\readme.txt", out _, out var fileDesc, out _, out _);

        Assert.NotNull(fileDesc);
        Assert.IsType<FileStream>(fileDesc);

        ((IDisposable)fileDesc).Dispose();
        _zipFs.Core.CurrentMemoryUsage = 0L;
    }

    // ─── Read via reflection ───

    [Fact]
    public void Read_ValidFile_ReadsContent()
    {
        InvokeOpenOrCreateFile("\\readme.txt", out var fileNode, out var fileDesc, out _, out _);

        var buffer = Marshal.AllocHGlobal(100);
        try
        {
            var result = _zipFs.Read(fileNode, fileDesc, buffer, 0ul, 100u, out _);

            Assert.Equal(StatusSuccess, result);
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }

        InvokeClose(fileNode, fileDesc);
    }

    [Fact]
    public void GetFileInfo_ValidNode_ReturnsSuccess()
    {
        InvokeOpenOrCreateFile("\\readme.txt", out var fileNode, out var fileDesc, out _, out _);

        var result = _zipFs.GetFileInfo(fileNode, fileDesc, out _);

        Assert.Equal(StatusSuccess, result);

        InvokeClose(fileNode, fileDesc);
    }

    [Fact]
    public void GetVolumeInfo_ReturnsExpectedValues()
    {
        var result = _zipFs.GetVolumeInfo(out _);

        Assert.Equal(StatusSuccess, result);
    }

    [Fact]
    public void SetVolumeLabel_ReturnsAccessDenied()
    {
        var result = _zipFs.SetVolumeLabel("NewLabel", out _);

        Assert.Equal(StatusAccessDenied, result);
    }

    [Fact]
    public void CanDelete_ReturnsAccessDenied()
    {
        var result = _zipFs.CanDelete(null!, null!, "\\test.txt");

        Assert.Equal(StatusAccessDenied, result);
    }

    [Fact]
    public void Rename_ReturnsAccessDenied()
    {
        var result = _zipFs.Rename(null!, null!, "\\old.txt", "\\new.txt", false);

        Assert.Equal(StatusAccessDenied, result);
    }

    [Fact]
    public void SetFileSize_ReturnsAccessDenied()
    {
        var result = _zipFs.SetFileSize(null!, null!, 100ul, false, out _);

        Assert.Equal(StatusAccessDenied, result);
    }

    [Fact]
    public void SetBasicInfo_ReturnsAccessDenied()
    {
        var result = _zipFs.SetBasicInfo(null!, null!, 0u, 0ul, 0ul, 0ul, 0ul, out _);

        Assert.Equal(StatusAccessDenied, result);
    }

    [Fact]
    public void Flush_ReturnsAccessDenied()
    {
        var result = _zipFs.Flush(null!, null!, out _);

        Assert.Equal(StatusAccessDenied, result);
    }

    [Fact]
    public void Overwrite_ReturnsAccessDenied()
    {
        var result = _zipFs.Overwrite(null!, null!, 0u, false, 0ul, out _);

        Assert.Equal(StatusAccessDenied, result);
    }

    [Fact]
    public void GetSecurity_ReturnsSuccess()
    {
        var securityDescriptor = Array.Empty<byte>();
        var result = _zipFs.GetSecurity(null!, null!, ref securityDescriptor);

        Assert.Equal(StatusSuccess, result);
    }

    [Fact]
    public void SetSecurity_ReturnsAccessDenied()
    {
        var result = _zipFs.SetSecurity(null!, null!, AccessControlSections.All, Array.Empty<byte>());

        Assert.Equal(StatusAccessDenied, result);
    }

    [Fact]
    public void GetSecurityByName_Root_ReturnsDirectory()
    {
        var securityDescriptor = Array.Empty<byte>();
        var result = _zipFs.GetSecurityByName("\\", out var fileAttributes, ref securityDescriptor);

        Assert.Equal(StatusSuccess, result);
        Assert.Equal((uint)FileAttributes.Directory, fileAttributes);
    }

    [Fact]
    public void GetSecurityByName_File_ReturnsReadOnlyArchive()
    {
        var securityDescriptor = Array.Empty<byte>();
        var result = _zipFs.GetSecurityByName("\\readme.txt", out var fileAttributes, ref securityDescriptor);

        Assert.Equal(StatusSuccess, result);
        Assert.Equal((uint)(FileAttributes.Archive | FileAttributes.ReadOnly), fileAttributes);
    }

    [Fact]
    public void GetSecurityByName_Unknown_ReturnsObjectNameNotFound()
    {
        var securityDescriptor = Array.Empty<byte>();
        var result = _zipFs.GetSecurityByName("\\unknown.txt", out _, ref securityDescriptor);

        Assert.Equal(StatusObjectNameNotFound, result);
    }

    [Fact]
    public void GetSecurityByName_PathTooLong_ReturnsUnsuccessful()
    {
        var longPath = "\\" + new string('a', 260);
        var securityDescriptor = Array.Empty<byte>();
        var result = _zipFs.GetSecurityByName(longPath, out _, ref securityDescriptor);

        Assert.Equal(StatusUnsuccessful, result);
    }

    // ─── ReadDirectoryEntry via reflection ───

    [Fact]
    public void ReadDirectoryEntry_RootReturnsChildren()
    {
        InvokeOpenOrCreateFile("\\", out var fileNode, out _, out _, out _);

        object context = null!;
        var names = new List<string>();
        while (_zipFs.ReadDirectoryEntry(fileNode, null!, "*", "", ref context, out var fileName, out _))
        {
            names.Add(fileName);
        }

        Assert.Contains("readme.txt", names);
        Assert.Contains("data", names);
        Assert.Contains("empty", names);
    }

    [Fact]
    public void ReadDirectoryEntry_SubdirectoryReturnsChildren()
    {
        InvokeOpenOrCreateFile("\\data", out var fileNode, out _, out _, out _);

        object context = null!;
        var names = new List<string>();
        while (_zipFs.ReadDirectoryEntry(fileNode, null!, "*", "", ref context, out var fileName, out _))
        {
            names.Add(fileName);
        }

        Assert.Equal(3, names.Count);
        Assert.Contains(".", names);
        Assert.Contains("..", names);
        Assert.Contains("info.txt", names);
    }

    [Fact]
    public void ReadDirectoryEntry_WildcardReturnsAll()
    {
        InvokeOpenOrCreateFile("\\", out var fileNode, out _, out _, out _);

        object context = null!;
        var names = new List<string>();
        while (_zipFs.ReadDirectoryEntry(fileNode, null!, "*", "", ref context, out var fileName, out _))
        {
            names.Add(fileName);
        }

        Assert.Contains("readme.txt", names);
        Assert.Contains("data", names);
        Assert.Contains("empty", names);
    }

    [Fact]
    public void ReadDirectoryEntry_NoMatch_ReturnsOnlyDots()
    {
        InvokeOpenOrCreateFile("\\", out var fileNode, out _, out _, out _);

        object context = null!;
        var names = new List<string>();
        while (_zipFs.ReadDirectoryEntry(fileNode, null!, "*.xyz", "", ref context, out var fileName, out _))
        {
            names.Add(fileName);
        }

        Assert.Contains(".", names);
        Assert.Contains("..", names);
        Assert.DoesNotContain("readme.txt", names);
        Assert.DoesNotContain("data", names);
    }

    // ─── ResolveSpecialPaths tests ───

    [Fact]
    public void ResolveSpecialPaths_Root_ReturnsRoot()
    {
        var result = ZipFsHelpers.ResolveSpecialPaths("/");

        Assert.Equal("/", result);
    }

    [Fact]
    public void ResolveSpecialPaths_DotPath_RemovesDot()
    {
        var result = ZipFsHelpers.ResolveSpecialPaths("/data/./file.txt");

        Assert.Equal("/data/file.txt", result);
    }

    [Fact]
    public void ResolveSpecialPaths_DoubleDotPath_RemovesParent()
    {
        var result = ZipFsHelpers.ResolveSpecialPaths("/data/../other/file.txt");

        Assert.Equal("/other/file.txt", result);
    }

    [Fact]
    public void ResolveSpecialPaths_NestedDotDot_ResolvesCorrectly()
    {
        var result = ZipFsHelpers.ResolveSpecialPaths("/a/b/c/../../d");

        Assert.Equal("/a/d", result);
    }

    [Fact]
    public void ResolveSpecialPaths_DoubleDotAtRoot_StaysAtRoot()
    {
        var result = ZipFsHelpers.ResolveSpecialPaths("/../something");

        Assert.Equal("/something", result);
    }

    [Fact]
    public void ResolveSpecialPaths_MultipleDots_RemovesAll()
    {
        var result = ZipFsHelpers.ResolveSpecialPaths("/./a/./b/.");

        Assert.Equal("/a/b", result);
    }

    [Fact]
    public void ResolveSpecialPaths_OnlyDots_ResolvesToRoot()
    {
        var result = ZipFsHelpers.ResolveSpecialPaths("/./..");

        Assert.Equal("/", result);
    }

    // ─── GetDirInfoByName tests ───

    [Fact]
    public void GetDirInfoByName_ExistingChild_ReturnsSuccess()
    {
        InvokeOpenOrCreateFile("\\", out var fileNode, out _, out _, out _);

        var result = _zipFs.GetDirInfoByName(fileNode, null!, "readme.txt", out _, out _);

        Assert.Equal(StatusSuccess, result);
    }

    [Fact]
    public void GetDirInfoByName_NonExistentChild_ReturnsObjectNameNotFound()
    {
        InvokeOpenOrCreateFile("\\", out var fileNode, out _, out _, out _);

        var result = _zipFs.GetDirInfoByName(fileNode, null!, "nonexistent.txt", out _, out _);

        Assert.Equal(StatusObjectNameNotFound, result);
    }

    [Fact]
    public void GetDirInfoByName_ExistingDirectory_ReturnsSuccess()
    {
        InvokeOpenOrCreateFile("\\", out var fileNode, out _, out _, out _);

        var result = _zipFs.GetDirInfoByName(fileNode, null!, "data", out _, out _);

        Assert.Equal(StatusSuccess, result);
    }

    [Fact]
    public void GetDirInfoByName_ParentNotDirectory_ReturnsNotADirectory()
    {
        InvokeOpenOrCreateFile("\\readme.txt", out var fileNode, out var fileDesc, out _, out _);

        var result = _zipFs.GetDirInfoByName(fileNode, null!, "child.txt", out _, out _);

        // A file node used as parent should return NOT_A_DIRECTORY
        Assert.Equal(unchecked((int)0xC0000103), result); // STATUS_NOT_A_DIRECTORY

        InvokeClose(fileNode, fileDesc);
    }

    // ─── StoredEntry stream tests ───

    [Fact]
    public void Read_StoredEntry_ReadsContent()
    {
        using var stream = CreateStoredZipStream();
        using var zipFs = new WinFspZipFs(stream, "M:\\", static (_, _) => { }, static () => null, "zip", 1);

        zipFs.OpenOrCreateFile("\\stored.txt", out var fileNode, out var fileDesc, out _, out _);

        var buffer = Marshal.AllocHGlobal(200);
        try
        {
            var readResult = zipFs.Read(fileNode, fileDesc, buffer, 0ul, 200u, out _);
            Assert.Equal(StatusSuccess, readResult);
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }

        InvokeCloseReflect(zipFs, fileNode, fileDesc);
    }

    // ─── Init tests ───

    [Fact]
    public void Init_ReturnsSuccess()
    {
        var result = _zipFs.Init(null!);

        Assert.Equal(StatusSuccess, result);
    }

    public void Dispose()
    {
        _zipFs.Dispose();
        _stream.Dispose();
        GC.SuppressFinalize(this);
    }
}
