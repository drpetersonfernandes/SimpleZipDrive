using System.Reflection;

namespace SimpleZipDrive.Tests;

[Collection("ErrorLogger")]
public class ErrorLoggerExtendedTests : IDisposable
{
    private readonly ErrorLogger _errorLogger;
    private readonly string _tempLogFilePath;

    private static readonly MethodInfo? GetEnvironmentDetailsMethod = typeof(ErrorLogger)
        .GetMethod("GetEnvironmentDetails", BindingFlags.NonPublic | BindingFlags.Instance);

    private static readonly MethodInfo? GetExceptionDetailsMethod = typeof(ErrorLogger)
        .GetMethod("GetExceptionDetails", BindingFlags.NonPublic | BindingFlags.Static);

    private static readonly MethodInfo? GetErrorDetailsMethod = typeof(ErrorLogger)
        .GetMethod("GetErrorDetails", BindingFlags.NonPublic | BindingFlags.Static);

    private static readonly MethodInfo? FormatErrorMessageMethod = typeof(ErrorLogger)
        .GetMethod("FormatErrorMessage", BindingFlags.NonPublic | BindingFlags.Static);

    public ErrorLoggerExtendedTests()
    {
        _tempLogFilePath = Path.Combine(Path.GetTempPath(), $"ErrorLoggerExtTests_{Guid.NewGuid()}.log");
        _errorLogger = new ErrorLogger(_tempLogFilePath);
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
            // ignored
        }
    }

    [Fact]
    public void GetEnvironmentDetails_ReturnsApplicationName()
    {
        var result = GetEnvironmentDetailsMethod?.Invoke(_errorLogger, null) as string;
        Assert.NotNull(result);
        Assert.Contains("SimpleZipDrive", result);
    }

    [Fact]
    public void GetEnvironmentDetails_ReturnsExpectedSections()
    {
        var result = GetEnvironmentDetailsMethod?.Invoke(_errorLogger, null) as string;
        Assert.NotNull(result);
        Assert.Contains("Environment Details", result);
        Assert.Contains("OS Version", result);
        Assert.Contains("Architecture", result);
        Assert.Contains("Processor Count", result);
        Assert.Contains("Temp Path", result);
    }

    [Fact]
    public void GetExceptionDetails_ContainsTypeAndMessage()
    {
        var ex = new InvalidOperationException("test operation failed");
        var result = GetExceptionDetailsMethod?.Invoke(null, [ex]) as string;
        Assert.NotNull(result);
        Assert.Contains("InvalidOperationException", result);
        Assert.Contains("test operation failed", result);
        Assert.Contains("Exception Details", result);
    }

    [Fact]
    public void GetExceptionDetails_IncludesInnerException()
    {
        var inner = new ArgumentException("inner arg error");
        var ex = new IOException("outer io error", inner);
        var result = GetExceptionDetailsMethod?.Invoke(null, [ex]) as string;
        Assert.NotNull(result);
        Assert.Contains("ArgumentException", result);
        Assert.Contains("inner arg error", result);
        Assert.Contains("Inner Exception", result);
    }

    [Fact]
    public async Task GetExceptionDetails_IncludesStackTraceAsync()
    {
        var ex = await Record.ExceptionAsync(static () => Task.Run(static () => throw new InvalidOperationException("stack trace test")));
        var result = GetExceptionDetailsMethod?.Invoke(null, [ex]) as string;
        Assert.NotNull(result);
        Assert.Contains("stack trace test", result);
        Assert.Contains("StackTrace", result);
    }

    [Fact]
    public void GetErrorDetails_ContainsContextAndMessage()
    {
        var ex = new IOException("file error");
        var result = GetErrorDetailsMethod?.Invoke(null, [ex, "test context"]) as string;
        Assert.NotNull(result);
        Assert.Contains("test context", result);
        Assert.Contains("file error", result);
        Assert.Contains("Error Details", result);
    }

    [Fact]
    public void FormatErrorMessage_ContainsAllSections()
    {
        var ex = new InvalidOperationException("format test error");
        var result = FormatErrorMessageMethod?.Invoke(null, [ex, "format context"]) as string;
        Assert.NotNull(result);
        Assert.Contains("Timestamp", result);
        Assert.Contains("Application: SimpleZipDrive", result);
        Assert.Contains("Context: format context", result);
        Assert.Contains("InvalidOperationException", result);
        Assert.Contains("format test error", result);
        Assert.Contains("Stack Trace", result);
    }

    [Fact]
    public void FormatErrorMessage_WithInnerException_IncludesInnerDetails()
    {
        var inner = new InvalidOperationException("inner cause");
        var ex = new IOException("outer", inner);
        var result = FormatErrorMessageMethod?.Invoke(null, [ex, "inner test"]) as string;
        Assert.NotNull(result);
        Assert.Contains("Inner Exception", result);
        Assert.Contains("InvalidOperationException", result);
        Assert.Contains("inner cause", result);
    }

    [Fact]
    public void WriteToCriticalLog_WritesToConsoleError()
    {
        var originalError = Console.Error;
        try
        {
            using var capture = new StringWriter();
            Console.SetError(capture);

            var method = typeof(ErrorLogger).GetMethod("WriteToCriticalLog",
                BindingFlags.NonPublic | BindingFlags.Instance);
            var ex = new IOException("critical failure");
            method?.Invoke(_errorLogger, [ex, "critical context"]);

            var output = capture.ToString();
            Assert.Contains("FATAL", output);
            Assert.Contains("critical context", output);
            Assert.Contains("critical failure", output);
            Assert.Contains("IOException", output);
        }
        finally
        {
            Console.SetError(originalError);
        }
    }

    [Fact]
    public void LogErrorSync_WritesToConfiguredFilePath()
    {
        var ex = new FileNotFoundException("custom path test");
        _errorLogger.LogErrorSync(ex, "path test");

        Assert.True(File.Exists(_tempLogFilePath));
        var content = File.ReadAllText(_tempLogFilePath);
        Assert.Contains("custom path test", content);
        Assert.Contains("path test", content);
    }

    [Fact]
    public void ErrorLogFilePath_IsConfigurable()
    {
        var customPath = Path.Combine(Path.GetTempPath(), $"custom_{Guid.NewGuid()}.log");
        try
        {
            using var logger = new ErrorLogger(customPath);
            logger.LogErrorSync(new IOException("custom"), "test");
            Assert.True(File.Exists(customPath));
        }
        finally
        {
            try
            {
                File.Delete(customPath);
            }
            catch
            {
                // ignored
            }
        }
    }

    [Fact]
    public void FireAndForget_MethodExists()
    {
        var fireAndForgetMethod = typeof(ErrorLogger).GetMethod("FireAndForgetAsync",
            BindingFlags.NonPublic | BindingFlags.Instance);

        Assert.NotNull(fireAndForgetMethod);
    }

    public void Dispose()
    {
        _errorLogger.Dispose();
        CleanupLogFile();
        GC.SuppressFinalize(this);
    }
}
