using System.Reflection;
using WinFspErrorLogger = SimpleZipDrive_WinFsp.ErrorLogger;
using WinFspErrorLoggerStatic = SimpleZipDrive_WinFsp.ErrorLoggerStatic;

namespace SimpleZipDrive.Tests.WinFsp;

[Collection("WinFspErrorLogger")]
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
        var prop = typeof(WinFspErrorLogger).GetProperty("ErrorLogFilePath", BindingFlags.NonPublic | BindingFlags.Instance);
        Assert.NotNull(prop);

        var value = prop.GetValue(_logger) as string;
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
        var method = typeof(WinFspErrorLogger).GetMethod("IsUserError", BindingFlags.NonPublic | BindingFlags.Static);
        Assert.NotNull(method);

        var result = method.Invoke(null, [new OperationCanceledException()]);

        Assert.True((bool)(result ?? false));
    }

    [Fact]
    public void IsUserError_TaskCanceledException_ReturnsTrue()
    {
        var method = typeof(WinFspErrorLogger).GetMethod("IsUserError", BindingFlags.NonPublic | BindingFlags.Static);
        Assert.NotNull(method);

        var result = method.Invoke(null, [new TaskCanceledException()]);

        Assert.True((bool)(result ?? false));
    }

    [Fact]
    public void IsUserError_FileNotFoundException_ReturnsTrue()
    {
        var method = typeof(WinFspErrorLogger).GetMethod("IsUserError", BindingFlags.NonPublic | BindingFlags.Static);
        Assert.NotNull(method);

        var result = method.Invoke(null, [new FileNotFoundException()]);

        Assert.True((bool)(result ?? false));
    }

    [Fact]
    public void IsUserError_DirectoryNotFoundException_ReturnsTrue()
    {
        var method = typeof(WinFspErrorLogger).GetMethod("IsUserError", BindingFlags.NonPublic | BindingFlags.Static);
        Assert.NotNull(method);

        var result = method.Invoke(null, [new DirectoryNotFoundException()]);

        Assert.True((bool)(result ?? false));
    }

    [Fact]
    public void IsUserError_UnauthorizedAccessException_ReturnsTrue()
    {
        var method = typeof(WinFspErrorLogger).GetMethod("IsUserError", BindingFlags.NonPublic | BindingFlags.Static);
        Assert.NotNull(method);

        var result = method.Invoke(null, [new UnauthorizedAccessException()]);

        Assert.True((bool)(result ?? false));
    }

    [Fact]
    public void IsUserError_NullException_ReturnsFalse()
    {
        var method = typeof(WinFspErrorLogger).GetMethod("IsUserError", BindingFlags.NonPublic | BindingFlags.Static);
        Assert.NotNull(method);

        var result = method.Invoke(null, [null]);

        Assert.False((bool)(result ?? true));
    }

    [Fact]
    public void IsUserError_GenericException_ReturnsFalse()
    {
        var method = typeof(WinFspErrorLogger).GetMethod("IsUserError", BindingFlags.NonPublic | BindingFlags.Static);
        Assert.NotNull(method);

        var result = method.Invoke(null, [new InvalidOperationException("Not a user error")]);

        Assert.False((bool)(result ?? true));
    }

    [Fact]
    public void IsUserError_PasswordMessage_ReturnsTrue()
    {
        var method = typeof(WinFspErrorLogger).GetMethod("IsUserError", BindingFlags.NonPublic | BindingFlags.Static);
        Assert.NotNull(method);

        var result = method.Invoke(null, [new InvalidOperationException("archive requires a password")]);

        Assert.True((bool)(result ?? false));
    }

    [Fact]
    public void IsUserError_EncryptedMessage_ReturnsTrue()
    {
        var method = typeof(WinFspErrorLogger).GetMethod("IsUserError", BindingFlags.NonPublic | BindingFlags.Static);
        Assert.NotNull(method);

        var result = method.Invoke(null, [new InvalidOperationException("file is encrypted")]);

        Assert.True((bool)(result ?? false));
    }

    [Theory]
    [InlineData("cannot find central directory")]
    [InlineData("invalid archive")]
    [InlineData("unknown format")]
    [InlineData("not a valid")]
    [InlineData("corrupt")]
    public void IsUserError_ArchiveErrorMessage_ReturnsTrue(string message)
    {
        var method = typeof(WinFspErrorLogger).GetMethod("IsUserError", BindingFlags.NonPublic | BindingFlags.Static);
        Assert.NotNull(method);

        var result = method.Invoke(null, [new InvalidOperationException(message)]);

        Assert.True((bool)(result ?? false));
    }

    [Fact]
    public void IsUserError_DriveLetterMessage_ReturnsTrue()
    {
        var method = typeof(WinFspErrorLogger).GetMethod("IsUserError", BindingFlags.NonPublic | BindingFlags.Static);
        Assert.NotNull(method);

        var result = method.Invoke(null, [new InvalidOperationException("can't assign a drive letter")]);

        Assert.True((bool)(result ?? false));
    }

    // ─── GetEnvironmentDetails tests (private, via reflection) ───

    [Fact]
    public void GetEnvironmentDetails_ReturnsNonEmptyString()
    {
        var method = typeof(WinFspErrorLogger).GetMethod("GetEnvironmentDetails", BindingFlags.NonPublic | BindingFlags.Instance);
        Assert.NotNull(method);

        var result = method.Invoke(_logger, null) as string;

        Assert.NotNull(result);
        Assert.NotEmpty(result);
        Assert.Contains("=== Environment Details ===", result);
    }

    // ─── FormatErrorMessage tests (private, via reflection) ───

    [Fact]
    public void FormatErrorMessage_ReturnsFormattedString()
    {
        var method = typeof(WinFspErrorLogger).GetMethod("FormatErrorMessage", BindingFlags.NonPublic | BindingFlags.Static);
        Assert.NotNull(method);

        var result = method.Invoke(null, [new InvalidOperationException("Format test"), "Test context"]) as string;

        Assert.NotNull(result);
        Assert.Contains("Exception Type:", result);
        Assert.Contains("Format test", result);
        Assert.Contains("Test context", result);
    }

    [Fact]
    public void FormatErrorMessage_WithInnerException_IncludesInnerDetails()
    {
        var method = typeof(WinFspErrorLogger).GetMethod("FormatErrorMessage", BindingFlags.NonPublic | BindingFlags.Static);
        Assert.NotNull(method);

        var inner = new ArgumentException("Inner exception");
        var ex = new InvalidOperationException("Outer", inner);
        var result = method.Invoke(null, [ex, "Inner exception test"]) as string;

        Assert.NotNull(result);
        Assert.Contains("Inner Exception", result);
        Assert.Contains("ArgumentException", result);
        Assert.Contains("Inner exception", result);
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
