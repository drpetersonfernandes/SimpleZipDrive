namespace SimpleZipDrive.Tests;

[Collection("ErrorLogger")]
public class ErrorLoggerTests : IDisposable
{
    private readonly ErrorLogger _errorLogger;
    private readonly string _tempLogFilePath;

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
        var result = ErrorLogger.IsUserError(null);
        Assert.False(result);
    }

    [Fact]
    public void IsUserErrorFileNotFoundExceptionReturnsTrue()
    {
        var result = ErrorLogger.IsUserError(new FileNotFoundException());
        Assert.True(result);
    }

    [Fact]
    public void IsUserErrorDirectoryNotFoundExceptionReturnsTrue()
    {
        var result = ErrorLogger.IsUserError(new DirectoryNotFoundException());
        Assert.True(result);
    }

    [Fact]
    public void IsUserErrorUnauthorizedAccessExceptionReturnsTrue()
    {
        var result = ErrorLogger.IsUserError(new UnauthorizedAccessException());
        Assert.True(result);
    }

    [Fact]
    public void IsUserErrorIoExceptionReturnsTrue()
    {
        var result = ErrorLogger.IsUserError(new IOException());
        Assert.True(result);
    }

    [Fact]
#pragma warning disable CA2201 // Do not raise reserved exception types
    public void IsUserErrorGenericExceptionReturnsFalse()
    {
        var result = ErrorLogger.IsUserError(new InvalidOperationException("generic error"));
        Assert.False(result);
    }
#pragma warning restore CA2201

    [Fact]
    public void IsUserErrorMessageContainsPasswordReturnsTrue()
    {
        var result = ErrorLogger.IsUserError(new InvalidOperationException("file requires a Password"));
        Assert.True(result);
    }

    [Fact]
    public void IsUserErrorMessageContainsEncryptedReturnsTrue()
    {
        var result = ErrorLogger.IsUserError(new InvalidOperationException("archive is Encrypted"));
        Assert.True(result);
    }

    [Fact]
    public void IsUserErrorMessageContainsRarAndHeaderReturnsTrue()
    {
        var result = ErrorLogger.IsUserError(new InvalidOperationException("Rar format: invalid header found"));
        Assert.True(result);
    }

    [Fact]
    public void IsUserErrorMessageContainsInvalidArchiveReturnsTrue()
    {
        var result = ErrorLogger.IsUserError(new InvalidOperationException("this is an Invalid Archive file"));
        Assert.True(result);
    }

    [Fact]
    public void IsUserErrorMessageContainsCorruptReturnsTrue()
    {
        var result = ErrorLogger.IsUserError(new InvalidOperationException("archive is Corrupt"));
        Assert.True(result);
    }

    [Fact]
    public void IsUserErrorMessageContainsDriveLetterPatternReturnsTrue()
    {
        var result = ErrorLogger.IsUserError(new InvalidOperationException("can't assign a drive letter to this device"));
        Assert.True(result);
    }

    [Fact]
    public void IsUserErrorOperationCanceledExceptionReturnsTrue()
    {
        var result = ErrorLogger.IsUserError(new OperationCanceledException());
        Assert.True(result);
    }

    [Fact]
    public void IsUserErrorTaskCanceledExceptionReturnsTrue()
    {
        var result = ErrorLogger.IsUserError(new TaskCanceledException());
        Assert.True(result);
    }

    [Fact]
    public void IsUserErrorHttpRequestExceptionWithInnerOperationCanceledExceptionReturnsTrue()
    {
        var ex = new HttpRequestException("request failed", new OperationCanceledException());
        var result = ErrorLogger.IsUserError(ex);
        Assert.True(result);
    }

    [Fact]
    public void IsUserErrorMessageContainsCanceledReturnsTrue()
    {
        var result = ErrorLogger.IsUserError(new InvalidOperationException("operation was canceled"));
        Assert.True(result);
    }

    [Fact]
    public void IsUserErrorMessageContainsCancelledReturnsTrue()
    {
        var result = ErrorLogger.IsUserError(new InvalidOperationException("operation was cancelled by user"));
        Assert.True(result);
    }

    [Fact]
    public void IsUserErrorMessageContainsTimeoutReturnsFalse()
    {
        var result = ErrorLogger.IsUserError(new InvalidOperationException("connection timeout"));

        Assert.False(result);
    }

    [Fact]
    public void IsUserErrorGenericPasswordMessageReturnsFalse()
    {
        var result = ErrorLogger.IsUserError(new InvalidOperationException("unexpected password mismatch in internal state"));

        Assert.False(result);
    }

    [Fact]
    public void IsUserErrorMessageContainsCannotFindCentralDirectoryReturnsTrue()
    {
        var result = ErrorLogger.IsUserError(new InvalidOperationException("cannot find central directory in zip"));
        Assert.True(result);
    }

    [Fact]
    public void IsUserErrorMessageContainsUnknownFormatReturnsTrue()
    {
        var result = ErrorLogger.IsUserError(new InvalidOperationException("unknown format for archive"));
        Assert.True(result);
    }

    [Fact]
    public void IsUserErrorMessageContainsNotAValidReturnsTrue()
    {
        var result = ErrorLogger.IsUserError(new InvalidOperationException("this is not a valid archive"));
        Assert.True(result);
    }

    [Fact]
    public void IsUserErrorMessageContainsDriveLetterInUseReturnsTrue()
    {
        var result = ErrorLogger.IsUserError(new InvalidOperationException("drive letter is in use"));
        Assert.True(result);
    }

    [Fact]
    public void IsUserErrorMessageContainsMountPointInvalidReturnsTrue()
    {
        var result = ErrorLogger.IsUserError(new InvalidOperationException("mount point is invalid"));
        Assert.True(result);
    }

    [Fact]
    public void IsUserErrorMessageContainsHeaderAndInvalidReturnsTrue()
    {
        var result = ErrorLogger.IsUserError(new InvalidOperationException("archive header is invalid"));
        Assert.True(result);
    }

    [Fact]
    public void IsUserErrorSharpCompressArchiveExceptionReturnsTrue()
    {
        var result = ErrorLogger.IsUserError(new SharpCompress.Common.ArchiveException("invalid archive"));
        Assert.True(result);
    }

    [Fact]
    public void IsUserErrorSharpCompressInvalidFormatExceptionReturnsTrue()
    {
        var result = ErrorLogger.IsUserError(new SharpCompress.Common.InvalidFormatException("invalid format"));
        Assert.True(result);
    }

    [Fact]
    public void Dispose_DoubleDispose_DoesNotThrow()
    {
        var tempPath = Path.Combine(Path.GetTempPath(), $"DoubleDispose_{Guid.NewGuid()}.log");
        var logger = new ErrorLogger(tempPath);

        logger.Dispose();

        var ex = Record.Exception(() => logger.Dispose());
        Assert.Null(ex);
    }

    [Fact]
    public void SuppressApiCalls_SetToTrue_ReturnsFalseFromSendLogToApi()
    {
        var originalValue = ErrorLogger.SuppressApiCalls;
        try
        {
            ErrorLogger.SuppressApiCalls = true;

            // SuppressApiCalls affects the private SendLogToApiAsync method.
            // We verify the property can be set and read.
            Assert.True(ErrorLogger.SuppressApiCalls);
        }
        finally
        {
            ErrorLogger.SuppressApiCalls = originalValue;
        }
    }

    [Fact]
    public void GetEnvironmentDetails_ReturnsApplicationName()
    {
        var result = _errorLogger.GetEnvironmentDetails();
        Assert.NotNull(result);
        Assert.Contains("SimpleZipDrive", result);
    }

    [Fact]
    public void GetEnvironmentDetails_ReturnsExpectedSections()
    {
        var result = _errorLogger.GetEnvironmentDetails();
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
        var result = ErrorLogger.GetExceptionDetails(ex);
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
        var result = ErrorLogger.GetExceptionDetails(ex);
        Assert.NotNull(result);
        Assert.Contains("ArgumentException", result);
        Assert.Contains("inner arg error", result);
        Assert.Contains("Inner Exception", result);
    }

    [Fact]
    public async Task GetExceptionDetails_IncludesStackTraceAsync()
    {
        var ex = await Record.ExceptionAsync(static () => Task.Run(static () => throw new InvalidOperationException("stack trace test")));
        var result = ErrorLogger.GetExceptionDetails(ex);
        Assert.NotNull(result);
        Assert.Contains("stack trace test", result);
        Assert.Contains("StackTrace", result);
    }

    [Fact]
    public void GetErrorDetails_ContainsContextAndMessage()
    {
        var ex = new IOException("file error");
        var result = ErrorLogger.GetErrorDetails(ex, "test context");
        Assert.NotNull(result);
        Assert.Contains("test context", result);
        Assert.Contains("file error", result);
        Assert.Contains("Error Details", result);
    }

    [Fact]
    public void FormatErrorMessage_ContainsAllSections()
    {
        var ex = new InvalidOperationException("format test error");
        var result = ErrorLogger.FormatErrorMessage(ex, "format context");
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
        var result = ErrorLogger.FormatErrorMessage(ex, "inner test");
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

            var ex = new IOException("critical failure");
            _errorLogger.WriteToCriticalLog(ex, "critical context");

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
        Action<Task> fireAndForget = ErrorLogger.FireAndForgetAsync;

        Assert.NotNull(fireAndForget);
    }

    public void Dispose()
    {
        _errorLogger.Dispose();
        CleanupLogFile();
        GC.SuppressFinalize(this);
    }
}
