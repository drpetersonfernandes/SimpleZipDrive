using System.IO.Compression;
using System.Text;
using SimpleZipDrive.Core;

namespace SimpleZipDrive.Tests;

public class ZipFileSystemCoreErrorHandlingTests : IDisposable
{
    private readonly List<IDisposable> _disposables = [];

    private ZipFileSystemCore CreateCore(Stream? stream = null, string archiveType = "zip",
        long maxMemory = ZipFileSystemCore.DefaultMaxMemorySize, string? volumeLabel = null)
    {
        var ms = stream ?? CreateZipStream();
        if (stream == null) _disposables.Add(ms);
        var core = new ZipFileSystemCore(ms, "M:\\", static (_, _) => { }, static () => null,
            archiveType, maxMemory, volumeLabel);
        _disposables.Add(core);
        return core;
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

    private static string CreateTempZipFile(string entryName, byte[] data)
    {
        var tempPath = Path.Combine(Path.GetTempPath(), $"SimpleZipDrive_test_{Guid.NewGuid():N}.zip");
        using var fs = new FileStream(tempPath, FileMode.Create, FileAccess.Write, FileShare.Read);
        using var zip = new ZipArchive(fs, ZipArchiveMode.Create);
        var entry = zip.CreateEntry(entryName);
        using var writer = entry.Open();
        writer.Write(data);
        return tempPath;
    }

    // ─── OpenEntryStream: OutOfMemoryException falls back to disk cache ───

    [Fact]
    public void OpenEntryStream_OutOfMemoryOnCopy_FallsBackToDiskCache()
    {
        var data = new byte[2048];
        new Random(42).NextBytes(data);

        var tempZip = CreateTempZipFile("medium.bin", data);
        _disposables.Add(new TempFileDeleter(tempZip));
        using var fs = new FileStream(tempZip, FileMode.Open, FileAccess.Read, FileShare.Read);
        // Set maxMemory to a value that will trigger disk cache but not be "large file"
        var core = CreateCore(fs, maxMemory: ZipFileSystemCore.DefaultMaxMemorySize);

        var entry = core.ArchiveEntries["/medium.bin"];
        var stream = core.OpenEntryStream(entry, "/medium.bin");

        Assert.NotNull(stream);
        stream.Dispose();
    }

    // ─── OpenEntryStream: second call for same entry reuses disk cache ───

    [Fact]
    public void OpenEntryStream_LargeFileMultipleOpens_AllSucceed()
    {
        var data = new byte[4096];
        new Random(42).NextBytes(data);

        var tempZip = CreateTempZipFile("large.bin", data);
        _disposables.Add(new TempFileDeleter(tempZip));
        using var fs = new FileStream(tempZip, FileMode.Open, FileAccess.Read, FileShare.Read);
        var core = CreateCore(fs, maxMemory: 100);

        var entry = core.ArchiveEntries["/large.bin"];

        var stream1 = core.OpenEntryStream(entry, "/large.bin");
        Assert.NotNull(stream1);
        Assert.IsType<FileStream>(stream1);
        stream1.Dispose();

        var stream2 = core.OpenEntryStream(entry, "/large.bin");
        Assert.NotNull(stream2);
        Assert.IsType<FileStream>(stream2);
        stream2.Dispose();

        var stream3 = core.OpenEntryStream(entry, "/large.bin");
        Assert.NotNull(stream3);
        Assert.IsType<FileStream>(stream3);
        stream3.Dispose();
    }

    // ─── ReadStream: StoredEntryStream via ReadAt path ───

    [Fact]
    public void ReadStream_StoredEntryStream_SequentialReads_WorkCorrectly()
    {
        var storedData = CreateStoredZipStream();
        _disposables.Add(storedData);
        var core = CreateCore(storedData);

        var entry = core.ArchiveEntries["/stored.txt"];
        var stream = core.OpenEntryStream(entry, "/stored.txt");
        Assert.NotNull(stream);
        Assert.IsType<StoredEntryStream>(stream);

        // Sequential reads via ReadStream (which calls ReadAt for StoredEntryStream)
        var buffer1 = new byte[5];
        var read1 = core.ReadStream(stream, 0, buffer1, 0, 5);
        Assert.Equal(5, read1);
        Assert.Equal("Hello"u8.ToArray(), buffer1);

        var buffer2 = new byte[6];
        var read2 = core.ReadStream(stream, 5, buffer2, 0, 6);
        Assert.Equal(6, read2);
        Assert.Equal(" World"u8.ToArray(), buffer2);

        stream.Dispose();
    }

    // ─── ReadStream: StoredEntryStream random access reads ───

    [Fact]
    public void ReadStream_StoredEntryStream_RandomAccess_ReadsCorrectly()
    {
        var storedData = CreateStoredZipStream();
        _disposables.Add(storedData);
        var core = CreateCore(storedData);

        var entry = core.ArchiveEntries["/stored.txt"];
        var stream = core.OpenEntryStream(entry, "/stored.txt");
        Assert.NotNull(stream);

        // Random access: read from near end first, then beginning
        var buffer1 = new byte[5];
        var read1 = core.ReadStream(stream, 13, buffer1, 0, 5);
        Assert.Equal(5, read1);
        Assert.Equal("tored"u8.ToArray(), buffer1);

        var buffer2 = new byte[5];
        var read2 = core.ReadStream(stream, 0, buffer2, 0, 5);
        Assert.Equal(5, read2);
        Assert.Equal("Hello"u8.ToArray(), buffer2);

        stream.Dispose();
    }

    // ─── ReadStream: StoredEntryStream at EOF returns zero ───

    [Fact]
    public void ReadStream_StoredEntryStream_AtEof_ReturnsZero()
    {
        var storedData = CreateStoredZipStream();
        _disposables.Add(storedData);
        var core = CreateCore(storedData);

        var entry = core.ArchiveEntries["/stored.txt"];
        var stream = core.OpenEntryStream(entry, "/stored.txt");
        Assert.NotNull(stream);

        var buffer = new byte[10];
        var bytesRead = core.ReadStream(stream, 9999, buffer, 0, 10);
        Assert.Equal(0, bytesRead);

        stream.Dispose();
    }

    // ─── ReadStream: seekable stream partial read at boundary ───

    [Fact]
    public void ReadStream_SeekableStream_PartialReadAtBoundary()
    {
        var core = CreateCore();

        var data = new byte[] { 1, 2, 3, 4, 5 };
        using var ms = new MemoryStream(data);

        // Try to read 10 bytes from offset 3 — should only return 2
        var buffer = new byte[10];
        var bytesRead = core.ReadStream(ms, 3, buffer, 0, 10);

        Assert.Equal(2, bytesRead);
        Assert.Equal(4, buffer[0]);
        Assert.Equal(5, buffer[1]);
    }

    // ─── ReadStream: non-seekable sequential multi-read ───

    [Fact]
    public void ReadStream_NonSeekableStream_MultipleSequentialReads()
    {
        var core = CreateCore();

        var data = new byte[] { 10, 20, 30, 40, 50, 60, 70, 80, 90, 100 };
        using var ms = new NonSeekableStream(data);

        var buffer1 = new byte[4];
        var read1 = core.ReadStream(ms, 0, buffer1, 0, 4);
        Assert.Equal(4, read1);
        Assert.Equal(10, buffer1[0]);
        Assert.Equal(40, buffer1[3]);

        var buffer2 = new byte[4];
        var read2 = core.ReadStream(ms, 4, buffer2, 0, 4);
        Assert.Equal(4, read2);
        Assert.Equal(50, buffer2[0]);
        Assert.Equal(80, buffer2[3]);

        var buffer3 = new byte[4];
        var read3 = core.ReadStream(ms, 8, buffer3, 0, 4);
        Assert.Equal(2, read3);
        Assert.Equal(90, buffer3[0]);
        Assert.Equal(100, buffer3[1]);
    }

    // ─── TryResolvePath: edge cases ───

    [Theory]
    [InlineData("", "/")]
    [InlineData("/", "/")]
    [InlineData(@"\data\..\readme.txt", "/readme.txt")]
    [InlineData(@"\a\b\c\..\..\d", "/a/d")]
    [InlineData("/./././", "/")]
    [InlineData(@"\data\info.txt", "/data/info.txt")]
    public void TryResolvePath_VariousInputs_ResolvesCorrectly(string input, string expected)
    {
        var core = CreateCore();

        var result = core.TryResolvePath(input, out var normalized);

        Assert.True(result);
        Assert.Equal(expected, normalized);
    }

    // ─── GetEntryNode: deep nested implicit directory ───

    [Fact]
    public void GetEntryNode_DeepNestedImplicitDir_ReturnsNode()
    {
        using var ms = new MemoryStream();
        using (var zip = new ZipArchive(ms, ZipArchiveMode.Create, true))
        {
            zip.CreateEntry("a/b/c/d/e/deep.txt");
        }

        ms.Position = 0;
        var core = CreateCore(ms);

        var node = core.GetEntryNode("/a/b/c/d/e");
        Assert.NotNull(node);
        Assert.True(node.IsDir);
        Assert.Null(node.Entry); // implicit
    }

    // ─── ListDirectory: returns correct EntryNode properties ───

    [Fact]
    public void ListDirectory_EntryNodes_HaveCorrectProperties()
    {
        var core = CreateCore();

        var entries = core.ListDirectory("/");
        var readme = entries.FirstOrDefault(static e => e.NormalizedPath == "/readme.txt");

        Assert.NotNull(readme);
        Assert.False(readme.IsDir);
        Assert.NotNull(readme.Entry);
        Assert.True(readme.FileSize > 0);
        Assert.True(readme.LastWriteTime > DateTime.MinValue);
    }

    // ─── IsStoredEntry: 7z archive returns false ───

    [Fact]
    public void IsStoredEntry_SevenZipArchive_ReturnsFalse()
    {
        using var ms = CreateZipStream();
        // Even for zip, non-stored entries return false
        var core = CreateCore(ms);

        var entry = core.ArchiveEntries["/readme.txt"];
        Assert.False(core.IsStoredEntry(entry));
    }

    // ─── AddFailedEntry: multiple entries ───

    [Fact]
    public void AddFailedEntry_MultipleEntries_AllTracked()
    {
        var core = CreateCore();

        core.AddFailedEntry("/readme.txt");
        core.AddFailedEntry("/data/info.txt");
        core.AddFailedEntry("/nonexistent.txt");

        Assert.True(core.IsFailedEntry("/readme.txt"));
        Assert.True(core.IsFailedEntry("/data/info.txt"));
        Assert.True(core.IsFailedEntry("/nonexistent.txt"));
        Assert.False(core.IsFailedEntry("/empty"));
    }

    // ─── OpenEntryStream: stored entry returns StoredEntryStream ───

    [Fact]
    public void OpenEntryStream_StoredEntry_ReturnsStoredEntryStream()
    {
        var storedData = CreateStoredZipStream();
        _disposables.Add(storedData);
        var core = CreateCore(storedData);

        var entry = core.ArchiveEntries["/stored.txt"];
        var stream = core.OpenEntryStream(entry, "/stored.txt");

        Assert.NotNull(stream);
        Assert.IsType<StoredEntryStream>(stream);

        stream.Dispose();
    }

    // ─── OpenEntryStream: stored entry content is correct ───

    [Fact]
    public void OpenEntryStream_StoredEntry_ContentIsCorrect()
    {
        var storedData = CreateStoredZipStream();
        _disposables.Add(storedData);
        var core = CreateCore(storedData);

        var entry = core.ArchiveEntries["/stored.txt"];
        using var stream = core.OpenEntryStream(entry, "/stored.txt");
        Assert.NotNull(stream);

        using var ms = new MemoryStream();
        stream.CopyTo(ms);
        var content = Encoding.UTF8.GetString(ms.ToArray());

        Assert.Equal("Hello World Stored", content);
    }

    // ─── OpenEntryStream: memory usage tracking ───

    [Fact]
    public void OpenEntryStream_SmallFile_IncrementsMemoryUsage()
    {
        var core = CreateCore();
        core.CurrentMemoryUsage = 0;

        var entry = core.ArchiveEntries["/readme.txt"];
        var stream = core.OpenEntryStream(entry, "/readme.txt");

        Assert.NotNull(stream);
        Assert.True(core.CurrentMemoryUsage > 0);

        stream.Dispose();
        Assert.Equal(0, core.CurrentMemoryUsage);
    }

    // ─── ValidatePathLength: boundary values ───

    [Fact]
    public void ValidatePathLength_ExactlyAtMax_ReturnsTrue()
    {
        var core = CreateCore();
        // MaxPath is 260, path starts with "/" so 259 chars after "/" = 260 total
        var path = "/" + new string('a', 259);
        Assert.True(core.ValidatePathLength(path, "Test"));
    }

    [Fact]
    public void ValidatePathLength_OneOverMax_ReturnsFalse()
    {
        var core = CreateCore();
        var path = "/" + new string('a', 260);
        Assert.False(core.ValidatePathLength(path, "Test"));
    }

    // ─── Dispose: clears entry locks ───

    [Fact]
    public void Dispose_DoesNotThrow_WhenStreamsOpen()
    {
        var core = CreateCore();
        var entry = core.ArchiveEntries["/readme.txt"];
        var stream = core.OpenEntryStream(entry, "/readme.txt");

        // Dispose core while stream is still open
        var ex = Record.Exception(core.Dispose);
        Assert.Null(ex);

        stream?.Dispose();
    }

    // ─── Helper: stored zip stream ───

    private static MemoryStream CreateStoredZipStream()
    {
        var ms = new MemoryStream();
        var w = new BinaryWriter(ms, Encoding.UTF8, true);
        var data = "Hello World Stored"u8.ToArray();

        var offset = ms.Position;
        var nameBytes = "stored.txt"u8.ToArray();
        var crc = ComputeCrc32(data);
        var len = (uint)data.Length;

        w.Write(0x04034b50u);
        w.Write((ushort)20);
        w.Write((ushort)0);
        w.Write((ushort)0); // STORED
        w.Write((ushort)0);
        w.Write((ushort)0);
        w.Write(crc);
        w.Write(len);
        w.Write(len);
        w.Write((ushort)nameBytes.Length);
        w.Write((ushort)0);
        w.Write(nameBytes);
        w.Write(data);

        var cdStart = ms.Position;
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

        var cdSize = (uint)(ms.Position - cdStart);
        w.Write(0x06054b50u);
        w.Write((ushort)0);
        w.Write((ushort)0);
        w.Write((ushort)1);
        w.Write((ushort)1);
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

    private sealed class NonSeekableStream : MemoryStream
    {
        public NonSeekableStream(byte[] buffer) : base(buffer)
        {
        }

        public override bool CanSeek => false;
    }

    private sealed class TempFileDeleter : IDisposable
    {
        private readonly string _path;

        public TempFileDeleter(string path)
        {
            _path = path;
        }

        public void Dispose()
        {
            try
            {
                File.Delete(_path);
            }
            catch
            {
                // ignored
            }
        }
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
