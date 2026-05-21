using System.Reflection;

namespace SimpleZipDrive.Tests;

[Collection("ErrorLogger")]
public class ErrorLoggerTests : IDisposable
{
    private readonly ErrorLogger _errorLogger;
    private readonly string _tempLogFilePath;
    private static readonly MethodInfo? IsUserErrorMethod = typeof(ErrorLogger).GetMethod("IsUserError", BindingFlags.NonPublic | BindingFlags.Static);

    public ErrorLoggerTests()
    {
        // Use a temporary file for testing to avoid file system conflicts and ensure cleanup
        _tempLogFilePath = Path.Combine(Path.GetTempPath(), $"ErrorLoggerTests_{Guid.NewGuid()}.log");

        // Create ErrorLogger with temporary file path
        _errorLogger = new ErrorLogger(_tempLogFilePath);

        // Ensure clean state
        CleanupLogFile();
    }

    private void CleanupLogFile()
    {
        try
        {
            if (File.Exists(_tempLogFilePath))
                File.Delete(_tempLogFilePath);
        }
        catch
        {
            // Best effort cleanup - ignore failures
        }
    }

    [Fact]
    public void LogErrorSyncUserErrorWritesToLogFile()
    {
        var ex = new FileNotFoundException("test file missing");
        _errorLogger.LogErrorSync(ex, "test context");

        Assert.True(File.Exists(_tempLogFilePath));
        var content = File.ReadAllText(_tempLogFilePath);
        Assert.Contains("test file missing", content);
        Assert.Contains("test context", content);
        Assert.Contains("FileNotFoundException", content);
    }

    [Fact]
    public void LogErrorSyncNullExceptionCreatesPlaceholder()
    {
        _errorLogger.LogErrorSync(null, "null test");

        Assert.True(File.Exists(_tempLogFilePath));
        var content = File.ReadAllText(_tempLogFilePath);
        Assert.Contains("null exception object", content);
        Assert.Contains("null test", content);
    }

    [Fact]
    public async Task LogErrorAsyncUserErrorWritesToLogFileAsync()
    {
        var ex = new DirectoryNotFoundException("test dir missing");
        await _errorLogger.LogErrorAsync(ex, "async test");

        Assert.True(File.Exists(_tempLogFilePath));
        var content = await File.ReadAllTextAsync(_tempLogFilePath);
        Assert.Contains("test dir missing", content);
        Assert.Contains("async test", content);
        Assert.Contains("DirectoryNotFoundException", content);
    }

    [Fact]
    public void LogErrorSyncNullContextWritesDefault()
    {
        var ex = new FileNotFoundException("test");
        _errorLogger.LogErrorSync(ex);

        Assert.True(File.Exists(_tempLogFilePath));
        var content = File.ReadAllText(_tempLogFilePath);
        Assert.Contains("No additional context provided.", content);
    }

    [Fact]
    public async Task LogErrorAsyncNullContextWritesDefaultAsync()
    {
        var ex = new FileNotFoundException("test");
        await _errorLogger.LogErrorAsync(ex);

        Assert.True(File.Exists(_tempLogFilePath));
        var content = await File.ReadAllTextAsync(_tempLogFilePath);
        Assert.Contains("No additional context provided.", content);
    }

    [Fact]
    public void LogErrorSyncWithInnerExceptionIncludesInnerDetails()
    {
        var inner = new InvalidOperationException("inner cause");
        var outer = new IOException("outer error", inner);
        _errorLogger.LogErrorSync(outer, "inner test");

        Assert.True(File.Exists(_tempLogFilePath));
        var content = File.ReadAllText(_tempLogFilePath);
        Assert.Contains("outer error", content);
        Assert.Contains("inner cause", content);
        Assert.Contains("Inner Exception", content);
        Assert.Contains("InvalidOperationException", content);
    }

    [Fact]
    public async Task LogErrorAsyncNullExceptionCreatesPlaceholderAsync()
    {
        await _errorLogger.LogErrorAsync(null, "async null test");

        Assert.True(File.Exists(_tempLogFilePath));
        var content = await File.ReadAllTextAsync(_tempLogFilePath);
        Assert.Contains("null exception object", content);
        Assert.Contains("async null test", content);
    }

    [Fact]
    public void IsUserErrorNullReturnsFalse()
    {
        var result = IsUserErrorMethod != null && (bool)(IsUserErrorMethod.Invoke(null, [null]) ?? throw new InvalidOperationException());
        Assert.False(result);
    }

    [Fact]
    public void IsUserErrorFileNotFoundExceptionReturnsTrue()
    {
        var result = IsUserErrorMethod != null && (bool)(IsUserErrorMethod.Invoke(null, [new FileNotFoundException()]) ?? throw new InvalidOperationException());
        Assert.True(result);
    }

    [Fact]
    public void IsUserErrorDirectoryNotFoundExceptionReturnsTrue()
    {
        var result = IsUserErrorMethod != null && (bool)(IsUserErrorMethod.Invoke(null, [new DirectoryNotFoundException()]) ?? throw new InvalidOperationException());
        Assert.True(result);
    }

    [Fact]
    public void IsUserErrorUnauthorizedAccessExceptionReturnsTrue()
    {
        var result = IsUserErrorMethod != null && (bool)(IsUserErrorMethod.Invoke(null, [new UnauthorizedAccessException()]) ?? throw new InvalidOperationException());
        Assert.True(result);
    }

    [Fact]
    public void IsUserErrorIoExceptionReturnsTrue()
    {
        var result = IsUserErrorMethod != null && (bool)(IsUserErrorMethod.Invoke(null, [new IOException()]) ?? throw new InvalidOperationException());
        Assert.True(result);
    }

    [Fact]
#pragma warning disable CA2201 // Do not raise reserved exception types
    public void IsUserErrorGenericExceptionReturnsFalse()
    {
        var result = IsUserErrorMethod != null && (bool)(IsUserErrorMethod.Invoke(null, [new Exception("generic error")]) ?? throw new InvalidOperationException());
        Assert.False(result);
    }
#pragma warning restore CA2201

    [Fact]
    public void IsUserErrorMessageContainsPasswordReturnsTrue()
    {
        var result = IsUserErrorMethod != null && (bool)(IsUserErrorMethod.Invoke(null, [new InvalidOperationException("file requires a Password")]) ?? throw new InvalidOperationException());
        Assert.True(result);
    }

    [Fact]
    public void IsUserErrorMessageContainsEncryptedReturnsTrue()
    {
        var result = IsUserErrorMethod != null && (bool)(IsUserErrorMethod.Invoke(null, [new InvalidOperationException("archive is Encrypted")]) ?? throw new InvalidOperationException());
        Assert.True(result);
    }

    [Fact]
    public void IsUserErrorMessageContainsRarAndHeaderReturnsTrue()
    {
        var result = IsUserErrorMethod != null && (bool)(IsUserErrorMethod.Invoke(null, [new InvalidOperationException("Rar format: invalid header found")]) ?? throw new InvalidOperationException());
        Assert.True(result);
    }

    [Fact]
    public void IsUserErrorMessageContainsInvalidArchiveReturnsTrue()
    {
        var result = IsUserErrorMethod != null && (bool)(IsUserErrorMethod.Invoke(null, [new InvalidOperationException("this is an Invalid Archive file")]) ?? throw new InvalidOperationException());
        Assert.True(result);
    }

    [Fact]
    public void IsUserErrorMessageContainsCorruptReturnsTrue()
    {
        var result = IsUserErrorMethod != null && (bool)(IsUserErrorMethod.Invoke(null, [new InvalidOperationException("archive is Corrupt")]) ?? throw new InvalidOperationException());
        Assert.True(result);
    }

    [Fact]
    public void IsUserErrorMessageContainsDriveLetterPatternReturnsTrue()
    {
        var result = IsUserErrorMethod != null && (bool)(IsUserErrorMethod.Invoke(null, [new InvalidOperationException("can't assign a drive letter to this device")]) ?? throw new InvalidOperationException());
        Assert.True(result);
    }

    [Fact]
    public void IsUserErrorOperationCanceledExceptionReturnsTrue()
    {
        var result = IsUserErrorMethod != null && (bool)(IsUserErrorMethod.Invoke(null, [new OperationCanceledException()]) ?? throw new InvalidOperationException());
        Assert.True(result);
    }

    [Fact]
    public void IsUserErrorTaskCanceledExceptionReturnsTrue()
    {
        var result = IsUserErrorMethod != null && (bool)(IsUserErrorMethod.Invoke(null, [new TaskCanceledException()]) ?? throw new InvalidOperationException());
        Assert.True(result);
    }

    [Fact]
    public void IsUserErrorHttpRequestExceptionWithInnerOperationCanceledExceptionReturnsTrue()
    {
        var ex = new HttpRequestException("request failed", new OperationCanceledException());
        var result = IsUserErrorMethod != null && (bool)(IsUserErrorMethod.Invoke(null, [ex]) ?? throw new InvalidOperationException());
        Assert.True(result);
    }

    [Fact]
    public void IsUserErrorMessageContainsCanceledReturnsTrue()
    {
        var result = IsUserErrorMethod != null && (bool)(IsUserErrorMethod.Invoke(null, [new InvalidOperationException("operation was canceled")]) ?? throw new InvalidOperationException());
        Assert.True(result);
    }

    [Fact]
    public void IsUserErrorMessageContainsCancelledReturnsTrue()
    {
        var result = IsUserErrorMethod != null && (bool)(IsUserErrorMethod.Invoke(null, [new InvalidOperationException("operation was cancelled by user")]) ?? throw new InvalidOperationException());
        Assert.True(result);
    }

    [Fact]
    public void IsUserErrorMessageContainsTimeoutReturnsTrue()
    {
        var result = IsUserErrorMethod != null && (bool)(IsUserErrorMethod.Invoke(null, [new InvalidOperationException("connection timeout")]) ?? throw new InvalidOperationException());
        Assert.True(result);
    }

    [Fact]
    public void IsUserErrorMessageContainsCannotFindCentralDirectoryReturnsTrue()
    {
        var result = IsUserErrorMethod != null && (bool)(IsUserErrorMethod.Invoke(null, [new InvalidOperationException("cannot find central directory in zip")]) ?? throw new InvalidOperationException());
        Assert.True(result);
    }

    [Fact]
    public void IsUserErrorMessageContainsUnknownFormatReturnsTrue()
    {
        var result = IsUserErrorMethod != null && (bool)(IsUserErrorMethod.Invoke(null, [new InvalidOperationException("unknown format for archive")]) ?? throw new InvalidOperationException());
        Assert.True(result);
    }

    [Fact]
    public void IsUserErrorMessageContainsNotAValidReturnsTrue()
    {
        var result = IsUserErrorMethod != null && (bool)(IsUserErrorMethod.Invoke(null, [new InvalidOperationException("this is not a valid archive")]) ?? throw new InvalidOperationException());
        Assert.True(result);
    }

    [Fact]
    public void IsUserErrorMessageContainsDriveLetterInUseReturnsTrue()
    {
        var result = IsUserErrorMethod != null && (bool)(IsUserErrorMethod.Invoke(null, [new InvalidOperationException("drive letter is in use")]) ?? throw new InvalidOperationException());
        Assert.True(result);
    }

    [Fact]
    public void IsUserErrorMessageContainsMountPointInvalidReturnsTrue()
    {
        var result = IsUserErrorMethod != null && (bool)(IsUserErrorMethod.Invoke(null, [new InvalidOperationException("mount point is invalid")]) ?? throw new InvalidOperationException());
        Assert.True(result);
    }

    [Fact]
    public void IsUserErrorMessageContainsHeaderAndInvalidReturnsTrue()
    {
        var result = IsUserErrorMethod != null && (bool)(IsUserErrorMethod.Invoke(null, [new InvalidOperationException("archive header is invalid")]) ?? throw new InvalidOperationException());
        Assert.True(result);
    }

    [Fact]
    public void IsUserErrorSharpCompressArchiveExceptionReturnsTrue()
    {
        var result = IsUserErrorMethod != null && (bool)(IsUserErrorMethod.Invoke(null, [new SharpCompress.Common.ArchiveException("invalid archive")]) ?? throw new InvalidOperationException());
        Assert.True(result);
    }

    [Fact]
    public void IsUserErrorSharpCompressInvalidFormatExceptionReturnsTrue()
    {
        var result = IsUserErrorMethod != null && (bool)(IsUserErrorMethod.Invoke(null, [new SharpCompress.Common.InvalidFormatException("invalid format")]) ?? throw new InvalidOperationException());
        Assert.True(result);
    }

    public void Dispose()
    {
        _errorLogger.Dispose();
        CleanupLogFile();
        GC.SuppressFinalize(this);
    }
}
