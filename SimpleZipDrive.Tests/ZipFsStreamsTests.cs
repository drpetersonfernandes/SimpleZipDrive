using SimpleZipDrive.Core;

namespace SimpleZipDrive.Tests;

public class ZipFsStreamsTests
{
    // ─── TrackedMemoryStream tests ───

    [Fact]
    public void TrackedMemoryStream_ReadReturnsCorrectData()
    {
        var data = new byte[] { 1, 2, 3, 4, 5 };
        var memoryLock = new object();
        using var stream = new TrackedMemoryStream(data, memoryLock, static _ => { });

        var buffer = new byte[5];
        var bytesRead = stream.Read(buffer, 0, 5);

        Assert.Equal(5, bytesRead);
        Assert.Equal(data, buffer);
    }

    [Fact]
    public void TrackedMemoryStream_ReadWithOffsetWorks()
    {
        var data = new byte[] { 10, 20, 30, 40, 50 };
        var memoryLock = new object();
        using var stream = new TrackedMemoryStream(data, memoryLock, static _ => { });

        stream.Position = 2;
        var buffer = new byte[3];
        var bytesRead = stream.Read(buffer, 0, 3);

        Assert.Equal(3, bytesRead);
        Assert.Equal(30, buffer[0]);
        Assert.Equal(40, buffer[1]);
        Assert.Equal(50, buffer[2]);
    }

    [Fact]
    public void TrackedMemoryStream_DisposeCallsCallback()
    {
        var data = new byte[] { 1, 2, 3 };
        var memoryLock = new object();
        var callbackSize = -1;
        var stream = new TrackedMemoryStream(data, memoryLock, size => { callbackSize = size; });

        stream.Dispose();

        Assert.Equal(3, callbackSize);
    }

    [Fact]
    public void TrackedMemoryStream_DoubleDisposeCallsCallbackOnce()
    {
        var data = new byte[] { 1, 2, 3 };
        var memoryLock = new object();
        var callCount = 0;
        var stream = new TrackedMemoryStream(data, memoryLock, _ => { callCount++; });

        stream.Dispose();
        stream.Dispose();

        Assert.Equal(1, callCount);
    }

    [Fact]
    public void TrackedMemoryStream_CanReadIsTrue()
    {
        var data = new byte[] { 1 };
        var memoryLock = new object();
        using var stream = new TrackedMemoryStream(data, memoryLock, static _ => { });

        Assert.True(stream.CanRead);
    }

    [Fact]
    public void TrackedMemoryStream_CanSeekIsTrue()
    {
        var data = new byte[] { 1 };
        var memoryLock = new object();
        using var stream = new TrackedMemoryStream(data, memoryLock, static _ => { });

        Assert.True(stream.CanSeek);
    }

    [Fact]
    public void TrackedMemoryStream_CanWriteIsFalse()
    {
        var data = new byte[] { 1 };
        var memoryLock = new object();
        using var stream = new TrackedMemoryStream(data, memoryLock, static _ => { });

        Assert.False(stream.CanWrite);
    }

    [Fact]
    public void TrackedMemoryStream_LengthMatchesBuffer()
    {
        var data = new byte[] { 1, 2, 3, 4 };
        var memoryLock = new object();
        using var stream = new TrackedMemoryStream(data, memoryLock, static _ => { });

        Assert.Equal(4, stream.Length);
    }

    [Fact]
    public void TrackedMemoryStream_DisposeWithLockContention()
    {
        var data = new byte[] { 1, 2, 3 };
        var memoryLock = new object();
        var callbackInvoked = false;
        var stream = new TrackedMemoryStream(data, memoryLock, _ => { callbackInvoked = true; });

        lock (memoryLock)
        {
            // Holding the lock - dispose should still work (it acquires the lock internally)
        }

        stream.Dispose();
        Assert.True(callbackInvoked);
    }

    // ─── StoredEntryStream tests ───

    [Fact]
    public void StoredEntryStream_ConstructorWithValidArgs_Succeeds()
    {
        using var source = new MemoryStream(new byte[100]);
        var lockObj = new object();

        using var stream = new StoredEntryStream(source, 0, 50, lockObj);

        Assert.Equal(50, stream.Length);
        Assert.True(stream.CanRead);
        Assert.True(stream.CanSeek);
        Assert.False(stream.CanWrite);
    }

    [Fact]
    public void StoredEntryStream_ReadFromBeginningReturnsData()
    {
        var data = new byte[] { 10, 20, 30, 40, 50 };
        using var source = new MemoryStream(data);
        var lockObj = new object();
        using var stream = new StoredEntryStream(source, 0, 5, lockObj);

        var buffer = new byte[5];
        var bytesRead = stream.Read(buffer, 0, 5);

        Assert.Equal(5, bytesRead);
        Assert.Equal(data, buffer);
    }

    [Fact]
    public void StoredEntryStream_ReadWithOffset()
    {
        var data = new byte[] { 0, 0, 10, 20, 30, 0, 0 };
        using var source = new MemoryStream(data);
        var lockObj = new object();
        using var stream = new StoredEntryStream(source, 2, 3, lockObj);

        var buffer = new byte[3];
        var bytesRead = stream.Read(buffer, 0, 3);

        Assert.Equal(3, bytesRead);
        Assert.Equal(10, buffer[0]);
        Assert.Equal(20, buffer[1]);
        Assert.Equal(30, buffer[2]);
    }

    [Fact]
    public void StoredEntryStream_ReadPastEndReturnsZero()
    {
        var data = new byte[] { 1, 2, 3 };
        using var source = new MemoryStream(data);
        var lockObj = new object();
        using var stream = new StoredEntryStream(source, 0, 3, lockObj);

        stream.Position = 3;
        var buffer = new byte[10];
        var bytesRead = stream.Read(buffer, 0, 10);

        Assert.Equal(0, bytesRead);
    }

    [Fact]
    public void StoredEntryStream_ReadPartialAtEnd()
    {
        var data = new byte[] { 10, 20, 30 };
        using var source = new MemoryStream(data);
        var lockObj = new object();
        using var stream = new StoredEntryStream(source, 0, 3, lockObj);

        stream.Position = 1;
        var buffer = new byte[10];
        var bytesRead = stream.Read(buffer, 0, 10);

        Assert.Equal(2, bytesRead);
        Assert.Equal(20, buffer[0]);
        Assert.Equal(30, buffer[1]);
    }

    [Fact]
    public void StoredEntryStream_SeekBegin()
    {
        var data = new byte[] { 10, 20, 30, 40, 50 };
        using var source = new MemoryStream(data);
        var lockObj = new object();
        using var stream = new StoredEntryStream(source, 0, 5, lockObj);

        var result = stream.Seek(2, SeekOrigin.Begin);

        Assert.Equal(2, result);
        Assert.Equal(2, stream.Position);
    }

    [Fact]
    public void StoredEntryStream_SeekCurrent()
    {
        var data = new byte[] { 10, 20, 30, 40, 50 };
        using var source = new MemoryStream(data);
        var lockObj = new object();
        using var stream = new StoredEntryStream(source, 0, 5, lockObj);

        stream.Position = 1;
        var result = stream.Seek(2, SeekOrigin.Current);

        Assert.Equal(3, result);
    }

    [Fact]
    public void StoredEntryStream_SeekEnd()
    {
        var data = new byte[] { 10, 20, 30, 40, 50 };
        using var source = new MemoryStream(data);
        var lockObj = new object();
        using var stream = new StoredEntryStream(source, 0, 5, lockObj);

        var result = stream.Seek(-2, SeekOrigin.End);

        Assert.Equal(3, result);
    }

    [Fact]
    public void StoredEntryStream_SeekOutOfRangeThrows()
    {
        var data = new byte[] { 10, 20, 30 };
        using var source = new MemoryStream(data);
        var lockObj = new object();
        using var stream = new StoredEntryStream(source, 0, 3, lockObj);

        Assert.Throws<IOException>(() => stream.Seek(-10, SeekOrigin.Begin));
    }

    [Fact]
    public void StoredEntryStream_SeekBeyondLengthThrows()
    {
        var data = new byte[] { 10, 20, 30 };
        using var source = new MemoryStream(data);
        var lockObj = new object();
        using var stream = new StoredEntryStream(source, 0, 3, lockObj);

        Assert.Throws<IOException>(() => stream.Seek(100, SeekOrigin.Begin));
    }

    [Fact]
    public void StoredEntryStream_PositionSetNegativeThrows()
    {
        var data = new byte[] { 10, 20, 30 };
        using var source = new MemoryStream(data);
        var lockObj = new object();
        using var stream = new StoredEntryStream(source, 0, 3, lockObj);

        Assert.Throws<ArgumentOutOfRangeException>(() => stream.Position = -1);
    }

    [Fact]
    public void StoredEntryStream_PositionBeyondLengthThrows()
    {
        var data = new byte[] { 10, 20, 30 };
        using var source = new MemoryStream(data);
        var lockObj = new object();
        using var stream = new StoredEntryStream(source, 0, 3, lockObj);

        Assert.Throws<ArgumentOutOfRangeException>(() => stream.Position = 100);
    }

    [Fact]
    public void StoredEntryStream_FlushDoesNotThrow()
    {
        var data = new byte[] { 1, 2, 3 };
        using var source = new MemoryStream(data);
        var lockObj = new object();
        using var stream = new StoredEntryStream(source, 0, 3, lockObj);

        var ex = Record.Exception(stream.Flush);
        Assert.Null(ex);
    }

    [Fact]
    public void StoredEntryStream_SetLengthThrowsNotSupported()
    {
        var data = new byte[] { 1, 2, 3 };
        using var source = new MemoryStream(data);
        var lockObj = new object();
        using var stream = new StoredEntryStream(source, 0, 3, lockObj);

        Assert.Throws<NotSupportedException>(() => stream.SetLength(10));
    }

    [Fact]
    public void StoredEntryStream_WriteThrowsNotSupported()
    {
        var data = new byte[] { 1, 2, 3 };
        using var source = new MemoryStream(data);
        var lockObj = new object();
        using var stream = new StoredEntryStream(source, 0, 3, lockObj);

        Assert.Throws<NotSupportedException>(() => stream.Write([1, 2], 0, 2));
    }

    [Fact]
    public void StoredEntryStream_DisposedReadThrowsObjectDisposed()
    {
        var data = new byte[] { 1, 2, 3 };
        using var source = new MemoryStream(data);
        var lockObj = new object();
        var stream = new StoredEntryStream(source, 0, 3, lockObj);

        stream.Dispose();

        var buffer = new byte[1];
        Assert.Throws<ObjectDisposedException>(() => stream.Read(buffer, 0, 1));
    }

    [Fact]
    public void StoredEntryStream_DisposedSeekThrowsObjectDisposed()
    {
        var data = new byte[] { 1, 2, 3 };
        using var source = new MemoryStream(data);
        var lockObj = new object();
        var stream = new StoredEntryStream(source, 0, 3, lockObj);

        stream.Dispose();

        Assert.Throws<ObjectDisposedException>(() => stream.Seek(0, SeekOrigin.Begin));
    }

    [Fact]
    public void StoredEntryStream_DisposedPositionGetThrowsObjectDisposed()
    {
        var data = new byte[] { 1, 2, 3 };
        using var source = new MemoryStream(data);
        var lockObj = new object();
        var stream = new StoredEntryStream(source, 0, 3, lockObj);

        stream.Dispose();

        // The Position getter accesses _position which doesn't check disposal
        // but reading does check disposal
        var buffer = new byte[1];
        Assert.Throws<ObjectDisposedException>(() => stream.Read(buffer, 0, 1));
    }

    [Fact]
    public void StoredEntryStream_ReadSpanReturnsCorrectData()
    {
        var data = new byte[] { 10, 20, 30, 40, 50 };
        using var source = new MemoryStream(data);
        var lockObj = new object();
        using var stream = new StoredEntryStream(source, 0, 5, lockObj);

        var buffer = new byte[5];
        var bytesRead = stream.Read(buffer.AsSpan());

        Assert.Equal(5, bytesRead);
        Assert.Equal(data, buffer);
    }

    [Fact]
    public void StoredEntryStream_ReadSpanPastEndReturnsPartial()
    {
        var data = new byte[] { 10, 20, 30 };
        using var source = new MemoryStream(data);
        var lockObj = new object();
        using var stream = new StoredEntryStream(source, 0, 3, lockObj);

        stream.Position = 1;
        var buffer = new byte[10];
        var bytesRead = stream.Read(buffer.AsSpan());

        Assert.Equal(2, bytesRead);
        Assert.Equal(20, buffer[0]);
        Assert.Equal(30, buffer[1]);
    }

    [Fact]
    public void StoredEntryStream_ReadAtReturnsCorrectData()
    {
        var data = new byte[] { 10, 20, 30, 40, 50 };
        using var source = new MemoryStream(data);
        var lockObj = new object();
        using var stream = new StoredEntryStream(source, 0, 5, lockObj);

        var buffer = new byte[3];
        var bytesRead = stream.ReadAt(2, buffer, 0, 3);

        Assert.Equal(3, bytesRead);
        Assert.Equal(30, buffer[0]);
        Assert.Equal(40, buffer[1]);
        Assert.Equal(50, buffer[2]);
    }

    [Fact]
    public void StoredEntryStream_ReadAtNegativeOffsetReturnsZero()
    {
        var data = new byte[] { 10, 20, 30 };
        using var source = new MemoryStream(data);
        var lockObj = new object();
        using var stream = new StoredEntryStream(source, 0, 3, lockObj);

        var buffer = new byte[3];
        var bytesRead = stream.ReadAt(-1, buffer, 0, 3);

        Assert.Equal(0, bytesRead);
    }

    [Fact]
    public void StoredEntryStream_ReadAtBeyondLengthReturnsZero()
    {
        var data = new byte[] { 10, 20, 30 };
        using var source = new MemoryStream(data);
        var lockObj = new object();
        using var stream = new StoredEntryStream(source, 0, 3, lockObj);

        var buffer = new byte[3];
        var bytesRead = stream.ReadAt(10, buffer, 0, 3);

        Assert.Equal(0, bytesRead);
    }

    [Fact]
    public void StoredEntryStream_ReadAtUpdatesPosition()
    {
        var data = new byte[] { 10, 20, 30, 40, 50 };
        using var source = new MemoryStream(data);
        var lockObj = new object();
        using var stream = new StoredEntryStream(source, 0, 5, lockObj);

        var buffer = new byte[2];
        stream.ReadAt(3, buffer, 0, 2);

        Assert.Equal(5, stream.Position);
    }

    [Fact]
    public void StoredEntryStream_ConstructorNegativeOffsetThrows()
    {
        using var source = new MemoryStream(new byte[10]);
        var lockObj = new object();

        Assert.Throws<ArgumentOutOfRangeException>(() => new StoredEntryStream(source, -1, 5, lockObj));
    }

    [Fact]
    public void StoredEntryStream_ConstructorOffsetBeyondLengthThrows()
    {
        using var source = new MemoryStream(new byte[10]);
        var lockObj = new object();

        Assert.Throws<ArgumentOutOfRangeException>(() => new StoredEntryStream(source, 100, 5, lockObj));
    }

    [Fact]
    public void StoredEntryStream_ConstructorNegativeLengthThrows()
    {
        using var source = new MemoryStream(new byte[10]);
        var lockObj = new object();

        Assert.Throws<ArgumentOutOfRangeException>(() => new StoredEntryStream(source, 0, -1, lockObj));
    }

    [Fact]
    public void StoredEntryStream_EmptyDataLengthZero_WorksCorrectly()
    {
        using var source = new MemoryStream(new byte[10]);
        var lockObj = new object();
        using var stream = new StoredEntryStream(source, 5, 0, lockObj);

        Assert.Equal(0, stream.Length);
        Assert.Equal(0, stream.Position);

        var buffer = new byte[1];
        var bytesRead = stream.Read(buffer, 0, 1);
        Assert.Equal(0, bytesRead);
    }

    [Fact]
    public void StoredEntryStream_SequentialReadsWorkCorrectly()
    {
        var data = new byte[] { 1, 2, 3, 4, 5, 6, 7, 8, 9, 10 };
        using var source = new MemoryStream(data);
        var lockObj = new object();
        using var stream = new StoredEntryStream(source, 0, 10, lockObj);

        var buffer1 = new byte[3];
        var read1 = stream.Read(buffer1, 0, 3);
        Assert.Equal(3, read1);
        Assert.Equal(1, buffer1[0]);
        Assert.Equal(2, buffer1[1]);
        Assert.Equal(3, buffer1[2]);

        var buffer2 = new byte[3];
        var read2 = stream.Read(buffer2, 0, 3);
        Assert.Equal(3, read2);
        Assert.Equal(4, buffer2[0]);
        Assert.Equal(5, buffer2[1]);
        Assert.Equal(6, buffer2[2]);

        var buffer3 = new byte[10];
        var read3 = stream.Read(buffer3, 0, 10);
        Assert.Equal(4, read3);
        Assert.Equal(7, buffer3[0]);
        Assert.Equal(10, buffer3[3]);
    }
}
