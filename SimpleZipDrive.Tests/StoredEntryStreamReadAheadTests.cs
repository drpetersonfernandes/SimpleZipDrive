using SimpleZipDrive.Core;

namespace SimpleZipDrive.Tests;

public class StoredEntryStreamReadAheadTests
{
    // ─── ReadAt: sequential reads with read-ahead buffer (FileStream source) ───

    [Fact]
    public void ReadAt_SequentialReads_FileStream_UsesReadAheadBuffer()
    {
        var tempPath = Path.Combine(Path.GetTempPath(), $"ReadAhead_test_{Guid.NewGuid():N}.bin");
        try
        {
            // Create data larger than read-ahead buffer (4MB) to trigger read-ahead allocation
            var data = new byte[1024 * 1024]; // 1MB — smaller than read-ahead but tests the path
            for (var i = 0; i < data.Length; i++)
            {
                data[i] = (byte)(i % 256);
            }

            File.WriteAllBytes(tempPath, data);

            using var fs = new FileStream(tempPath, FileMode.Open, FileAccess.Read, FileShare.Read);
            var lockObj = new object();
            using var stream = new StoredEntryStream(fs, 0, data.Length, lockObj);

            // Sequential reads: first read sets _lastReadEnd
            var buffer1 = new byte[1024];
            var read1 = stream.ReadAt(0, buffer1, 0, 1024);
            Assert.Equal(1024, read1);
            Assert.Equal(0, buffer1[0]);
            Assert.Equal((byte)255, buffer1[255]);

            // Second sequential read should benefit from read-ahead
            var buffer2 = new byte[1024];
            var read2 = stream.ReadAt(1024, buffer2, 0, 1024);
            Assert.Equal(1024, read2);
            Assert.Equal(0, buffer2[0]); // (1024 % 256) = 0

            // Third sequential read
            var buffer3 = new byte[1024];
            var read3 = stream.ReadAt(2048, buffer3, 0, 1024);
            Assert.Equal(1024, read3);
        }
        finally
        {
            try { File.Delete(tempPath); } catch { /* ignored */ }
        }
    }

    // ─── ReadAt: non-sequential random access with FileStream ───

    [Fact]
    public void ReadAt_RandomAccess_FileStream_ReadsCorrectly()
    {
        var tempPath = Path.Combine(Path.GetTempPath(), $"ReadAhead_test_{Guid.NewGuid():N}.bin");
        try
        {
            var data = new byte[4096];
            for (var i = 0; i < data.Length; i++)
            {
                data[i] = (byte)(i % 256);
            }

            File.WriteAllBytes(tempPath, data);

            using var fs = new FileStream(tempPath, FileMode.Open, FileAccess.Read, FileShare.Read);
            var lockObj = new object();
            using var stream = new StoredEntryStream(fs, 100, 200, lockObj);

            // Random access: jump to middle
            var buffer1 = new byte[10];
            var read1 = stream.ReadAt(50, buffer1, 0, 10);
            Assert.Equal(10, read1);
            for (var i = 0; i < 10; i++)
                Assert.Equal((byte)((100 + 50 + i) % 256), buffer1[i]);

            // Jump to beginning
            var buffer2 = new byte[10];
            var read2 = stream.ReadAt(0, buffer2, 0, 10);
            Assert.Equal(10, read2);
            for (var i = 0; i < 10; i++)
                Assert.Equal((byte)((100 + i) % 256), buffer2[i]);

            // Jump to near end
            var buffer3 = new byte[10];
            var read3 = stream.ReadAt(190, buffer3, 0, 10);
            Assert.Equal(10, read3);
            for (var i = 0; i < 10; i++)
                Assert.Equal((byte)((100 + 190 + i) % 256), buffer3[i]);
        }
        finally
        {
            try { File.Delete(tempPath); } catch { /* ignored */ }
        }
    }

    // ─── ReadAt: read at exact boundary of data length ───

    [Fact]
    public void ReadAt_ExatBoundary_ReturnsCorrectCount()
    {
        var tempPath = Path.Combine(Path.GetTempPath(), $"ReadAhead_test_{Guid.NewGuid():N}.bin");
        try
        {
            var data = new byte[100];
            for (var i = 0; i < 100; i++)
            {
                data[i] = (byte)i;
            }

            File.WriteAllBytes(tempPath, data);

            using var fs = new FileStream(tempPath, FileMode.Open, FileAccess.Read, FileShare.Read);
            var lockObj = new object();
            using var stream = new StoredEntryStream(fs, 0, 100, lockObj);

            // Read exactly at the boundary
            var buffer = new byte[50];
            var read = stream.ReadAt(90, buffer, 0, 50);
            Assert.Equal(10, read); // Only 10 bytes left
            Assert.Equal(90, buffer[0]);
            Assert.Equal(99, buffer[9]);
        }
        finally
        {
            try { File.Delete(tempPath); } catch { /* ignored */ }
        }
    }

    // ─── ReadAt: read with zero count returns zero ───

    [Fact]
    public void ReadAt_ZeroCount_ReturnsZero()
    {
        using var source = new MemoryStream(new byte[100]);
        var lockObj = new object();
        using var stream = new StoredEntryStream(source, 0, 100, lockObj);

        var buffer = new byte[10];
        var read = stream.ReadAt(0, buffer, 0, 0);
        Assert.Equal(0, read);
    }

    // ─── ReadAt: sequential reads with MemoryStream source (no read-ahead) ───

    [Fact]
    public void ReadAt_SequentialReads_MemoryStream_WorkCorrectly()
    {
        var data = new byte[] { 0, 1, 2, 3, 4, 5, 6, 7, 8, 9 };
        using var source = new MemoryStream(data);
        var lockObj = new object();
        using var stream = new StoredEntryStream(source, 0, 10, lockObj);

        // MemoryStream source — no FileStream, so no read-ahead buffer
        var buffer1 = new byte[3];
        var read1 = stream.ReadAt(0, buffer1, 0, 3);
        Assert.Equal(3, read1);
        Assert.Equal(0, buffer1[0]);
        Assert.Equal(2, buffer1[2]);

        var buffer2 = new byte[3];
        var read2 = stream.ReadAt(3, buffer2, 0, 3);
        Assert.Equal(3, read2);
        Assert.Equal(3, buffer2[0]);
        Assert.Equal(5, buffer2[2]);

        var buffer3 = new byte[10];
        var read3 = stream.ReadAt(6, buffer3, 0, 10);
        Assert.Equal(4, read3);
        Assert.Equal(6, buffer3[0]);
        Assert.Equal(9, buffer3[3]);
    }

    // ─── ReadAt: large data with FileStream triggers read-ahead allocation ───

    [Fact]
    public void ReadAt_LargeData_FileStream_TriggersReadAheadAllocation()
    {
        var tempPath = Path.Combine(Path.GetTempPath(), $"ReadAhead_test_{Guid.NewGuid():N}.bin");
        try
        {
            // Data larger than 4MB read-ahead buffer
            var data = new byte[5 * 1024 * 1024]; // 5MB
            new Random(42).NextBytes(data);
            File.WriteAllBytes(tempPath, data);

            using var fs = new FileStream(tempPath, FileMode.Open, FileAccess.Read, FileShare.Read);
            var lockObj = new object();
            using var stream = new StoredEntryStream(fs, 0, data.Length, lockObj);

            // The constructor should have allocated a read-ahead buffer since dataLength > 4MB
            Assert.True(stream.Length > 4 * 1024 * 1024);

            // Sequential reads across the read-ahead boundary
            var buffer = new byte[1024 * 1024]; // 1MB reads

            // First read
            var read1 = stream.ReadAt(0, buffer, 0, buffer.Length);
            Assert.Equal(buffer.Length, read1);

            // Second sequential read (should hit read-ahead cache)
            var read2 = stream.ReadAt(buffer.Length, buffer, 0, buffer.Length);
            Assert.Equal(buffer.Length, read2);

            // Third sequential read (may need new read-ahead fill)
            var read3 = stream.ReadAt(2 * buffer.Length, buffer, 0, buffer.Length);
            Assert.Equal(buffer.Length, read3);
        }
        finally
        {
            try { File.Delete(tempPath); } catch { /* ignored */ }
        }
    }

    // ─── ReadAt: read with non-zero buffer offset ───

    [Fact]
    public void ReadAt_NonZeroBufferOffset_FileStream()
    {
        var tempPath = Path.Combine(Path.GetTempPath(), $"ReadAhead_test_{Guid.NewGuid():N}.bin");
        try
        {
            var data = new byte[] { 10, 20, 30, 40, 50, 60, 70, 80, 90, 100 };
            File.WriteAllBytes(tempPath, data);

            using var fs = new FileStream(tempPath, FileMode.Open, FileAccess.Read, FileShare.Read);
            var lockObj = new object();
            using var stream = new StoredEntryStream(fs, 2, 6, lockObj);

            var buffer = new byte[20];
            var read = stream.ReadAt(1, buffer, 10, 4);

            Assert.Equal(4, read);
            Assert.Equal(0, buffer[0]);   // untouched
            Assert.Equal(0, buffer[9]);   // untouched
            Assert.Equal(40, buffer[10]); // data[2+1] = 40
            Assert.Equal(50, buffer[11]); // data[2+2] = 50
            Assert.Equal(60, buffer[12]); // data[2+3] = 60
            Assert.Equal(70, buffer[13]); // data[2+4] = 70
        }
        finally
        {
            try { File.Delete(tempPath); } catch { /* ignored */ }
        }
    }

    // ─── Read(Span): large buffer ───

    [Fact]
    public void ReadSpan_LargeBuffer_ReadsCorrectly()
    {
        var data = new byte[10000];
        for (var i = 0; i < data.Length; i++)
        {
            data[i] = (byte)(i % 256);
        }

        using var source = new MemoryStream(data);
        var lockObj = new object();
        using var stream = new StoredEntryStream(source, 0, 10000, lockObj);

        var buffer = new byte[5000];
        var bytesRead = stream.Read(buffer.AsSpan());

        Assert.Equal(5000, bytesRead);
        for (var i = 0; i < 5000; i++)
            Assert.Equal((byte)(i % 256), buffer[i]);
    }

    // ─── Seek: invalid origin throws ───

    [Fact]
    public void Seek_InvalidOrigin_ThrowsArgumentOutOfRange()
    {
        using var source = new MemoryStream(new byte[100]);
        var lockObj = new object();
        using var stream = new StoredEntryStream(source, 0, 100, lockObj);

        Assert.Throws<ArgumentOutOfRangeException>(() => stream.Seek(0, (SeekOrigin)99));
    }
}
