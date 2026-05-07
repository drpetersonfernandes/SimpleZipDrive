namespace SimpleZipDrive.Tests;

[Collection("ErrorLogger")]
public class ErrorLoggerTests : IDisposable
{
    private readonly string _logFilePath;

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

    public void Dispose()
    {
        if (File.Exists(_logFilePath))
            File.Delete(_logFilePath);
        GC.SuppressFinalize(this);
    }
}
