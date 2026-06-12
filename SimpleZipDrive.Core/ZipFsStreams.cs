using Microsoft.Win32.SafeHandles;

namespace SimpleZipDrive.Core;

/// <summary>
/// A MemoryStream wrapper that tracks memory usage and invokes a callback when disposed.
/// Used to prevent unbounded memory consumption when many small files are opened.
/// </summary>
internal sealed class TrackedMemoryStream : MemoryStream
{
    private readonly object _memoryLock;
    private readonly Action<int> _onDispose;
    private readonly int _size;
    private bool _disposed;

    public TrackedMemoryStream(byte[] buffer, object memoryLock, Action<int> onDispose) : base(buffer, false)
    {
        _memoryLock = memoryLock;
        _onDispose = onDispose;
        _size = buffer.Length;
        _disposed = false;
    }

    protected override void Dispose(bool disposing)
    {
        if (!_disposed)
        {
            if (disposing)
            {
                lock (_memoryLock)
                {
                    _onDispose(_size);
                }
            }

            _disposed = true;
        }

        base.Dispose(disposing);
    }
}

/// <summary>
/// Provides direct read access to a stored (uncompressed) entry within an archive stream
/// without extracting it to a separate cache. Uses the original archive stream with
/// position synchronization via an external lock.
/// </summary>
internal sealed class StoredEntryStream : Stream
{
    private const int ReadAheadBufferSize = 4 * 1024 * 1024; // 4 MB

    private readonly Stream _sourceStream;
    private readonly long _dataOffset;
    private readonly object _sourceLock;
    private readonly SafeFileHandle? _fileHandle;
    private long _position;
    private bool _disposed;

    private byte[]? _readAheadBuffer;
    private long _readAheadFileOffset = -1;
    private int _readAheadLength;
    private long _lastReadEnd = -1;

    public StoredEntryStream(Stream sourceStream, long dataOffset, long dataLength, object sourceLock)
    {
        if (dataOffset < 0 || dataOffset > sourceStream.Length)
            throw new ArgumentOutOfRangeException(nameof(dataOffset));

        ArgumentOutOfRangeException.ThrowIfNegative(dataLength);

        _sourceStream = sourceStream;
        _dataOffset = dataOffset;
        Length = dataLength;
        _sourceLock = sourceLock;
        _fileHandle = (sourceStream as FileStream)?.SafeFileHandle;
        _position = 0;
        _sourceStream.Position = dataOffset;

        if (dataLength > ReadAheadBufferSize)
        {
            _readAheadBuffer = new byte[ReadAheadBufferSize];
        }
    }

    public override bool CanRead => true;
    public override bool CanSeek => true;
    public override bool CanWrite => false;
    public override long Length { get; }

    public override long Position
    {
        get => _position;
        set
        {
            if (value < 0 || value > Length)
                throw new ArgumentOutOfRangeException(nameof(value));

            _position = value;
            _lastReadEnd = -1;
        }
    }

    public override int Read(byte[] buffer, int offset, int count)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        var maxBytes = (int)Math.Min(count, Length - _position);
        if (maxBytes <= 0) return 0;

        var targetPosition = _dataOffset + _position;

        if (_fileHandle != null)
        {
            var bytesRead = RandomAccess.Read(_fileHandle, buffer.AsSpan(offset, maxBytes), targetPosition);
            _position += bytesRead;
            return bytesRead;
        }

        lock (_sourceLock)
        {
            if (_sourceStream.Position != targetPosition)
            {
                _sourceStream.Position = targetPosition;
            }

            var bytesRead = _sourceStream.Read(buffer, offset, maxBytes);
            _position += bytesRead;
            return bytesRead;
        }
    }

    public override int Read(Span<byte> buffer)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        var maxBytes = (int)Math.Min(buffer.Length, Length - _position);
        if (maxBytes <= 0) return 0;

        var targetPosition = _dataOffset + _position;

        if (_fileHandle != null)
        {
            var bytesRead = RandomAccess.Read(_fileHandle, buffer[..maxBytes], targetPosition);
            _position += bytesRead;
            return bytesRead;
        }

        lock (_sourceLock)
        {
            if (_sourceStream.Position != targetPosition)
            {
                _sourceStream.Position = targetPosition;
            }

            var bytesRead = _sourceStream.Read(buffer[..maxBytes]);
            _position += bytesRead;
            return bytesRead;
        }
    }

    public int ReadAt(long fileOffset, byte[] buffer, int bufferOffset, int count)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (fileOffset < 0 || fileOffset >= Length)
            return 0;

        var maxBytes = (int)Math.Min(count, Length - fileOffset);
        if (maxBytes <= 0) return 0;

        var buf = _readAheadBuffer;
        if (buf != null)
        {
            var isSequential = _lastReadEnd >= 0 && fileOffset == _lastReadEnd;

            switch (isSequential)
            {
                case true
                    when _readAheadFileOffset >= 0
                         && fileOffset >= _readAheadFileOffset
                         && fileOffset < _readAheadFileOffset + _readAheadLength:
                    {
                        var bufStart = (int)(fileOffset - _readAheadFileOffset);
                        var available = _readAheadLength - bufStart;
                        var toCopy = Math.Min(maxBytes, available);
                        Buffer.BlockCopy(buf, bufStart, buffer, bufferOffset, toCopy);
                        _position = fileOffset + toCopy;
                        _lastReadEnd = fileOffset + toCopy;
                        return toCopy;
                    }
                case true:
                    {
                        var readAheadSize = (int)Math.Min(ReadAheadBufferSize, Length - fileOffset);
                        var directBytes = ReadFromSource(fileOffset, buf, 0, readAheadSize);
                        _readAheadFileOffset = fileOffset;
                        _readAheadLength = directBytes;

                        var resultBytes = Math.Min(maxBytes, directBytes);
                        Buffer.BlockCopy(buf, 0, buffer, bufferOffset, resultBytes);
                        _position = fileOffset + resultBytes;
                        _lastReadEnd = fileOffset + resultBytes;
                        return resultBytes;
                    }
            }
        }

        var bytesRead = ReadFromSource(fileOffset, buffer, bufferOffset, maxBytes);
        _position = fileOffset + bytesRead;
        _lastReadEnd = fileOffset + bytesRead;
        return bytesRead;
    }

    private int ReadFromSource(long fileOffset, byte[] buffer, int bufferOffset, int count)
    {
        var targetPosition = _dataOffset + fileOffset;

        if (_fileHandle != null)
        {
            return RandomAccess.Read(_fileHandle, buffer.AsSpan(bufferOffset, count), targetPosition);
        }

        lock (_sourceLock)
        {
            if (_sourceStream.Position != targetPosition)
            {
                _sourceStream.Position = targetPosition;
            }

            return _sourceStream.Read(buffer, bufferOffset, count);
        }
    }

    public override long Seek(long offset, SeekOrigin origin)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        _position = origin switch
        {
            SeekOrigin.Begin => offset,
            SeekOrigin.Current => _position + offset,
            SeekOrigin.End => Length + offset,
            _ => throw new ArgumentOutOfRangeException(nameof(origin))
        };

        if (_position < 0 || _position > Length)
            throw new IOException("Seek position out of range");

        _lastReadEnd = -1;
        return _position;
    }

    public override void Flush()
    {
    }

    public override void SetLength(long value)
    {
        throw new NotSupportedException();
    }

    public override void Write(byte[] buffer, int offset, int count)
    {
        throw new NotSupportedException();
    }

    protected override void Dispose(bool disposing)
    {
        _disposed = true;
        _readAheadBuffer = null;
        base.Dispose(disposing);
    }
}
