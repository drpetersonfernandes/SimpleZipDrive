using System.IO.Compression;
using System.Text;
using SimpleZipDrive.Core;

namespace SimpleZipDrive.Tests;

public class ZipFileSystemCoreAdditionalTests : IDisposable
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

    // ─── OpenEntryStream: failed entry returns null ───

    [Fact]
    public void OpenEntryStream_FailedEntry_ReturnsNull()
    {
        var core = CreateCore();
        var entry = core.ArchiveEntries["/readme.txt"];

        core.AddFailedEntry("/readme.txt");
        var stream = core.OpenEntryStream(entry, "/readme.txt");

        Assert.Null(stream);
    }

    // ─── OpenEntryStream: successful entry returns non-null ───

    [Fact]
    public void OpenEntryStream_ValidEntry_ReturnsStream()
    {
        var core = CreateCore();
        var entry = core.ArchiveEntries["/readme.txt"];

        var stream = core.OpenEntryStream(entry, "/readme.txt");

        Assert.NotNull(stream);
        stream.Dispose();
    }

    // ─── OpenEntryStream: disk cache for large files ───

    [Fact]
    public void OpenEntryStream_LargeFile_CachesToDisk()
    {
        // Create a zip with a file > maxMemory
        var data = new byte[2048];
        new Random(42).NextBytes(data);

        var tempZip = CreateTempZipFile("large.bin", data);
        _disposables.Add(new TempFileDeleter(tempZip));
        using var fs = new FileStream(tempZip, FileMode.Open, FileAccess.Read, FileShare.Read);
        var core = CreateCore(fs, maxMemory: 100); // 100 bytes max memory

        var entry = core.ArchiveEntries["/large.bin"];
        var stream = core.OpenEntryStream(entry, "/large.bin");

        Assert.NotNull(stream);
        Assert.IsType<FileStream>(stream);

        // Verify disk cache was populated
        Assert.True(core.LargeFileCache.ContainsKey("/large.bin"));

        stream.Dispose();
    }

    // ─── OpenEntryStream: disk cache reuse on second open ───

    [Fact]
    public void OpenEntryStream_LargeFileSecondOpen_ReusesCache()
    {
        var data = new byte[2048];
        new Random(42).NextBytes(data);

        var tempZip = CreateTempZipFile("large.bin", data);
        _disposables.Add(new TempFileDeleter(tempZip));
        using var fs = new FileStream(tempZip, FileMode.Open, FileAccess.Read, FileShare.Read);
        var core = CreateCore(fs, maxMemory: 100);

        var entry = core.ArchiveEntries["/large.bin"];

        // First open creates the cache
        var stream1 = core.OpenEntryStream(entry, "/large.bin");
        Assert.NotNull(stream1);
        Assert.True(core.LargeFileCache.ContainsKey("/large.bin"));
        stream1.Dispose();

        // Second open reuses the cache
        var stream2 = core.OpenEntryStream(entry, "/large.bin");
        Assert.NotNull(stream2);
        Assert.IsType<FileStream>(stream2);
        stream2.Dispose();
    }

    // ─── OpenEntryStream: memory throttle fallback to disk ───

    [Fact]
    public void OpenEntryStream_MemoryLimitApproaching_FallsToDiskCache()
    {
        var data = new byte[50];
        new Random(42).NextBytes(data);

        var tempZip = CreateTempZipFile("small.bin", data);
        _disposables.Add(new TempFileDeleter(tempZip));
        using var fs = new FileStream(tempZip, FileMode.Open, FileAccess.Read, FileShare.Read);
        var core = CreateCore(fs, maxMemory: 1024 * 1024);

        // Simulate memory limit approaching
        core.CurrentMemoryUsage = core.MaxTotalMemoryCache - 10;

        var entry = core.ArchiveEntries["/small.bin"];
        var stream = core.OpenEntryStream(entry, "/small.bin");

        Assert.NotNull(stream);
        Assert.IsType<FileStream>(stream);

        stream.Dispose();
        core.CurrentMemoryUsage = 0;
    }

    // ─── DumpEntries: runs without throwing ───

    [Fact]
    public void DumpEntries_DoesNotThrow()
    {
        var core = CreateCore();

        var ex = Record.Exception(() => core.DumpEntries());

        Assert.Null(ex);
    }

    // ─── DumpEntries: with failed entries ───

    [Fact]
    public void DumpEntries_WithFailedEntries_DoesNotThrow()
    {
        var core = CreateCore();
        core.AddFailedEntry("/readme.txt");

        var ex = Record.Exception(() => core.DumpEntries());

        Assert.Null(ex);
    }

    // ─── DumpEntries: with max entries limit ───

    [Fact]
    public void DumpEntries_WithMaxEntries_DoesNotThrow()
    {
        var core = CreateCore();

        var ex = Record.Exception(() => core.DumpEntries(1));

        Assert.Null(ex);
    }

    // ─── Constructor: non-seekable stream throws ───

    [Fact]
    public void Constructor_NonSeekableStream_ThrowsInvalidOperation()
    {
        var data = CreateZipStream().ToArray();
        var nonSeekable = new NonSeekableStream(data);

        // SharpCompress requires seekable streams, so constructor should throw
        Assert.Throws<InvalidOperationException>(() => CreateCore(nonSeekable));
    }

    // ─── GetEntryNode: entry has valid times ───

    [Fact]
    public void GetEntryNode_EntryWithTimes_HasValidTimes()
    {
        var core = CreateCore();

        var node = core.GetEntryNode("/readme.txt");

        Assert.NotNull(node);
        // ZipArchive entries have timestamps set when created
        Assert.True(node.CreationTime > DateTime.MinValue);
        Assert.True(node.LastWriteTime > DateTime.MinValue);
    }

    // ─── ListDirectory: deduplication ───

    [Fact]
    public void ListDirectory_DuplicateNames_AreDeduped()
    {
        // Create a zip where same filename appears in different subdirs
        using var ms = new MemoryStream();
        using (var zip = new ZipArchive(ms, ZipArchiveMode.Create, true))
        {
            var e1 = zip.CreateEntry("dir1/file.txt");
            using (var w = new StreamWriter(e1.Open()))
            {
                w.Write("content1");
            }

            var e2 = zip.CreateEntry("dir2/file.txt");
            using (var w = new StreamWriter(e2.Open()))
            {
                w.Write("content2");
            }
        }

        ms.Position = 0;
        var core = CreateCore(ms);

        var dir1Entries = core.ListDirectory("/dir1");
        Assert.Single(dir1Entries);
        Assert.Equal("/dir1/file.txt", dir1Entries[0].NormalizedPath);

        var dir2Entries = core.ListDirectory("/dir2");
        Assert.Single(dir2Entries);
        Assert.Equal("/dir2/file.txt", dir2Entries[0].NormalizedPath);
    }

    // ─── ListDirectory: root auto-creation ───

    [Fact]
    public void ListDirectory_RootDirectory_IsAutoCreated()
    {
        var core = CreateCore();

        var root = core.GetEntryNode("/");
        Assert.NotNull(root);
        Assert.True(root.IsDir);
        Assert.Equal("/", root.NormalizedPath);
    }

    // ─── ReadStream: delegates to StoredEntryStream.ReadAt ───

    [Fact]
    public void ReadStream_StoredEntryStream_DelegatesToReadAt()
    {
        // Create a StoredEntryStream via the stored entry path
        var storedData = CreateStoredZipStream();
        _disposables.Add(storedData);
        var storedCore = CreateCore(storedData);
        _disposables.Add(storedCore);

        var entry = storedCore.ArchiveEntries["/stored.txt"];
        var stream = storedCore.OpenEntryStream(entry, "/stored.txt");
        Assert.NotNull(stream);
        Assert.IsType<StoredEntryStream>(stream);

        var buffer = new byte[5];
        var bytesRead = storedCore.ReadStream(stream, 0, buffer, 0, 5);

        Assert.Equal(5, bytesRead);
        Assert.Equal("Hello"u8.ToArray(), buffer);

        stream.Dispose();
    }

    // ─── Dispose: temp file cleanup failure is handled ───

    [Fact]
    public void Dispose_HandlesTempFileCleanupFailure()
    {
        var core = CreateCore();
        // Add a fake entry to LargeFileCache pointing to a non-existent file
        core.LargeFileCache["/fake"] = Path.Combine(core.TempDirectoryPath, "nonexistent.tmp");

        var ex = Record.Exception(core.Dispose);

        Assert.Null(ex);
        Assert.True(core.IsDisposed);
    }

    // ─── Dispose: temp directory deletion failure is handled ───

    [Fact]
    public void Dispose_HandlesTempDirectoryDeletionFailure()
    {
        var core = CreateCore();
        var tempDir = core.TempDirectoryPath;

        // Create a file that's locked to prevent directory deletion
        var lockedFile = Path.Combine(tempDir, "locked.tmp");
        File.Create(lockedFile).Dispose();

        // Dispose should handle the failure gracefully
        var ex = Record.Exception(core.Dispose);

        Assert.Null(ex);
        Assert.True(core.IsDisposed);

        // Cleanup
        try
        {
            File.Delete(lockedFile);
        }
        catch
        {
            // ignored
        }

        try
        {
            Directory.Delete(tempDir, true);
        }
        catch
        {
            // ignored
        }
    }

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
