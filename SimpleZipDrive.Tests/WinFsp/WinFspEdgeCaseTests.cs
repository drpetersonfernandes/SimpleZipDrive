using System.Diagnostics.CodeAnalysis;
using System.IO.Compression;
using System.Text;
using SimpleZipDrive.Core;
using WinFspZipFs = SimpleZipDrive_WinFsp.ZipFs;

namespace SimpleZipDrive.Tests.WinFsp;

[SuppressMessage("ReSharper", "NullableWarningSuppressionIsUsed")]
public class WinFspEdgeCaseTests : IDisposable
{
    private readonly List<IDisposable> _disposables = [];

    private const int StatusSuccess = 0;

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

    // ─── OpenOrCreateFile: implicit directory ───

    [Fact]
    public void OpenOrCreateFile_ImplicitDirectory_ReturnsSuccess()
    {
        var zipFs = CreateZipFs();

        var result = zipFs.OpenOrCreateFile("\\data", out var fileNode, out var fileDesc, out var fileInfo, out _);

        Assert.Equal(StatusSuccess, result);
        Assert.Equal((uint)FileAttributes.Directory, fileInfo.FileAttributes);

        zipFs.Close(fileNode, fileDesc);
    }

    // ─── OpenOrCreateFile: nested path ───

    [Fact]
    public void OpenOrCreateFile_NestedPath_ReturnsSuccess()
    {
        var zipFs = CreateZipFs();

        var result = zipFs.OpenOrCreateFile(@"\data\info.txt", out var fileNode, out var fileDesc, out var fileInfo, out _);

        Assert.Equal(StatusSuccess, result);
        Assert.True(fileInfo.FileSize > 0);

        zipFs.Close(fileNode, fileDesc);
    }

    // ─── Read: at various offsets ───

    [Fact]
    public void Read_AtVariousOffsets_ReadsCorrectContent()
    {
        var zipFs = CreateZipFs();

        zipFs.OpenOrCreateFile("\\readme.txt", out var fileNode, out var fileDesc, out _, out _);

        var buffer = Marshal.AllocHGlobal(100);
        try
        {
            // Read from beginning
            var result1 = zipFs.Read(fileNode, fileDesc, buffer, 0ul, 5u, out _);
            Assert.Equal(StatusSuccess, result1);

            // Read from offset
            var result2 = zipFs.Read(fileNode, fileDesc, buffer, 6ul, 5u, out _);
            Assert.Equal(StatusSuccess, result2);
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }

        zipFs.Close(fileNode, fileDesc);
    }

    // ─── Read: past end of file ───

    [Fact]
    public void Read_PastEndOfFile_ReturnsSuccess()
    {
        var zipFs = CreateZipFs();

        zipFs.OpenOrCreateFile("\\readme.txt", out var fileNode, out var fileDesc, out _, out _);

        var buffer = Marshal.AllocHGlobal(100);
        try
        {
            var result = zipFs.Read(fileNode, fileDesc, buffer, 999999ul, 100u, out _);
            Assert.Equal(StatusSuccess, result);
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }

        zipFs.Close(fileNode, fileDesc);
    }

    // ─── ReadDirectoryEntry: with pattern filter ───

    [Fact]
    public void ReadDirectoryEntry_WithPatternFilter_FiltersCorrectly()
    {
        var zipFs = CreateZipFs();

        zipFs.OpenOrCreateFile("\\", out var fileNode, out _, out _, out _);

        object context = null!;
        var names = new List<string>();
        while (zipFs.ReadDirectoryEntry(fileNode, null!, "*.txt", "", ref context, out var fileName, out _))
        {
            names.Add(fileName);
        }

        // Should only contain "readme.txt" (and ".", ".." which are always included)
        Assert.Contains("readme.txt", names);
        Assert.DoesNotContain("data", names);
        Assert.DoesNotContain("empty", names);
    }

    // ─── ReadDirectoryEntry: subdirectory listing ───

    [Fact]
    public void ReadDirectoryEntry_Subdirectory_IncludesDotEntries()
    {
        var zipFs = CreateZipFs();

        zipFs.OpenOrCreateFile("\\data", out var fileNode, out _, out _, out _);

        object context = null!;
        var names = new List<string>();
        while (zipFs.ReadDirectoryEntry(fileNode, null!, "*", "", ref context, out var fileName, out _))
        {
            names.Add(fileName);
        }

        Assert.Contains(".", names);
        Assert.Contains("..", names);
        Assert.Contains("info.txt", names);
        Assert.Equal(3, names.Count);
    }

    // ─── GetDirInfoByName: with marker parameter ───

    [Fact]
    public void GetDirInfoByName_WithMarker_ReturnsSuccess()
    {
        var zipFs = CreateZipFs();

        zipFs.OpenOrCreateFile("\\", out var fileNode, out _, out _, out _);

        var result = zipFs.GetDirInfoByName(fileNode, null!, "readme.txt", out _, out _);

        Assert.Equal(StatusSuccess, result);
    }

    // ─── GetSecurityByName: with security descriptor buffer ───

    [Fact]
    public void GetSecurityByName_WithLargeBuffer_ReturnsSuccess()
    {
        var zipFs = CreateZipFs();
        var secDesc = new byte[4096];

        var result = zipFs.GetSecurityByName("\\readme.txt", out var fileAttributes, ref secDesc);

        Assert.Equal(StatusSuccess, result);
        Assert.True((fileAttributes & (uint)FileAttributes.Archive) != 0);
    }

    // ─── OpenOrCreateFile: stored entry with FileStream source ───

    [Fact]
    public void OpenOrCreateFile_StoredEntry_FileStream_ReturnsSuccess()
    {
        var tempPath = CreateTempStoredZipFile("stored.txt", "Hello Stored"u8.ToArray());
        _disposables.Add(new TempFileDeleter(tempPath));

        using var fs = new FileStream(tempPath, FileMode.Open, FileAccess.Read, FileShare.Read);
        var zipFs = new WinFspZipFs(fs, "M:\\", static (_, _) => { }, static () => null, "zip", 1);
        _disposables.Add(zipFs);

        var result = zipFs.OpenOrCreateFile("\\stored.txt", out var fileNode, out var fileDesc, out _, out _);

        Assert.Equal(StatusSuccess, result);
        Assert.NotNull(fileDesc);

        zipFs.Close(fileNode, fileDesc);
    }

    // ─── Read: stored entry with FileStream source ───

    [Fact]
    public void Read_StoredEntry_FileStream_ReadsCorrectContent()
    {
        var expected = "Hello Stored Content"u8.ToArray();
        var tempPath = CreateTempStoredZipFile("stored.txt", expected);
        _disposables.Add(new TempFileDeleter(tempPath));

        using var fs = new FileStream(tempPath, FileMode.Open, FileAccess.Read, FileShare.Read);
        var zipFs = new WinFspZipFs(fs, "M:\\", static (_, _) => { }, static () => null, "zip", 1);
        _disposables.Add(zipFs);

        zipFs.OpenOrCreateFile("\\stored.txt", out var fileNode, out var fileDesc, out _, out _);

        var buffer = Marshal.AllocHGlobal(100);
        try
        {
            var result = zipFs.Read(fileNode, fileDesc, buffer, 0ul, (uint)expected.Length, out _);
            Assert.Equal(StatusSuccess, result);

            var readData = new byte[expected.Length];
            Marshal.Copy(buffer, readData, 0, expected.Length);
            Assert.Equal(expected, readData);
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }

        zipFs.Close(fileNode, fileDesc);
    }

    // ─── GetVolumeInfo: total size matches stream length ───

    [Fact]
    public void GetVolumeInfo_TotalSize_MatchesStreamLength()
    {
        var zipFs = CreateZipFs();

        zipFs.GetVolumeInfo(out var volumeInfo);

        Assert.True(volumeInfo.TotalSize > 0);
    }

    // ─── Helper methods ───

    private static string CreateTempStoredZipFile(string entryName, byte[] data)
    {
        var tempPath = Path.Combine(Path.GetTempPath(), $"SimpleZipDrive_test_{Guid.NewGuid():N}.zip");
        using var fs = new FileStream(tempPath, FileMode.Create, FileAccess.Write, FileShare.Read);
        var w = new BinaryWriter(fs, Encoding.UTF8, true);

        var offset = fs.Position;
        var nameBytes = Encoding.UTF8.GetBytes(entryName);
        var crc = ComputeCrc32(data);
        var len = (uint)data.Length;

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
        w.Write(data);

        var cdStart = fs.Position;
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
        w.Write((uint)offset);
        w.Write(nameBytes);

        var cdSize = (uint)(fs.Position - cdStart);
        w.Write(0x06054b50u);
        w.Write((ushort)0);
        w.Write((ushort)0);
        w.Write((ushort)1);
        w.Write((ushort)1);
        w.Write(cdSize);
        w.Write((uint)cdStart);
        w.Write((ushort)0);
        w.Flush();

        return tempPath;
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

    private sealed class TempFileDeleter : IDisposable
    {
        private readonly string _path;
        public TempFileDeleter(string path)
        {
            _path = path;
        }

        public void Dispose() { try { File.Delete(_path); } catch { /* ignored */ } }
    }

    private static class Marshal
    {
        public static IntPtr AllocHGlobal(int size)
        {
            return System.Runtime.InteropServices.Marshal.AllocHGlobal(size);
        }

        public static void FreeHGlobal(IntPtr ptr)
        {
            System.Runtime.InteropServices.Marshal.FreeHGlobal(ptr);
        }

        public static void Copy(IntPtr source, byte[] destination, int startIndex, int length)
        {
            System.Runtime.InteropServices.Marshal.Copy(source, destination, startIndex, length);
        }
    }

    public void Dispose()
    {
        foreach (var d in _disposables)
        {
            try { d.Dispose(); } catch { /* best effort */ }
        }
        GC.SuppressFinalize(this);
    }
}
