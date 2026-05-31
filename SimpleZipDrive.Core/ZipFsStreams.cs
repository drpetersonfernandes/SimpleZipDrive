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
    private readonly Stream _sourceStream;
    private readonly long _dataOffset;
    private readonly object _sourceLock;
    private readonly SafeFileHandle? _fileHandle;
    private long _position;
    private bool _disposed;

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
        }
    }

    public override int Read(byte[] buffer, int offset, int count)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        var maxBytes = (int)Math.Min(count, Length - _position);
        if (maxBytes <= 0) return 0;

        if (Monitor.TryEnter(_sourceLock))
        {
            try
            {
                if (_sourceStream.Position == _dataOffset + _position)
                {
                    var bytesRead = _sourceStream.Read(buffer, offset, maxBytes);
                    _position += bytesRead;
                    return bytesRead;
                }
            }
            finally
            {
                Monitor.Exit(_sourceLock);
            }
        }

        if (_fileHandle != null)
        {
            var bytesRead = RandomAccess.Read(_fileHandle, buffer.AsSpan(offset, maxBytes), _dataOffset + _position);
            _position += bytesRead;
            return bytesRead;
        }

        lock (_sourceLock)
        {
            _sourceStream.Position = _dataOffset + _position;
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

        if (Monitor.TryEnter(_sourceLock))
        {
            try
            {
                if (_sourceStream.Position == _dataOffset + _position)
                {
                    var bytesRead = _sourceStream.Read(buffer[..maxBytes]);
                    _position += bytesRead;
                    return bytesRead;
                }
            }
            finally
            {
                Monitor.Exit(_sourceLock);
            }
        }

        if (_fileHandle != null)
        {
            var bytesRead = RandomAccess.Read(_fileHandle, buffer[..maxBytes], _dataOffset + _position);
            _position += bytesRead;
            return bytesRead;
        }

        lock (_sourceLock)
        {
            _sourceStream.Position = _dataOffset + _position;
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

        if (Monitor.TryEnter(_sourceLock))
        {
            try
            {
                if (_sourceStream.Position == _dataOffset + fileOffset)
                {
                    var bytesRead = _sourceStream.Read(buffer, bufferOffset, maxBytes);
                    _position = fileOffset + bytesRead;
                    return bytesRead;
                }
            }
            finally
            {
                Monitor.Exit(_sourceLock);
            }
        }

        if (_fileHandle != null)
        {
            var bytesRead = RandomAccess.Read(_fileHandle, buffer.AsSpan(bufferOffset, maxBytes), _dataOffset + fileOffset);
            _position = fileOffset + bytesRead;
            return bytesRead;
        }

        lock (_sourceLock)
        {
            _sourceStream.Position = _dataOffset + fileOffset;
            var bytesRead = _sourceStream.Read(buffer, bufferOffset, maxBytes);
            _position = fileOffset + bytesRead;
            return bytesRead;
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
        base.Dispose(disposing);
    }
}
