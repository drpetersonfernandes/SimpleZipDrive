using System.Collections;
using System.IO.Compression;
using System.Reflection;
using System.Security.AccessControl;
using System.Text;
using SharpCompress.Common;
using SharpCompress.Writers;
using WinFspZipFs = SimpleZipDrive_WinFsp.ZipFs;

namespace SimpleZipDrive.Tests.WinFsp;

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
        out Fsp.Interop.FileInfo fileInfo, out string normalizedName)
    {
        var method = typeof(WinFspZipFs).GetMethod("OpenOrCreateFile", BindingFlags.NonPublic | BindingFlags.Instance);
        Assert.NotNull(method);

        var args = new object?[] { fileName, null!, null!, default(Fsp.Interop.FileInfo), fileName };
        var result = (int)(method.Invoke(_zipFs, args) ?? StatusUnsuccessful);

        fileNode = args[1] ?? null!;
        fileDesc = args[2] ?? null!;
        fileInfo = args[3] is Fsp.Interop.FileInfo fi ? fi : default;
        normalizedName = (string)(args[4] ?? fileName);

        return result;
    }

    private void InvokeClose(object fileNode, object fileDesc)
    {
        var method = typeof(WinFspZipFs).GetMethod("Close", BindingFlags.Public | BindingFlags.Instance);
        Assert.NotNull(method);
        method.Invoke(_zipFs, [fileNode, fileDesc]);
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

        Assert.Equal("/readme.txt", normalizedName);

        InvokeClose(fileNode, fileDesc);
    }

    [Fact]
    public void OpenOrCreateFile_AfterDispose_ReturnsDeviceNotReady()
    {
        var stream = CreateZipStream();
        var zipFs = new WinFspZipFs(stream, "M:\\", static (_, _) => { }, static () => null, "zip");
        zipFs.Dispose();

        var method = typeof(WinFspZipFs).GetMethod("OpenOrCreateFile", BindingFlags.NonPublic | BindingFlags.Instance);
        Assert.NotNull(method);
        var args = new object?[] { "\\readme.txt", null!, null!, default(Fsp.Interop.FileInfo), "" };
        var result = (int)(method.Invoke(zipFs, args) ?? StatusUnsuccessful);

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
        var method = typeof(WinFspZipFs).GetMethod("NormalizePath", BindingFlags.NonPublic | BindingFlags.Static);
        Assert.NotNull(method);

        var result = method.Invoke(null, [null]);

        Assert.Equal("/", result);
    }

    [Fact]
    public void NormalizePath_Empty_ReturnsRoot()
    {
        var method = typeof(WinFspZipFs).GetMethod("NormalizePath", BindingFlags.NonPublic | BindingFlags.Static);
        Assert.NotNull(method);

        var result = method.Invoke(null, [""]);

        Assert.Equal("/", result);
    }

    [Fact]
    public void NormalizePath_ConvertsBackslashes()
    {
        var method = typeof(WinFspZipFs).GetMethod("NormalizePath", BindingFlags.NonPublic | BindingFlags.Static);
        Assert.NotNull(method);

        var result = method.Invoke(null, [@"foo\bar\baz.txt"]);

        Assert.Equal("/foo/bar/baz.txt", result);
    }

    [Fact]
    public void NormalizePath_AddsLeadingSlash()
    {
        var method = typeof(WinFspZipFs).GetMethod("NormalizePath", BindingFlags.NonPublic | BindingFlags.Static);
        Assert.NotNull(method);

        var result = method.Invoke(null, ["data/info.txt"]);

        Assert.Equal("/data/info.txt", result);
    }

    [Fact]
    public void NormalizePath_PreservesExistingLeadingSlash()
    {
        var method = typeof(WinFspZipFs).GetMethod("NormalizePath", BindingFlags.NonPublic | BindingFlags.Static);
        Assert.NotNull(method);

        var result = method.Invoke(null, ["/already/normalized"]);

        Assert.Equal("/already/normalized", result);
    }

    // ─── IsPasswordRequiredException tests ───

    [Fact]
    public void IsPasswordRequiredException_MessageContainsPassword_ReturnsTrue()
    {
        var method = typeof(WinFspZipFs).GetMethod("IsPasswordRequiredException", BindingFlags.NonPublic | BindingFlags.Static);
        Assert.NotNull(method);

        var result = method.Invoke(null, [new InvalidOperationException("archive requires a password")]);

        Assert.True((bool)(result ?? false));
    }

    [Fact]
    public void IsPasswordRequiredException_MessageContainsEncrypted_ReturnsTrue()
    {
        var method = typeof(WinFspZipFs).GetMethod("IsPasswordRequiredException", BindingFlags.NonPublic | BindingFlags.Static);
        Assert.NotNull(method);

        var result = method.Invoke(null, [new InvalidOperationException("file is encrypted")]);

        Assert.True((bool)(result ?? false));
    }

    [Fact]
    public void IsPasswordRequiredException_NonMatchingMessage_ReturnsFalse()
    {
        var method = typeof(WinFspZipFs).GetMethod("IsPasswordRequiredException", BindingFlags.NonPublic | BindingFlags.Static);
        Assert.NotNull(method);

        var result = method.Invoke(null, [new InvalidOperationException("file not found")]);

        Assert.False((bool)(result ?? true));
    }

    // ─── IsDataErrorException tests ───

    [Theory]
    [InlineData("Data Error", true)]
    [InlineData("Data error occurred", true)]
    [InlineData("some data ERROR here", true)]
    [InlineData("Some random error", false)]
    public void IsDataErrorException_DetectsByMessageContent(string message, bool expected)
    {
        var method = typeof(WinFspZipFs).GetMethod("IsDataErrorException", BindingFlags.NonPublic | BindingFlags.Static);
        Assert.NotNull(method);

        var result = method.Invoke(null, [new InvalidDataException(message)]);

        Assert.Equal(expected, result);
    }

    [Fact]
    public void IsDataErrorException_DetectsByTypeName()
    {
        var method = typeof(WinFspZipFs).GetMethod("IsDataErrorException", BindingFlags.NonPublic | BindingFlags.Static);
        Assert.NotNull(method);

        var result = method.Invoke(null, [new TestDataErrorException("some message")]);

        Assert.True((bool)(result ?? false));
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
        var method = typeof(WinFspZipFs).GetMethod("IsPathLengthValid", BindingFlags.NonPublic | BindingFlags.Static);
        Assert.NotNull(method);

        var result = method.Invoke(null, [null]);

        Assert.True((bool)(result ?? false));
    }

    [Fact]
    public void IsPathLengthValid_Empty_ReturnsTrue()
    {
        var method = typeof(WinFspZipFs).GetMethod("IsPathLengthValid", BindingFlags.NonPublic | BindingFlags.Static);
        Assert.NotNull(method);

        var result = method.Invoke(null, [""]);

        Assert.True((bool)(result ?? false));
    }

    [Fact]
    public void IsPathLengthValid_ExactlyMaxPath_ReturnsTrue()
    {
        var method = typeof(WinFspZipFs).GetMethod("IsPathLengthValid", BindingFlags.NonPublic | BindingFlags.Static);
        Assert.NotNull(method);

        var path = "\\" + new string('a', 259);
        var result = method.Invoke(null, [path]);

        Assert.True((bool)(result ?? false));
    }

    [Fact]
    public void IsPathLengthValid_ExceedsMaxPath_ReturnsFalse()
    {
        var method = typeof(WinFspZipFs).GetMethod("IsPathLengthValid", BindingFlags.NonPublic | BindingFlags.Static);
        Assert.NotNull(method);

        var path = "\\" + new string('a', 260);
        var result = method.Invoke(null, [path]);

        Assert.False((bool)(result ?? true));
    }

    [Fact]
    public void IsPathLengthValid_ExtendedPathPrefix_WithinLimit_ReturnsTrue()
    {
        var method = typeof(WinFspZipFs).GetMethod("IsPathLengthValid", BindingFlags.NonPublic | BindingFlags.Static);
        Assert.NotNull(method);

        var path = @"\\?\" + new string('a', 260);
        var result = method.Invoke(null, [path]);

        Assert.True((bool)(result ?? false));
    }

    // ─── IsMatchSimple tests ───

    [Fact]
    public void IsMatchSimple_WildcardQuestionMark_MatchesSingleChar()
    {
        var method = typeof(WinFspZipFs).GetMethod("IsMatchSimple", BindingFlags.NonPublic | BindingFlags.Static);
        Assert.NotNull(method);

        var result = method.Invoke(null, ["abc.txt", "abc.tx?"]);

        Assert.True((bool)(result ?? false));
    }

    [Fact]
    public void IsMatchSimple_QuestionMark_DoesNotMatchDifferentLength()
    {
        var method = typeof(WinFspZipFs).GetMethod("IsMatchSimple", BindingFlags.NonPublic | BindingFlags.Static);
        Assert.NotNull(method);

        var result = method.Invoke(null, ["abcd.txt", "abc.tx?"]);

        Assert.False((bool)(result ?? true));
    }

    [Fact]
    public void IsMatchSimple_PatternTooLong_ReturnsFalse()
    {
        var method = typeof(WinFspZipFs).GetMethod("IsMatchSimple", BindingFlags.NonPublic | BindingFlags.Static);
        Assert.NotNull(method);

        var pattern = new string('a', 261);
        var result = method.Invoke(null, ["anything", pattern]);

        Assert.False((bool)(result ?? true));
    }

    [Fact]
    public void IsMatchSimple_StarPattern_ReturnsTrue()
    {
        var method = typeof(WinFspZipFs).GetMethod("IsMatchSimple", BindingFlags.NonPublic | BindingFlags.Static);
        Assert.NotNull(method);

        var result = method.Invoke(null, ["readme.txt", "*"]);

        Assert.True((bool)(result ?? false));
    }

    [Fact]
    public void IsMatchSimple_StarDotStarPattern_ReturnsTrue()
    {
        var method = typeof(WinFspZipFs).GetMethod("IsMatchSimple", BindingFlags.NonPublic | BindingFlags.Static);
        Assert.NotNull(method);

        var result = method.Invoke(null, ["test.txt", "*.*"]);

        Assert.True((bool)(result ?? false));
    }

    [Fact]
    public void IsMatchSimple_ExactMatch_ReturnsTrue()
    {
        var method = typeof(WinFspZipFs).GetMethod("IsMatchSimple", BindingFlags.NonPublic | BindingFlags.Static);
        Assert.NotNull(method);

        var result = method.Invoke(null, ["readme.txt", "readme.txt"]);

        Assert.True((bool)(result ?? false));
    }

    [Fact]
    public void IsMatchSimple_NoMatch_ReturnsFalse()
    {
        var method = typeof(WinFspZipFs).GetMethod("IsMatchSimple", BindingFlags.NonPublic | BindingFlags.Static);
        Assert.NotNull(method);

        var result = method.Invoke(null, ["readme.txt", "other*"]);

        Assert.False((bool)(result ?? true));
    }

    // ─── DateTimeToFileTimeUtc tests ───

    [Fact]
    public void DateTimeToFileTimeUtc_MinValue_ReturnsFileTimeForEpoch()
    {
        var method = typeof(WinFspZipFs).GetMethod("DateTimeToFileTimeUtc", BindingFlags.NonPublic | BindingFlags.Static);
        Assert.NotNull(method);

        var result = method.Invoke(null, [DateTime.MinValue]);

        Assert.NotNull(result);
        Assert.IsType<ulong>(result);
        Assert.Equal(0ul, (ulong)result);
    }

    [Fact]
    public void DateTimeToFileTimeUtc_Now_ReturnsValidFileTime()
    {
        var method = typeof(WinFspZipFs).GetMethod("DateTimeToFileTimeUtc", BindingFlags.NonPublic | BindingFlags.Static);
        Assert.NotNull(method);

        var result = method.Invoke(null, [DateTime.Now]);

        Assert.NotNull(result);
        Assert.IsType<ulong>(result);
        Assert.True((ulong)result > 0);
    }

    // ─── EntryNodeToFileInfo tests ───

    [Fact]
    public void EntryNodeToFileInfo_File_ReturnsCorrectInfo()
    {
        var entryNodeType = typeof(WinFspZipFs).GetNestedType("EntryNode", BindingFlags.NonPublic);
        Assert.NotNull(entryNodeType);

        var node = Activator.CreateInstance(entryNodeType);
        Assert.NotNull(node);

        node.GetType().GetField("NormalizedPath")!.SetValue(node, "/test.txt");
        node.GetType().GetField("IsDir")!.SetValue(node, false);
        node.GetType().GetField("FileSize")!.SetValue(node, 1024L);
        node.GetType().GetField("CreationTime")!.SetValue(node, new DateTime(2024, 1, 1));
        node.GetType().GetField("LastWriteTime")!.SetValue(node, new DateTime(2024, 2, 1));
        node.GetType().GetField("LastAccessTime")!.SetValue(node, new DateTime(2024, 3, 1));

        var method = typeof(WinFspZipFs).GetMethod("EntryNodeToFileInfo", BindingFlags.NonPublic | BindingFlags.Static);
        Assert.NotNull(method);

        var result = method.Invoke(null, [node]);
        Assert.NotNull(result);

        var fi = (Fsp.Interop.FileInfo)result;
        Assert.Equal(1024ul, fi.FileSize);
        Assert.Equal((uint)(FileAttributes.Archive | FileAttributes.ReadOnly), fi.FileAttributes);
        Assert.NotEqual(0ul, fi.AllocationSize);
    }

    [Fact]
    public void EntryNodeToFileInfo_Directory_ReturnsDirectoryInfo()
    {
        var entryNodeType = typeof(WinFspZipFs).GetNestedType("EntryNode", BindingFlags.NonPublic);
        Assert.NotNull(entryNodeType);

        var node = Activator.CreateInstance(entryNodeType);
        Assert.NotNull(node);

        node.GetType().GetField("NormalizedPath")!.SetValue(node, "/testdir");
        node.GetType().GetField("IsDir")!.SetValue(node, true);
        node.GetType().GetField("FileSize")!.SetValue(node, 0L);
        node.GetType().GetField("CreationTime")!.SetValue(node, new DateTime(2024, 1, 1));
        node.GetType().GetField("LastWriteTime")!.SetValue(node, new DateTime(2024, 2, 1));
        node.GetType().GetField("LastAccessTime")!.SetValue(node, new DateTime(2024, 3, 1));

        var method = typeof(WinFspZipFs).GetMethod("EntryNodeToFileInfo", BindingFlags.NonPublic | BindingFlags.Static);
        Assert.NotNull(method);

        var result = method.Invoke(null, [node]);
        Assert.NotNull(result);

        var fi = (Fsp.Interop.FileInfo)result;
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

        var method = typeof(WinFspZipFs).GetMethod("IsDirectory", BindingFlags.NonPublic | BindingFlags.Static);
        Assert.NotNull(method);

        var entriesField = typeof(WinFspZipFs).GetField("_archiveEntries", BindingFlags.NonPublic | BindingFlags.Instance);
        Assert.NotNull(entriesField);
        var entries = entriesField.GetValue(zipFs);
        Assert.NotNull(entries);

        var dictType = entries.GetType();
        var valuesProp = dictType.GetProperty("Values");
        Assert.NotNull(valuesProp);
        var values = valuesProp.GetValue(entries) as IEnumerable;
        Assert.NotNull(values);

        object? dirEntry = null;
        foreach (var e in values)
        {
            var keyProp = e.GetType().GetProperty("Key");
            if (keyProp?.GetValue(e) is string key && key.EndsWith('/'))
            {
                dirEntry = e;
                break;
            }
        }

        Assert.NotNull(dirEntry);
        var result = method.Invoke(null, [dirEntry]);
        Assert.True((bool)(result ?? false));
    }

    [Fact]
    public void IsDirectory_FileEntry_ReturnsFalse()
    {
        using var stream = CreateZipStream();
        using var zipFs = new WinFspZipFs(stream, "M:\\", static (_, _) => { }, static () => null, "zip");

        var method = typeof(WinFspZipFs).GetMethod("IsDirectory", BindingFlags.NonPublic | BindingFlags.Static);
        Assert.NotNull(method);

        var entriesField = typeof(WinFspZipFs).GetField("_archiveEntries", BindingFlags.NonPublic | BindingFlags.Instance);
        Assert.NotNull(entriesField);
        var entries = entriesField.GetValue(zipFs);
        Assert.NotNull(entries);

        var dictType = entries.GetType();
        var valuesProp = dictType.GetProperty("Values");
        Assert.NotNull(valuesProp);
        var values = valuesProp.GetValue(entries) as IEnumerable;
        Assert.NotNull(values);

        object? fileEntry = null;
        foreach (var e in values)
        {
            var keyProp = e.GetType().GetProperty("Key");
            if (keyProp?.GetValue(e) is string key && key.Contains("readme.txt"))
            {
                fileEntry = e;
                break;
            }
        }

        Assert.NotNull(fileEntry);
        var result = method.Invoke(null, [fileEntry]);
        Assert.False((bool)(result ?? true));
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

        var method = zipFs.GetType().GetMethod("OpenOrCreateFile", BindingFlags.NonPublic | BindingFlags.Instance);
        Assert.NotNull(method);

        var args = new object?[] { "\\stored.txt", null!, null!, default(Fsp.Interop.FileInfo), "" };
        var result = (int)(method.Invoke(zipFs, args) ?? StatusUnsuccessful);

        Assert.Equal(StatusSuccess, result);
        Assert.NotNull(args[1]);
        Assert.NotNull(args[2]);

        InvokeCloseReflect(zipFs, args[1]!, args[2]!);
    }

    private static void InvokeCloseReflect(WinFspZipFs zipFs, object fileNode, object fileDesc)
    {
        var method = zipFs.GetType().GetMethod("Close", BindingFlags.Public | BindingFlags.Instance);
        Assert.NotNull(method);
        method.Invoke(zipFs, [fileNode, fileDesc]);
    }

    [Fact]
    public void IsStoredEntry_StoredZipEntry_ReturnsTrue()
    {
        using var stream = CreateStoredZipStream();
        using var zipFs = new WinFspZipFs(stream, "M:\\", static (_, _) => { }, static () => null, "zip");

        var method = typeof(WinFspZipFs).GetMethod("IsStoredEntry", BindingFlags.NonPublic | BindingFlags.Instance);
        Assert.NotNull(method);

        var entriesField = typeof(WinFspZipFs).GetField("_archiveEntries", BindingFlags.NonPublic | BindingFlags.Instance);
        Assert.NotNull(entriesField);
        var entries = entriesField.GetValue(zipFs);
        Assert.NotNull(entries);

        var dictType = entries.GetType();
        var keysAndValues = new Dictionary<string, object>();
        var keysProp = dictType.GetProperty("Keys");
        var indexProp = dictType.GetProperty("Item");
        if (keysProp != null && indexProp != null)
        {
            if (keysProp.GetValue(entries) is IEnumerable keys)
            {
                foreach (var key in keys)
                {
                    keysAndValues[(string)key] = indexProp.GetValue(entries, [key])!;
                }
            }
        }

        var storedKey = keysAndValues.Keys.FirstOrDefault(static k => k.Contains("stored.txt"));
        Assert.NotNull(storedKey);

        var result = method.Invoke(zipFs, [keysAndValues[storedKey]]);
        Assert.True((bool)(result ?? false));
    }

    [Fact]
    public void IsStoredEntry_CompressedZipEntry_ReturnsFalse()
    {
        using var stream = CreateZipStream();
        using var zipFs = new WinFspZipFs(stream, "M:\\", static (_, _) => { }, static () => null, "zip");

        var method = typeof(WinFspZipFs).GetMethod("IsStoredEntry", BindingFlags.NonPublic | BindingFlags.Instance);
        Assert.NotNull(method);

        var entriesField = typeof(WinFspZipFs).GetField("_archiveEntries", BindingFlags.NonPublic | BindingFlags.Instance);
        Assert.NotNull(entriesField);
        var entries = entriesField.GetValue(zipFs);
        Assert.NotNull(entries);

        var dictType = entries.GetType();
        var keysProp = dictType.GetProperty("Keys");
        var indexProp = dictType.GetProperty("Item");
        var keysAndValues = new Dictionary<string, object>();
        if (keysProp != null && indexProp != null)
        {
            if (keysProp.GetValue(entries) is IEnumerable keys)
            {
                foreach (var key in keys)
                {
                    keysAndValues[(string)key] = indexProp.GetValue(entries, [key])!;
                }
            }
        }

        var readmeKey = keysAndValues.Keys.FirstOrDefault(static k => k.Contains("readme.txt"));
        Assert.NotNull(readmeKey);

        var result = method.Invoke(zipFs, [keysAndValues[readmeKey]]);
        Assert.False((bool)(result ?? true));
    }

    // ─── 7z archive type tests ───

    private static MemoryStream CreateSevenZipStream()
    {
        var ms = new MemoryStream();
        using (var writer = WriterFactory.OpenWriter(ms, ArchiveType.SevenZip,
                   new SharpCompress.Writers.SevenZip.SevenZipWriterOptions(CompressionType.LZMA)))
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

        var method = typeof(WinFspZipFs).GetMethod("OpenOrCreateFile", BindingFlags.NonPublic | BindingFlags.Instance);
        Assert.NotNull(method);

        var args = new object?[] { "\\readme.txt", null!, null!, default(Fsp.Interop.FileInfo), "" };
        var result = (int)(method.Invoke(zipFs, args) ?? StatusUnsuccessful);

        Assert.Equal(StatusSuccess, result);
        Assert.NotNull(args[1]);
        Assert.NotNull(args[2]);

        InvokeCloseReflect(zipFs, args[1]!, args[2]!);
    }

    [Fact]
    public void SevenZip_OpenOrCreateFile_NonExistent_ReturnsObjectNameNotFound()
    {
        using var stream = CreateSevenZipStream();
        using var zipFs = new WinFspZipFs(stream, "M:\\", static (_, _) => { }, static () => null, "7z");

        var method = typeof(WinFspZipFs).GetMethod("OpenOrCreateFile", BindingFlags.NonPublic | BindingFlags.Instance);
        Assert.NotNull(method);

        var args = new object?[] { "\\nope.txt", null!, null!, default(Fsp.Interop.FileInfo), "" };
        var result = (int)(method.Invoke(zipFs, args) ?? StatusSuccess);

        Assert.Equal(StatusObjectNameNotFound, result);
    }

    // ─── TrackedMemoryStream disposal test ───

    [Fact]
    public void OpenOrCreateFile_Close_DecrementsMemoryUsage()
    {
        var memoryUsageField = typeof(WinFspZipFs).GetField("_currentMemoryUsage", BindingFlags.NonPublic | BindingFlags.Instance);
        Assert.NotNull(memoryUsageField);

        memoryUsageField.SetValue(_zipFs, 0L);

        InvokeOpenOrCreateFile("\\readme.txt", out var fileNode, out var fileDesc, out _, out _);

        var afterOpen = (long)(memoryUsageField.GetValue(_zipFs) ?? throw new InvalidOperationException());
        Assert.True(afterOpen > 0);

        InvokeClose(fileNode, fileDesc);

        var afterClose = (long)(memoryUsageField.GetValue(_zipFs) ?? throw new InvalidOperationException());
        Assert.Equal(0, afterClose);
    }

    // ─── Memory throttling test ───

    [Fact]
    public void OpenOrCreateFile_MemoryThrottling_FallsBackToDiskCache()
    {
        var memoryUsageField = typeof(WinFspZipFs).GetField("_currentMemoryUsage", BindingFlags.NonPublic | BindingFlags.Instance);
        var maxTotalCacheField = typeof(WinFspZipFs).GetField("_maxTotalMemoryCache", BindingFlags.NonPublic | BindingFlags.Instance);
        Assert.NotNull(memoryUsageField);
        Assert.NotNull(maxTotalCacheField);

        var maxTotal = (long)(maxTotalCacheField.GetValue(_zipFs) ?? 1073741824L);
        memoryUsageField.SetValue(_zipFs, maxTotal - 5);

        InvokeOpenOrCreateFile("\\readme.txt", out _, out var fileDesc, out _, out _);

        Assert.NotNull(fileDesc);
        Assert.IsType<FileStream>(fileDesc);

        ((IDisposable)fileDesc).Dispose();
        memoryUsageField.SetValue(_zipFs, 0L);
    }

    // ─── Read via reflection ───

    [Fact]
    public void Read_ValidFile_ReadsContent()
    {
        InvokeOpenOrCreateFile("\\readme.txt", out var fileNode, out var fileDesc, out _, out _);

        var readMethod = typeof(WinFspZipFs).GetMethod("Read", BindingFlags.Public | BindingFlags.Instance);
        Assert.NotNull(readMethod);

        var buffer = System.Runtime.InteropServices.Marshal.AllocHGlobal(100);
        try
        {
            var result = (int)(readMethod.Invoke(_zipFs, [fileNode, fileDesc, buffer, 0ul, 100u, 0u]) ?? StatusUnsuccessful);

            Assert.Equal(StatusSuccess, result);
        }
        finally
        {
            System.Runtime.InteropServices.Marshal.FreeHGlobal(buffer);
        }

        InvokeClose(fileNode, fileDesc);
    }

    [Fact]
    public void GetFileInfo_ValidNode_ReturnsSuccess()
    {
        InvokeOpenOrCreateFile("\\readme.txt", out var fileNode, out var fileDesc, out _, out _);

        var method = typeof(WinFspZipFs).GetMethod("GetFileInfo", BindingFlags.Public | BindingFlags.Instance);
        Assert.NotNull(method);

        var result = (int)(method.Invoke(_zipFs, [fileNode, fileDesc, default(Fsp.Interop.FileInfo)]) ?? StatusUnsuccessful);

        Assert.Equal(StatusSuccess, result);

        InvokeClose(fileNode, fileDesc);
    }

    [Fact]
    public void GetVolumeInfo_ReturnsExpectedValues()
    {
        var method = typeof(WinFspZipFs).GetMethod("GetVolumeInfo", BindingFlags.Public | BindingFlags.Instance);
        Assert.NotNull(method);

        var result = (int)(method.Invoke(_zipFs, [default(Fsp.Interop.VolumeInfo)]) ?? StatusUnsuccessful);

        Assert.Equal(StatusSuccess, result);
    }

    [Fact]
    public void SetVolumeLabel_ReturnsAccessDenied()
    {
        var method = typeof(WinFspZipFs).GetMethod("SetVolumeLabel", BindingFlags.Public | BindingFlags.Instance);
        Assert.NotNull(method);

        var result = (int)(method.Invoke(_zipFs, ["NewLabel", default(Fsp.Interop.VolumeInfo)]) ?? StatusSuccess);

        Assert.Equal(StatusAccessDenied, result);
    }

    [Fact]
    public void CanDelete_ReturnsAccessDenied()
    {
        var method = typeof(WinFspZipFs).GetMethod("CanDelete", BindingFlags.Public | BindingFlags.Instance);
        Assert.NotNull(method);

        var result = (int)(method.Invoke(_zipFs, [null!, null!, "\\test.txt"]) ?? StatusSuccess);

        Assert.Equal(StatusAccessDenied, result);
    }

    [Fact]
    public void Rename_ReturnsAccessDenied()
    {
        var method = typeof(WinFspZipFs).GetMethod("Rename", BindingFlags.Public | BindingFlags.Instance);
        Assert.NotNull(method);

        var result = (int)(method.Invoke(_zipFs, [null!, null!, "\\old.txt", "\\new.txt", false]) ?? StatusSuccess);

        Assert.Equal(StatusAccessDenied, result);
    }

    [Fact]
    public void SetFileSize_ReturnsAccessDenied()
    {
        var method = typeof(WinFspZipFs).GetMethod("SetFileSize", BindingFlags.Public | BindingFlags.Instance);
        Assert.NotNull(method);

        var result = (int)(method.Invoke(_zipFs, [null!, null!, 100ul, false, default(Fsp.Interop.FileInfo)]) ?? StatusSuccess);

        Assert.Equal(StatusAccessDenied, result);
    }

    [Fact]
    public void SetBasicInfo_ReturnsAccessDenied()
    {
        var method = typeof(WinFspZipFs).GetMethod("SetBasicInfo", BindingFlags.Public | BindingFlags.Instance);
        Assert.NotNull(method);

        var result = (int)(method.Invoke(_zipFs, [null!, null!, 0u, 0ul, 0ul, 0ul, 0ul, default(Fsp.Interop.FileInfo)]) ?? StatusSuccess);

        Assert.Equal(StatusAccessDenied, result);
    }

    [Fact]
    public void Flush_ReturnsAccessDenied()
    {
        var method = typeof(WinFspZipFs).GetMethod("Flush", BindingFlags.Public | BindingFlags.Instance);
        Assert.NotNull(method);

        var result = (int)(method.Invoke(_zipFs, [null!, null!, default(Fsp.Interop.FileInfo)]) ?? StatusSuccess);

        Assert.Equal(StatusAccessDenied, result);
    }

    [Fact]
    public void Overwrite_ReturnsAccessDenied()
    {
        var method = typeof(WinFspZipFs).GetMethod("Overwrite", BindingFlags.Public | BindingFlags.Instance);
        Assert.NotNull(method);

        var result = (int)(method.Invoke(_zipFs, [null!, null!, 0u, false, 0ul, default(Fsp.Interop.FileInfo)]) ?? StatusSuccess);

        Assert.Equal(StatusAccessDenied, result);
    }

    [Fact]
    public void GetSecurity_ReturnsSuccess()
    {
        var method = typeof(WinFspZipFs).GetMethod("GetSecurity", BindingFlags.Public | BindingFlags.Instance);
        Assert.NotNull(method);

        var securityDescriptor = Array.Empty<byte>();
        var args = new object?[] { null!, null!, securityDescriptor };
        var result = (int)(method.Invoke(_zipFs, args) ?? StatusUnsuccessful);

        Assert.Equal(StatusSuccess, result);
    }

    [Fact]
    public void SetSecurity_ReturnsAccessDenied()
    {
        var method = typeof(WinFspZipFs).GetMethod("SetSecurity", BindingFlags.Public | BindingFlags.Instance);
        Assert.NotNull(method);

        var result = (int)(method.Invoke(_zipFs, [null!, null!, AccessControlSections.All, Array.Empty<byte>()]) ?? StatusSuccess);

        Assert.Equal(StatusAccessDenied, result);
    }

    [Fact]
    public void GetSecurityByName_Root_ReturnsDirectory()
    {
        var method = typeof(WinFspZipFs).GetMethod("GetSecurityByName", BindingFlags.Public | BindingFlags.Instance);
        Assert.NotNull(method);

        var securityDescriptor = Array.Empty<byte>();
        var args = new object?[] { "\\", 0u, securityDescriptor };
        var result = (int)(method.Invoke(_zipFs, args) ?? StatusUnsuccessful);

        Assert.Equal(StatusSuccess, result);
        Assert.Equal((uint)FileAttributes.Directory, args[1]);
    }

    [Fact]
    public void GetSecurityByName_File_ReturnsReadOnlyArchive()
    {
        var method = typeof(WinFspZipFs).GetMethod("GetSecurityByName", BindingFlags.Public | BindingFlags.Instance);
        Assert.NotNull(method);

        var securityDescriptor = Array.Empty<byte>();
        var args = new object?[] { "\\readme.txt", 0u, securityDescriptor };
        var result = (int)(method.Invoke(_zipFs, args) ?? StatusUnsuccessful);

        Assert.Equal(StatusSuccess, result);
        Assert.Equal((uint)(FileAttributes.Archive | FileAttributes.ReadOnly), (uint?)args[1]);
    }

    [Fact]
    public void GetSecurityByName_Unknown_ReturnsObjectNameNotFound()
    {
        var method = typeof(WinFspZipFs).GetMethod("GetSecurityByName", BindingFlags.Public | BindingFlags.Instance);
        Assert.NotNull(method);

        var securityDescriptor = Array.Empty<byte>();
        var args = new object?[] { "\\unknown.txt", 0u, securityDescriptor };
        var result = (int)(method.Invoke(_zipFs, args) ?? StatusSuccess);

        Assert.Equal(StatusObjectNameNotFound, result);
    }

    [Fact]
    public void GetSecurityByName_PathTooLong_ReturnsUnsuccessful()
    {
        var method = typeof(WinFspZipFs).GetMethod("GetSecurityByName", BindingFlags.Public | BindingFlags.Instance);
        Assert.NotNull(method);

        var longPath = "\\" + new string('a', 260);
        var securityDescriptor = Array.Empty<byte>();
        var args = new object?[] { longPath, 0u, securityDescriptor };
        var result = (int)(method.Invoke(_zipFs, args) ?? StatusSuccess);

        Assert.Equal(StatusUnsuccessful, result);
    }

    // ─── ReadDirectoryEntry via reflection ───

    [Fact]
    public void ReadDirectoryEntry_RootReturnsChildren()
    {
        InvokeOpenOrCreateFile("\\", out var fileNode, out _, out _, out _);

        var method = typeof(WinFspZipFs).GetMethod("ReadDirectoryEntry", BindingFlags.Public | BindingFlags.Instance);
        Assert.NotNull(method);

        object context = null!;
        var names = new List<string>();

        var args = new[] { fileNode, null!, "*", "", context, null!, default(Fsp.Interop.FileInfo) };
        while ((bool)(method.Invoke(_zipFs, args) ?? false))
        {
            names.Add((string)args[5]);
        }

        Assert.Contains("readme.txt", names);
        Assert.Contains("data", names);
        Assert.Contains("empty", names);
    }

    [Fact]
    public void ReadDirectoryEntry_SubdirectoryReturnsChildren()
    {
        InvokeOpenOrCreateFile("\\data", out var fileNode, out _, out _, out _);

        var method = typeof(WinFspZipFs).GetMethod("ReadDirectoryEntry", BindingFlags.Public | BindingFlags.Instance);
        Assert.NotNull(method);

        object context = null!;
        var names = new List<string>();

        var args = new[] { fileNode, null!, "*", "", context, null!, default(Fsp.Interop.FileInfo) };
        while ((bool)(method.Invoke(_zipFs, args) ?? false))
        {
            names.Add((string)args[5]);
        }

        Assert.Single(names);
        Assert.Contains("info.txt", names);
    }

    [Fact]
    public void ReadDirectoryEntry_WildcardReturnsAll()
    {
        InvokeOpenOrCreateFile("\\", out var fileNode, out _, out _, out _);

        var method = typeof(WinFspZipFs).GetMethod("ReadDirectoryEntry", BindingFlags.Public | BindingFlags.Instance);
        Assert.NotNull(method);

        object context = null!;
        var names = new List<string>();

        var args = new[] { fileNode, null!, "*", "", context, null!, default(Fsp.Interop.FileInfo) };
        while ((bool)(method.Invoke(_zipFs, args) ?? false))
        {
            names.Add((string)args[5]);
        }

        Assert.Contains("readme.txt", names);
        Assert.Contains("data", names);
        Assert.Contains("empty", names);
    }

    [Fact]
    public void ReadDirectoryEntry_NoMatch_ReturnsEmpty()
    {
        InvokeOpenOrCreateFile("\\", out var fileNode, out _, out _, out _);

        var method = typeof(WinFspZipFs).GetMethod("ReadDirectoryEntry", BindingFlags.Public | BindingFlags.Instance);
        Assert.NotNull(method);

        var args = new[] { fileNode, null!, "*.xyz", "", null!, null!, default(Fsp.Interop.FileInfo) };
        var hasEntry = (bool)(method.Invoke(_zipFs, args) ?? false);

        Assert.False(hasEntry);
    }

    public void Dispose()
    {
        _zipFs.Dispose();
        _stream.Dispose();
        GC.SuppressFinalize(this);
    }
}
