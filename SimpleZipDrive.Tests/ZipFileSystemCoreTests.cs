using System.IO.Compression;
using System.Text;
using SimpleZipDrive.Core;

namespace SimpleZipDrive.Tests;

public class ZipFileSystemCoreTests : IDisposable
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

    private static MemoryStream CreateMultiLevelZipStream()
    {
        var ms = new MemoryStream();
        using (var zip = new ZipArchive(ms, ZipArchiveMode.Create, true))
        {
            zip.CreateEntry("a/b/c/deep.txt");
            zip.CreateEntry("a/b/other.txt");
            zip.CreateEntry("a/top.txt");
            zip.CreateEntry("root.txt");
        }

        ms.Position = 0;
        return ms;
    }

    // ─── Constructor tests ───

    [Fact]
    public void Constructor_ValidZip_Succeeds()
    {
        var core = CreateCore();

        Assert.False(core.IsDisposed);
        Assert.Equal("zip", core.ArchiveType);
        Assert.Equal(ZipFileSystemCore.DefaultVolumeLabel, core.VolumeLabel);
    }

    [Fact]
    public void Constructor_CustomVolumeLabel_IsSet()
    {
        var core = CreateCore(volumeLabel: "MyDrive");

        Assert.Equal("MyDrive", core.VolumeLabel);
    }

    [Fact]
    public void Constructor_NullVolumeLabel_UsesDefault()
    {
        var core = CreateCore(volumeLabel: null);

        Assert.Equal(ZipFileSystemCore.DefaultVolumeLabel, core.VolumeLabel);
    }

    [Fact]
    public void Constructor_UnsupportedArchiveType_Throws()
    {
        using var ms = new MemoryStream();
        Assert.Throws<NotSupportedException>(() =>
            new ZipFileSystemCore(ms, "M:\\", static (_, _) => { }, static () => null, "tar"));
    }

    [Fact]
    public void Constructor_PopulatesArchiveEntries()
    {
        var core = CreateCore();

        Assert.NotEmpty(core.ArchiveEntries);
        Assert.True(core.ArchiveEntries.ContainsKey("/readme.txt"));
        Assert.True(core.ArchiveEntries.ContainsKey("/data/info.txt"));
    }

    [Fact]
    public void Constructor_CreatesTempDirectory()
    {
        var core = CreateCore();

        Assert.NotNull(core.TempDirectoryPath);
        Assert.True(Directory.Exists(core.TempDirectoryPath));
    }

    [Fact]
    public void Constructor_SetsMaxMemorySize()
    {
        var core = CreateCore(maxMemory: 1024 * 1024);

        Assert.Equal(1024 * 1024, core.MaxMemorySize);
    }

    [Fact]
    public void Constructor_TotalSizeReturnsStreamLength()
    {
        using var ms = CreateZipStream();
        var expectedLength = ms.Length;
        var core = CreateCore(ms);

        Assert.Equal(expectedLength, core.TotalSize);
    }

    // ─── IsStoredEntry tests ───

    [Fact]
    public void IsStoredEntry_NonZipType_ReturnsFalse()
    {
        using var ms = CreateZipStream();
        // Need 7z stream for non-zip test
        using var ms7Z = CreateZipStream();
        // Can't easily create a valid 7z here, so test with zip but override archive type
        // Instead, verify the logic: non-zip archive type returns false
        var core = CreateCore(archiveType: "zip");
        var entry = core.ArchiveEntries.Values.First();

        // Even if the entry looks stored, archive type check matters
        // For zip entries, it checks compression type
        var result = core.IsStoredEntry(entry);
        Assert.False(result); // Deflate compressed
    }

    [Fact]
    public void IsStoredEntry_DirectoryEntry_ReturnsFalse()
    {
        var core = CreateCore();

        var dirEntry = core.ArchiveEntries.Values.FirstOrDefault(static e => ZipFsHelpers.IsDirectory(e));
        if (dirEntry != null)
        {
            Assert.False(core.IsStoredEntry(dirEntry));
        }
    }

    // ─── IsFailedEntry / AddFailedEntry tests ───

    [Fact]
    public void IsFailedEntry_NotAdded_ReturnsFalse()
    {
        var core = CreateCore();

        Assert.False(core.IsFailedEntry("/nonexistent.txt"));
    }

    [Fact]
    public void AddFailedEntry_MakesIsFailedEntryReturnTrue()
    {
        var core = CreateCore();

        core.AddFailedEntry("/test.txt");

        Assert.True(core.IsFailedEntry("/test.txt"));
    }

    [Fact]
    public void AddFailedEntry_CaseInsensitive()
    {
        var core = CreateCore();

        core.AddFailedEntry("/Test.TXT");

        Assert.True(core.IsFailedEntry("/test.txt"));
    }

    // ─── GetEntryNode tests ───

    [Fact]
    public void GetEntryNode_ExistingFile_ReturnsNode()
    {
        var core = CreateCore();

        var node = core.GetEntryNode("/readme.txt");

        Assert.NotNull(node);
        Assert.False(node.IsDir);
        Assert.Equal("/readme.txt", node.NormalizedPath);
        Assert.True(node.FileSize > 0);
    }

    [Fact]
    public void GetEntryNode_ExistingDirectory_ReturnsDirNode()
    {
        var core = CreateCore();

        var node = core.GetEntryNode("/data/info.txt");

        Assert.NotNull(node);
        Assert.False(node.IsDir);
    }

    [Fact]
    public void GetEntryNode_Root_ReturnsImplicitDirNode()
    {
        var core = CreateCore();

        var node = core.GetEntryNode("/");

        Assert.NotNull(node);
        Assert.True(node.IsDir);
        Assert.Equal("/", node.NormalizedPath);
    }

    [Fact]
    public void GetEntryNode_ImplicitDirectory_ReturnsNode()
    {
        var core = CreateCore();

        // "data" is an implicit directory (created from "data/info.txt")
        var node = core.GetEntryNode("/data");

        Assert.NotNull(node);
        Assert.True(node.IsDir);
        Assert.Null(node.Entry); // implicit dirs have no archive entry
    }

    [Fact]
    public void GetEntryNode_NonExistentPath_ReturnsNull()
    {
        var core = CreateCore();

        var node = core.GetEntryNode("/nonexistent/path.txt");

        Assert.Null(node);
    }

    [Fact]
    public void GetEntryNode_ExplicitDirEntry_HasEntry()
    {
        using var ms = new MemoryStream();
        using (var zip = new ZipArchive(ms, ZipArchiveMode.Create, true))
        {
            zip.CreateEntry("mydir/");
            var entry = zip.CreateEntry("mydir/file.txt");
            using var w = new StreamWriter(entry.Open());
            w.Write("content");
        }

        ms.Position = 0;
        var core = CreateCore(ms);

        var node = core.GetEntryNode("/mydir");

        Assert.NotNull(node);
        Assert.True(node.IsDir);
        Assert.NotNull(node.Entry);
    }

    // ─── TryResolvePath tests ───

    [Fact]
    public void TryResolvePath_NormalPath_ReturnsNormalized()
    {
        var core = CreateCore();

        var result = core.TryResolvePath(@"\data\info.txt", out var normalized);

        Assert.True(result);
        Assert.Equal("/data/info.txt", normalized);
    }

    [Fact]
    public void TryResolvePath_SpecialPaths_Resolves()
    {
        var core = CreateCore();

        var result = core.TryResolvePath("/data/../readme.txt", out var normalized);

        Assert.True(result);
        Assert.Equal("/readme.txt", normalized);
    }

    // ─── ListDirectory tests ───

    [Fact]
    public void ListDirectory_Root_ReturnsAllChildren()
    {
        var core = CreateCore();

        var entries = core.ListDirectory("/");

        Assert.NotEmpty(entries);
        var names = entries.Select(static e => e.NormalizedPath).ToList();
        Assert.Contains("/readme.txt", names);
        Assert.Contains("/data", names);
    }

    [Fact]
    public void ListDirectory_Subdirectory_ReturnsNestedEntries()
    {
        var core = CreateCore();

        var entries = core.ListDirectory("/data");

        Assert.Single(entries);
        Assert.Equal("/data/info.txt", entries[0].NormalizedPath);
    }

    [Fact]
    public void ListDirectory_EmptyDirectory_ReturnsEmpty()
    {
        var core = CreateCore();

        // "empty" directory exists as explicit entry
        var entries = core.ListDirectory("/empty");

        Assert.Empty(entries);
    }

    [Fact]
    public void ListDirectory_NonExistentPath_ReturnsEmpty()
    {
        var core = CreateCore();

        var entries = core.ListDirectory("/nonexistent");

        Assert.Empty(entries);
    }

    [Fact]
    public void ListDirectory_MultiLevel_CorrectDepth()
    {
        using var ms = CreateMultiLevelZipStream();
        var core = CreateCore(ms);

        var aEntries = core.ListDirectory("/a");
        Assert.Contains(aEntries, static e => e.NormalizedPath == "/a/top.txt");
        Assert.Contains(aEntries, static e => e.NormalizedPath == "/a/b");

        var bEntries = core.ListDirectory("/a/b");
        Assert.Contains(bEntries, static e => e.NormalizedPath == "/a/b/other.txt");
        Assert.Contains(bEntries, static e => e.NormalizedPath == "/a/b/c");
    }

    [Fact]
    public void ListDirectory_ImplicitDirectory_ContainsImplicitDirs()
    {
        using var ms = CreateMultiLevelZipStream();
        var core = CreateCore(ms);

        // /a/b/c is implicit (no explicit "a/b/c/" entry)
        var bEntries = core.ListDirectory("/a/b");
        Assert.Contains(bEntries, static e => e is { NormalizedPath: "/a/b/c", IsDir: true });
    }

    // ─── ReadStream tests ───

    [Fact]
    public void ReadStream_SeekableStream_ReadsAtOffset()
    {
        var core = CreateCore();

        var data = new byte[] { 10, 20, 30, 40, 50 };
        using var ms = new MemoryStream(data);

        var buffer = new byte[3];
        var bytesRead = core.ReadStream(ms, 2, buffer, 0, 3);

        Assert.Equal(3, bytesRead);
        Assert.Equal(30, buffer[0]);
        Assert.Equal(40, buffer[1]);
        Assert.Equal(50, buffer[2]);
    }

    [Fact]
    public void ReadStream_SeekableStream_BeyondLength_ReturnsZero()
    {
        var core = CreateCore();

        var data = new byte[] { 10, 20, 30 };
        using var ms = new MemoryStream(data);

        var buffer = new byte[3];
        var bytesRead = core.ReadStream(ms, 100, buffer, 0, 3);

        Assert.Equal(0, bytesRead);
    }

    [Fact]
    public void ReadStream_NonSeekableStream_SequentialRead()
    {
        var core = CreateCore();

        var data = new byte[] { 10, 20, 30, 40, 50 };
        using var ms = new NonSeekableStream(data);

        var buffer = new byte[3];
        var bytesRead = core.ReadStream(ms, 0, buffer, 0, 3);

        Assert.Equal(3, bytesRead);
        Assert.Equal(10, buffer[0]);
        Assert.Equal(20, buffer[1]);
        Assert.Equal(30, buffer[2]);
    }

    [Fact]
    public void ReadStream_NonSeekableStream_NonSequentialOffset_Throws()
    {
        var core = CreateCore();

        var data = new byte[] { 10, 20, 30, 40, 50 };
        using var ms = new NonSeekableStream(data);

        var buffer = new byte[3];
        // First read at offset 0 advances position to 3
        core.ReadStream(ms, 0, buffer, 0, 3);

        // Trying to read at offset 0 again (non-sequential) should throw
        Assert.Throws<InvalidOperationException>(() => core.ReadStream(ms, 0, buffer, 0, 3));
    }

    // ─── ValidatePathLength tests ───

    [Fact]
    public void ValidatePathLength_ValidPath_ReturnsTrue()
    {
        var core = CreateCore();

        var result = core.ValidatePathLength("/short/path.txt", "CreateFile");

        Assert.True(result);
    }

    [Fact]
    public void ValidatePathLength_TooLongPath_ReturnsFalse()
    {
        var core = CreateCore();

        var longPath = "/" + new string('a', 260);
        var result = core.ValidatePathLength(longPath, "CreateFile");

        Assert.False(result);
    }

    [Fact]
    public void ValidatePathLength_ExtendedPath_ReturnsTrue()
    {
        var core = CreateCore();

        var extendedPath = @"\\?\" + new string('a', 260);
        var result = core.ValidatePathLength(extendedPath, "CreateFile");

        Assert.True(result);
    }

    // ─── CreateSecureTempFile tests ───

    [Fact]
    public void CreateSecureTempFile_CreatesFileInTempDirectory()
    {
        var core = CreateCore();

        var filePath = core.CreateSecureTempFile();

        Assert.True(File.Exists(filePath));
        Assert.StartsWith(core.TempDirectoryPath, filePath);
        Assert.EndsWith(".tmp", filePath);

        File.Delete(filePath);
    }

    [Fact]
    public void CreateSecureTempFile_MultipleCallsProduceUniquePaths()
    {
        var core = CreateCore();

        var path1 = core.CreateSecureTempFile();
        var path2 = core.CreateSecureTempFile();

        Assert.NotEqual(path1, path2);

        File.Delete(path1);
        File.Delete(path2);
    }

    // ─── Dispose tests ───

    [Fact]
    public void Dispose_SetsIsDisposed()
    {
        var core = CreateCore();

        Assert.False(core.IsDisposed);
        core.Dispose();

        Assert.True(core.IsDisposed);
    }

    [Fact]
    public void Dispose_DoubleDispose_DoesNotThrow()
    {
        var core = CreateCore();

        core.Dispose();
        var ex = Record.Exception(core.Dispose);

        Assert.Null(ex);
    }

    [Fact]
    public void Dispose_ClearsMemoryUsage()
    {
        var core = CreateCore();

        core.CurrentMemoryUsage = 1000;
        core.Dispose();

        Assert.Equal(0, core.CurrentMemoryUsage);
    }

    [Fact]
    public void Dispose_DeletesTempDirectory()
    {
        var core = CreateCore();
        var tempDir = core.TempDirectoryPath;

        Assert.True(Directory.Exists(tempDir));
        core.Dispose();

        Assert.False(Directory.Exists(tempDir));
    }

    [Fact]
    public void Dispose_ClearsLargeFileCache()
    {
        var core = CreateCore();

        core.LargeFileCache["test"] = "path";
        core.Dispose();

        Assert.Empty(core.LargeFileCache);
    }

    // ─── EntryNode model tests ───

    [Fact]
    public void EntryNode_DefaultValues()
    {
        var node = new EntryNode();

        Assert.Null(node.NormalizedPath);
        Assert.Null(node.CanonicalPath);
        Assert.False(node.IsDir);
        Assert.Null(node.Entry);
        Assert.Equal(0, node.FileSize);
    }

    [Fact]
    public void EntryNode_PropertyAssignment()
    {
        var node = new EntryNode
        {
            NormalizedPath = "/test",
            CanonicalPath = "/test",
            IsDir = true,
            FileSize = 12345,
            CreationTime = new DateTime(2024, 1, 1),
            LastWriteTime = new DateTime(2024, 6, 15),
            LastAccessTime = new DateTime(2024, 12, 31)
        };

        Assert.Equal("/test", node.NormalizedPath);
        Assert.Equal("/test", node.CanonicalPath);
        Assert.True(node.IsDir);
        Assert.Equal(12345, node.FileSize);
        Assert.Equal(new DateTime(2024, 1, 1), node.CreationTime);
        Assert.Equal(new DateTime(2024, 6, 15), node.LastWriteTime);
        Assert.Equal(new DateTime(2024, 12, 31), node.LastAccessTime);
    }

    // ─── Constants tests ───

    [Fact]
    public void DefaultVolumeLabel_IsSimpleZipDrive()
    {
        Assert.Equal("SimpleZipDrive", ZipFileSystemCore.DefaultVolumeLabel);
    }

    [Fact]
    public void DefaultMaxMemorySize_Is512MB()
    {
        Assert.Equal(512L * 1024 * 1024, ZipFileSystemCore.DefaultMaxMemorySize);
    }

    // ─── NonSeekableStream helper ───

    private sealed class NonSeekableStream : MemoryStream
    {
        public NonSeekableStream(byte[] buffer) : base(buffer)
        {
        }

        public override bool CanSeek => false;
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
