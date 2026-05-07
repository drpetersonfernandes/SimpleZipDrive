using System.Reflection;

namespace SimpleZipDrive.Tests;

[Collection("ErrorLogger")]
public class ErrorLoggerTests : IDisposable
{
    private readonly string _logFilePath;
    private static readonly MethodInfo? IsUserErrorMethod = typeof(ErrorLogger).GetMethod("IsUserError", BindingFlags.NonPublic | BindingFlags.Static);

    public ErrorLoggerTests()
    {
        _logFilePath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "error.log");
        if (File.Exists(_logFilePath))
            File.Delete(_logFilePath);
    }

    [Fact]
    public void LogErrorSyncUserErrorWritesToLogFile()
    {
        var ex = new FileNotFoundException("test file missing");
        ErrorLogger.LogErrorSync(ex, "test context");

        Assert.True(File.Exists(_logFilePath));
        var content = File.ReadAllText(_logFilePath);
        Assert.Contains("test file missing", content);
        Assert.Contains("test context", content);
        Assert.Contains("FileNotFoundException", content);
    }

    [Fact]
    public void LogErrorSyncNullExceptionCreatesPlaceholder()
    {
        ErrorLogger.LogErrorSync(null, "null test");

        Assert.True(File.Exists(_logFilePath));
        var content = File.ReadAllText(_logFilePath);
        Assert.Contains("null exception object", content);
        Assert.Contains("null test", content);
    }

    [Fact]
    public async Task LogErrorAsyncUserErrorWritesToLogFile()
    {
        var ex = new DirectoryNotFoundException("test dir missing");
        await ErrorLogger.LogErrorAsync(ex, "async test");

        Assert.True(File.Exists(_logFilePath));
        var content = await File.ReadAllTextAsync(_logFilePath);
        Assert.Contains("test dir missing", content);
        Assert.Contains("async test", content);
        Assert.Contains("DirectoryNotFoundException", content);
    }

    [Fact]
    public void LogErrorSyncNullContextWritesDefault()
    {
        var ex = new FileNotFoundException("test");
        ErrorLogger.LogErrorSync(ex);

        Assert.True(File.Exists(_logFilePath));
        var content = File.ReadAllText(_logFilePath);
        Assert.Contains("No additional context provided.", content);
    }

    [Fact]
    public async Task LogErrorAsyncNullContextWritesDefault()
    {
        var ex = new FileNotFoundException("test");
        await ErrorLogger.LogErrorAsync(ex);

        Assert.True(File.Exists(_logFilePath));
        var content = await File.ReadAllTextAsync(_logFilePath);
        Assert.Contains("No additional context provided.", content);
    }

    [Fact]
    public void LogErrorSyncWithInnerExceptionIncludesInnerDetails()
    {
        var inner = new InvalidOperationException("inner cause");
        var outer = new IOException("outer error", inner);
        ErrorLogger.LogErrorSync(outer, "inner test");

        Assert.True(File.Exists(_logFilePath));
        var content = File.ReadAllText(_logFilePath);
        Assert.Contains("outer error", content);
        Assert.Contains("inner cause", content);
        Assert.Contains("Inner Exception", content);
        Assert.Contains("InvalidOperationException", content);
    }

    [Fact]
    public async Task LogErrorAsyncNullExceptionCreatesPlaceholder()
    {
        await ErrorLogger.LogErrorAsync(null, "async null test");

        Assert.True(File.Exists(_logFilePath));
        var content = await File.ReadAllTextAsync(_logFilePath);
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

    public void Dispose()
    {
        if (File.Exists(_logFilePath))
            File.Delete(_logFilePath);
        GC.SuppressFinalize(this);
    }
}
