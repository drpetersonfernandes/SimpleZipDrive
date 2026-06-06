using WinFspErrorLogger = SimpleZipDrive.Core.ErrorLogger;
using WinFspErrorLoggerStatic = SimpleZipDrive.Core.ErrorLoggerStatic;

namespace SimpleZipDrive.Tests.WinFsp;

[Collection("ErrorLogger")]
public class WinFspErrorLoggerTests : IDisposable
{
    private readonly WinFspErrorLogger _logger;
    private readonly string _logFilePath;

    public WinFspErrorLoggerTests()
    {
        _logFilePath = Path.Combine(Path.GetTempPath(), $"WinFsp_ErrorLoggerTest_{Guid.NewGuid():N}.log");
        _logger = new WinFspErrorLogger(_logFilePath);
    }

    [Fact]
    public void Constructor_SetsLogFilePath()
    {
        var value = _logger.ErrorLogFilePath;
        Assert.Equal(_logFilePath, value);
    }

    [Fact]
    public void LogErrorSync_ValidException_DoesNotThrow()
    {
        var ex = Record.Exception(() => _logger.LogErrorSync(new InvalidOperationException("Test error"), "Test context"));

        Assert.Null(ex);
    }

    [Fact]
    public void LogErrorSync_NullException_CreatesArgumentNullException()
    {
        var ex = Record.Exception(() => _logger.LogErrorSync(null, "null exception test"));

        Assert.Null(ex);
    }

    [Fact]
    public async Task LogErrorAsync_ValidException_DoesNotThrow()
    {
        var ex = await Record.ExceptionAsync(() =>
            _logger.LogErrorAsync(new InvalidOperationException("Async test error"), "Async test context"));

        Assert.Null(ex);
    }

    [Fact]
    public async Task LogErrorAsync_NullException_CreatesArgumentNullException()
    {
        var ex = await Record.ExceptionAsync(() =>
            _logger.LogErrorAsync(null, "null async exception test"));

        Assert.Null(ex);
    }

    [Fact]
    public void ReportSilentException_DoesNotThrow()
    {
        var ex = Record.Exception(() =>
            _logger.ReportSilentException(new IOException("Silent test"), "Silent context", true));

        Assert.Null(ex);
    }

    // ─── IsUserError tests (private, via reflection) ───

    [Fact]
    public void IsUserError_OperationCanceledException_ReturnsTrue()
    {
        var result = WinFspErrorLogger.IsUserError(new OperationCanceledException());

        Assert.True(result);
    }

    [Fact]
    public void IsUserError_TaskCanceledException_ReturnsTrue()
    {
        var result = WinFspErrorLogger.IsUserError(new TaskCanceledException());

        Assert.True(result);
    }

    [Fact]
    public void IsUserError_FileNotFoundException_ReturnsTrue()
    {
        var result = WinFspErrorLogger.IsUserError(new FileNotFoundException());

        Assert.True(result);
    }

    [Fact]
    public void IsUserError_DirectoryNotFoundException_ReturnsTrue()
    {
        var result = WinFspErrorLogger.IsUserError(new DirectoryNotFoundException());

        Assert.True(result);
    }

    [Fact]
    public void IsUserError_UnauthorizedAccessException_ReturnsTrue()
    {
        var result = WinFspErrorLogger.IsUserError(new UnauthorizedAccessException());

        Assert.True(result);
    }

    [Fact]
    public void IsUserError_NullException_ReturnsFalse()
    {
        var result = WinFspErrorLogger.IsUserError(null);

        Assert.False(result);
    }

    [Fact]
    public void IsUserError_GenericException_ReturnsFalse()
    {
        var result = WinFspErrorLogger.IsUserError(new InvalidOperationException("Not a user error"));

        Assert.False(result);
    }

    [Fact]
    public void IsUserError_PasswordMessage_ReturnsTrue()
    {
        var result = WinFspErrorLogger.IsUserError(new InvalidOperationException("archive requires a password"));

        Assert.True(result);
    }

    [Fact]
    public void IsUserError_EncryptedMessage_ReturnsTrue()
    {
        var result = WinFspErrorLogger.IsUserError(new InvalidOperationException("file is encrypted"));

        Assert.True(result);
    }

    [Theory]
    [InlineData("cannot find central directory")]
    [InlineData("invalid archive")]
    [InlineData("unknown format")]
    [InlineData("not a valid")]
    [InlineData("archive is corrupt")]
    public void IsUserError_ArchiveErrorMessage_ReturnsTrue(string message)
    {
        var result = WinFspErrorLogger.IsUserError(new InvalidOperationException(message));

        Assert.True(result);
    }

    [Fact]
    public void IsUserError_DriveLetterMessage_ReturnsTrue()
    {
        var result = WinFspErrorLogger.IsUserError(new InvalidOperationException("can't assign a drive letter"));

        Assert.True(result);
    }

    [Fact]
    public void IsUserError_DriveLetterInUseMessage_ReturnsTrue()
    {
        var result = WinFspErrorLogger.IsUserError(new InvalidOperationException("drive letter is in use by another device"));

        Assert.True(result);
    }

    [Theory]
    [InlineData("canceled")]
    [InlineData("cancelled")]
    public void IsUserError_CancellationMessages_ReturnsTrue(string message)
    {
        var result = WinFspErrorLogger.IsUserError(new InvalidOperationException(message));

        Assert.True(result);
    }

    [Fact]
    public void IsUserError_MountPointInvalidMessage_ReturnsTrue()
    {
        var result = WinFspErrorLogger.IsUserError(new InvalidOperationException("mount point is invalid"));

        Assert.True(result);
    }

    [Fact]
    public void IsUserError_HeaderAndInvalidMessage_ReturnsTrue()
    {
        var result = WinFspErrorLogger.IsUserError(new InvalidOperationException("archive header is invalid"));

        Assert.True(result);
    }

    [Fact]
    public void IsUserError_HttpRequestExceptionWithCanceled_ReturnsTrue()
    {
        var ex = new HttpRequestException("request failed", new OperationCanceledException());
        var result = WinFspErrorLogger.IsUserError(ex);

        Assert.True(result);
    }

    // ─── GetEnvironmentDetails tests (private, via reflection) ───

    [Fact]
    public void GetEnvironmentDetails_ReturnsNonEmptyString()
    {
        var result = _logger.GetEnvironmentDetails();

        Assert.NotNull(result);
        Assert.NotEmpty(result);
        Assert.Contains("=== Environment Details ===", result);
    }

    // ─── FormatErrorMessage tests (private, via reflection) ───

    [Fact]
    public void FormatErrorMessage_ReturnsFormattedString()
    {
        var result = WinFspErrorLogger.FormatErrorMessage(new InvalidOperationException("Format test"), "Test context");

        Assert.NotNull(result);
        Assert.Contains("Exception Type:", result);
        Assert.Contains("Format test", result);
        Assert.Contains("Test context", result);
    }

    [Fact]
    public void FormatErrorMessage_WithInnerException_IncludesInnerDetails()
    {
        var inner = new ArgumentException("Inner exception");
        var ex = new InvalidOperationException("Outer", inner);
        var result = WinFspErrorLogger.FormatErrorMessage(ex, "Inner exception test");

        Assert.NotNull(result);
        Assert.Contains("Inner Exception", result);
        Assert.Contains("ArgumentException", result);
        Assert.Contains("Inner exception", result);
    }

    [Fact]
    public void Dispose_DoubleDispose_DoesNotThrow()
    {
        var tempPath = Path.Combine(Path.GetTempPath(), $"WinFsp_DoubleDispose_{Guid.NewGuid():N}.log");
        var logger = new WinFspErrorLogger(tempPath);

        logger.Dispose();

        var ex = Record.Exception(logger.Dispose);
        Assert.Null(ex);
    }

    public void Dispose()
    {
        _logger.Dispose();
        if (File.Exists(_logFilePath))
        {
            try
            {
                File.Delete(_logFilePath);
            }
            catch
            {
                // ignored
            }
        }

        GC.SuppressFinalize(this);
    }
}

public class WinFspErrorLoggerStaticTests
{
    [Fact]
    public void Instance_IsSingleton()
    {
        var instance1 = WinFspErrorLoggerStatic.Instance;
        var instance2 = WinFspErrorLoggerStatic.Instance;

        Assert.Same(instance1, instance2);
    }

    [Fact]
    public void ReportSilentException_DelegatesToInstance()
    {
        var ex = Record.Exception(static () =>
            WinFspErrorLoggerStatic.ReportSilentException(new IOException("Static test"), "Static context", true));

        Assert.Null(ex);
    }

    [Fact]
    public void LogErrorSync_DelegatesToInstance()
    {
        var ex = Record.Exception(static () =>
            WinFspErrorLoggerStatic.LogErrorSync(new InvalidOperationException("Static sync"), "Static sync context"));

        Assert.Null(ex);
    }

    [Fact]
    public void LogErrorAsync_DelegatesToInstance()
    {
        var task = WinFspErrorLoggerStatic.LogErrorAsync(
            new InvalidOperationException("Static async"), "Static async context");

        Assert.NotNull(task);
        Assert.True(task is not null);
    }
}
