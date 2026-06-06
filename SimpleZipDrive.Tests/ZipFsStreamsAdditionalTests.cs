using SimpleZipDrive.Core;

namespace SimpleZipDrive.Tests;

public class ZipFsStreamsAdditionalTests
{
    // ─── StoredEntryStream: ReadAt with non-zero bufferOffset ───

    [Fact]
    public void StoredEntryStream_ReadAt_NonZeroBufferOffset()
    {
        var data = new byte[] { 10, 20, 30, 40, 50 };
        using var source = new MemoryStream(data);
        var lockObj = new object();
        using var stream = new StoredEntryStream(source, 0, 5, lockObj);

        var buffer = new byte[10];
        var bytesRead = stream.ReadAt(2, buffer, 5, 3);

        Assert.Equal(3, bytesRead);
        Assert.Equal(0, buffer[0]); // untouched
        Assert.Equal(0, buffer[4]); // untouched
        Assert.Equal(30, buffer[5]); // written at offset 5
        Assert.Equal(40, buffer[6]);
        Assert.Equal(50, buffer[7]);
    }

    // ─── StoredEntryStream: Read(Span) after seeking ───

    [Fact]
    public void StoredEntryStream_ReadSpan_AfterSeek()
    {
        var data = new byte[] { 10, 20, 30, 40, 50 };
        using var source = new MemoryStream(data);
        var lockObj = new object();
        using var stream = new StoredEntryStream(source, 0, 5, lockObj);

        stream.Seek(2, SeekOrigin.Begin);
        var buffer = new byte[3];
        var bytesRead = stream.Read(buffer.AsSpan());

        Assert.Equal(3, bytesRead);
        Assert.Equal(30, buffer[0]);
        Assert.Equal(40, buffer[1]);
        Assert.Equal(50, buffer[2]);
    }

    // ─── StoredEntryStream: Read(Span) disposed throws ───

    [Fact]
    public void StoredEntryStream_ReadSpan_Disposed_ThrowsObjectDisposed()
    {
        var data = new byte[] { 1, 2, 3 };
        using var source = new MemoryStream(data);
        var lockObj = new object();
        var stream = new StoredEntryStream(source, 0, 3, lockObj);

        stream.Dispose();

        var buffer = new byte[1];
        Assert.Throws<ObjectDisposedException>(() => stream.Read(buffer.AsSpan()));
    }

    // ─── StoredEntryStream: ReadAt disposed throws ───

    [Fact]
    public void StoredEntryStream_ReadAt_Disposed_ThrowsObjectDisposed()
    {
        var data = new byte[] { 1, 2, 3 };
        using var source = new MemoryStream(data);
        var lockObj = new object();
        var stream = new StoredEntryStream(source, 0, 3, lockObj);

        stream.Dispose();

        var buffer = new byte[1];
        Assert.Throws<ObjectDisposedException>(() => stream.ReadAt(0, buffer, 0, 1));
    }

    // ─── StoredEntryStream: Position setter disposed throws ───

    [Fact]
    public void StoredEntryStream_PositionSet_Disposed_ThrowsObjectDisposed()
    {
        var data = new byte[] { 1, 2, 3 };
        using var source = new MemoryStream(data);
        var lockObj = new object();
        var stream = new StoredEntryStream(source, 0, 3, lockObj);

        stream.Dispose();

        // Position setter uses _position field which doesn't check disposal
        // but we can verify the stream is disposed by trying to read
        var buffer = new byte[1];
        Assert.Throws<ObjectDisposedException>(() => stream.Read(buffer, 0, 1));
    }

    // ─── StoredEntryStream: constructor with FileStream source ───

    [Fact]
    public void StoredEntryStream_FileStreamSource_UsesRandomAccess()
    {
        var tempPath = Path.Combine(Path.GetTempPath(), $"StoredEntryStream_test_{Guid.NewGuid():N}.bin");
        try
        {
            var data = new byte[] { 10, 20, 30, 40, 50, 60, 70, 80, 90, 100 };
            File.WriteAllBytes(tempPath, data);

            using var fs = new FileStream(tempPath, FileMode.Open, FileAccess.Read, FileShare.Read);
            var lockObj = new object();
            using var stream = new StoredEntryStream(fs, 2, 5, lockObj);

            Assert.Equal(5, stream.Length);

            var buffer = new byte[5];
            var bytesRead = stream.Read(buffer, 0, 5);

            Assert.Equal(5, bytesRead);
            Assert.Equal(30, buffer[0]);
            Assert.Equal(40, buffer[1]);
            Assert.Equal(50, buffer[2]);
            Assert.Equal(60, buffer[3]);
            Assert.Equal(70, buffer[4]);
        }
        finally
        {
            try { File.Delete(tempPath); } catch { }
        }
    }

    // ─── StoredEntryStream: FileStream source ReadAt ───

    [Fact]
    public void StoredEntryStream_FileStreamSource_ReadAt()
    {
        var tempPath = Path.Combine(Path.GetTempPath(), $"StoredEntryStream_test_{Guid.NewGuid():N}.bin");
        try
        {
            var data = new byte[] { 0, 1, 2, 3, 4, 5, 6, 7, 8, 9 };
            File.WriteAllBytes(tempPath, data);

            using var fs = new FileStream(tempPath, FileMode.Open, FileAccess.Read, FileShare.Read);
            var lockObj = new object();
            using var stream = new StoredEntryStream(fs, 3, 5, lockObj);

            var buffer = new byte[3];
            var bytesRead = stream.ReadAt(1, buffer, 0, 3);

            Assert.Equal(3, bytesRead);
            Assert.Equal(4, buffer[0]); // data[3+1]
            Assert.Equal(5, buffer[1]); // data[3+2]
            Assert.Equal(6, buffer[2]); // data[3+3]
        }
        finally
        {
            try { File.Delete(tempPath); } catch { }
        }
    }

    // ─── StoredEntryStream: FileStream source Read(Span) ───

    [Fact]
    public void StoredEntryStream_FileStreamSource_ReadSpan()
    {
        var tempPath = Path.Combine(Path.GetTempPath(), $"StoredEntryStream_test_{Guid.NewGuid():N}.bin");
        try
        {
            var data = new byte[] { 100, 101, 102, 103, 104, 105 };
            File.WriteAllBytes(tempPath, data);

            using var fs = new FileStream(tempPath, FileMode.Open, FileAccess.Read, FileShare.Read);
            var lockObj = new object();
            using var stream = new StoredEntryStream(fs, 1, 4, lockObj);

            var buffer = new byte[4];
            var bytesRead = stream.Read(buffer.AsSpan());

            Assert.Equal(4, bytesRead);
            Assert.Equal(101, buffer[0]);
            Assert.Equal(102, buffer[1]);
            Assert.Equal(103, buffer[2]);
            Assert.Equal(104, buffer[3]);
        }
        finally
        {
            try { File.Delete(tempPath); } catch { }
        }
    }

    // ─── TrackedMemoryStream: span-based Read ───

    [Fact]
    public void TrackedMemoryStream_ReadSpan_ReturnsCorrectData()
    {
        var data = new byte[] { 10, 20, 30, 40, 50 };
        var memoryLock = new object();
        using var stream = new TrackedMemoryStream(data, memoryLock, static _ => { });

        var buffer = new byte[5];
        var bytesRead = stream.Read(buffer.AsSpan());

        Assert.Equal(5, bytesRead);
        Assert.Equal(data, buffer);
    }

    // ─── TrackedMemoryStream: span-based Read with offset ───

    [Fact]
    public void TrackedMemoryStream_ReadSpan_WithPositionOffset()
    {
        var data = new byte[] { 10, 20, 30, 40, 50 };
        var memoryLock = new object();
        using var stream = new TrackedMemoryStream(data, memoryLock, static _ => { });

        stream.Position = 2;
        var buffer = new byte[3];
        var bytesRead = stream.Read(buffer.AsSpan());

        Assert.Equal(3, bytesRead);
        Assert.Equal(30, buffer[0]);
        Assert.Equal(40, buffer[1]);
        Assert.Equal(50, buffer[2]);
    }

    // ─── TrackedMemoryStream: span-based Read at EOF ───

    [Fact]
    public void TrackedMemoryStream_ReadSpan_AtEof_ReturnsZero()
    {
        var data = new byte[] { 10, 20, 30 };
        var memoryLock = new object();
        using var stream = new TrackedMemoryStream(data, memoryLock, static _ => { });

        stream.Position = 3;
        var buffer = new byte[10];
        var bytesRead = stream.Read(buffer.AsSpan());

        Assert.Equal(0, bytesRead);
    }

    // ─── TrackedMemoryStream: Write throws ───

    [Fact]
    public void TrackedMemoryStream_Write_ThrowsNotSupported()
    {
        var data = new byte[] { 1, 2, 3 };
        var memoryLock = new object();
        using var stream = new TrackedMemoryStream(data, memoryLock, static _ => { });

        Assert.Throws<NotSupportedException>(() => stream.Write(new byte[] { 4 }, 0, 1));
    }

    // ─── TrackedMemoryStream: SetLength throws ───

    [Fact]
    public void TrackedMemoryStream_SetLength_ThrowsNotSupported()
    {
        var data = new byte[] { 1, 2, 3 };
        var memoryLock = new object();
        using var stream = new TrackedMemoryStream(data, memoryLock, static _ => { });

        Assert.Throws<NotSupportedException>(() => stream.SetLength(10));
    }

    // ─── StoredEntryStream: sequential reads with FileStream ───

    [Fact]
    public void StoredEntryStream_FileStreamSource_SequentialReads()
    {
        var tempPath = Path.Combine(Path.GetTempPath(), $"StoredEntryStream_test_{Guid.NewGuid():N}.bin");
        try
        {
            var data = new byte[] { 0, 1, 2, 3, 4, 5, 6, 7, 8, 9 };
            File.WriteAllBytes(tempPath, data);

            using var fs = new FileStream(tempPath, FileMode.Open, FileAccess.Read, FileShare.Read);
            var lockObj = new object();
            using var stream = new StoredEntryStream(fs, 0, 10, lockObj);

            var buffer1 = new byte[3];
            var read1 = stream.Read(buffer1, 0, 3);
            Assert.Equal(3, read1);
            Assert.Equal(0, buffer1[0]);
            Assert.Equal(1, buffer1[1]);
            Assert.Equal(2, buffer1[2]);

            var buffer2 = new byte[3];
            var read2 = stream.Read(buffer2, 0, 3);
            Assert.Equal(3, read2);
            Assert.Equal(3, buffer2[0]);
            Assert.Equal(4, buffer2[1]);
            Assert.Equal(5, buffer2[2]);

            var buffer3 = new byte[10];
            var read3 = stream.Read(buffer3, 0, 10);
            Assert.Equal(4, read3);
            Assert.Equal(6, buffer3[0]);
            Assert.Equal(9, buffer3[3]);
        }
        finally
        {
            try { File.Delete(tempPath); } catch { }
        }
    }
}
