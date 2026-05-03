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

    public void Dispose()
    {
        if (File.Exists(_logFilePath))
            File.Delete(_logFilePath);
        GC.SuppressFinalize(this);
    }
}
