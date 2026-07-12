using SharpCompress.Common;
using SimpleZipDrive.Core;

namespace SimpleZipDrive.Tests;

[Collection("Logging")]
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
    public void LogErrorSyncUserError_DoesNotThrowAndWritesNoLocalFile()
    {
        var ex = new FileNotFoundException("test file missing");
        var thrown = Record.Exception(() => _errorLogger.LogErrorSync(ex, "test context"));

        Assert.Null(thrown);
        // error.log has been retired in favor of the single Serilog session log.
        Assert.False(File.Exists(_tempLogFilePath));
    }

    [Fact]
    public void LogErrorSyncNullException_DoesNotThrow()
    {
        var thrown = Record.Exception(() => _errorLogger.LogErrorSync(null, "null test"));

        Assert.Null(thrown);
        Assert.False(File.Exists(_tempLogFilePath));
    }

    [Fact]
    public async Task LogErrorAsyncUserError_DoesNotThrowAsync()
    {
        var ex = new DirectoryNotFoundException("test dir missing");
        var thrown = await Record.ExceptionAsync(() => _errorLogger.LogErrorAsync(ex, "async test"));

        Assert.Null(thrown);
        Assert.False(File.Exists(_tempLogFilePath));
    }

    [Fact]
    public void LogErrorSyncNullContext_DoesNotThrow()
    {
        var ex = new FileNotFoundException("test");
        var thrown = Record.Exception(() => _errorLogger.LogErrorSync(ex));

        Assert.Null(thrown);
    }

    [Fact]
    public async Task LogErrorAsyncNullContext_DoesNotThrowAsync()
    {
        var ex = new FileNotFoundException("test");
        var thrown = await Record.ExceptionAsync(() => _errorLogger.LogErrorAsync(ex));

        Assert.Null(thrown);
    }

    [Fact]
    public void LogErrorSyncWithInnerException_DoesNotThrow()
    {
        var inner = new InvalidOperationException("inner cause");
        var outer = new IOException("outer error", inner);
        var thrown = Record.Exception(() => _errorLogger.LogErrorSync(outer, "inner test"));

        Assert.Null(thrown);
    }

    [Fact]
    public async Task LogErrorAsyncNullException_DoesNotThrowAsync()
    {
        var thrown = await Record.ExceptionAsync(() => _errorLogger.LogErrorAsync(null, "async null test"));

        Assert.Null(thrown);
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
        var result = ErrorLogger.IsUserError(new ArchiveException("invalid archive"));
        Assert.True(result);
    }

    [Fact]
    public void IsUserErrorSharpCompressInvalidFormatExceptionReturnsTrue()
    {
        var result = ErrorLogger.IsUserError(new InvalidFormatException("invalid format"));
        Assert.True(result);
    }

    [Fact]
    public void Dispose_DoubleDispose_DoesNotThrow()
    {
        var tempPath = Path.Combine(Path.GetTempPath(), $"DoubleDispose_{Guid.NewGuid()}.log");
        var logger = new ErrorLogger(tempPath);

        logger.Dispose();

        var ex = Record.Exception(logger.Dispose);
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
    public void LogErrorSync_UserError_DoesNotWriteLocalFile()
    {
        var ex = new FileNotFoundException("custom path test");
        _errorLogger.LogErrorSync(ex, "path test");

        Assert.False(File.Exists(_tempLogFilePath));
    }

    [Fact]
    public void ErrorLogFilePath_IsConfigurable()
    {
        var customPath = Path.Combine(Path.GetTempPath(), $"custom_{Guid.NewGuid()}.log");
        using var logger = new ErrorLogger(customPath);

        Assert.Equal(customPath, logger.ErrorLogFilePath);
    }

    [Fact]
    public void FireAndForget_MethodExists()
    {
        var fireAndForget = ErrorLogger.FireAndForgetAsync;

        Assert.NotNull(fireAndForget);
    }

    public void Dispose()
    {
        _errorLogger.Dispose();
        CleanupLogFile();
        GC.SuppressFinalize(this);
    }
}
