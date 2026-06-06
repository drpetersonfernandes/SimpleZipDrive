using SimpleZipDrive.Core;

namespace SimpleZipDrive.Tests;

public class ErrorLoggerStaticAdditionalTests
{
    // ─── ReportSilentException: non-silent mode ───

    [Fact]
    public void ReportSilentException_NonSilent_DoesNotThrow()
    {
        var ex = new InvalidOperationException("test non-silent");

        var thrownEx = Record.Exception(() =>
            ErrorLoggerStatic.ReportSilentException(ex, "test context"));

        Assert.Null(thrownEx);
    }

    // ─── ReportSilentException: silent mode ───

    [Fact]
    public void ReportSilentException_Silent_DoesNotThrow()
    {
        var ex = new InvalidOperationException("test silent");

        var thrownEx = Record.Exception(() =>
            ErrorLoggerStatic.ReportSilentException(ex, "test context", true));

        Assert.Null(thrownEx);
    }

    // ─── LogErrorSync: with context ───

    [Fact]
    public void LogErrorSync_WithContext_DoesNotThrow()
    {
        var ex = new IOException("test io");

        var thrownEx = Record.Exception(() =>
            ErrorLoggerStatic.LogErrorSync(ex, "detailed context"));

        Assert.Null(thrownEx);
    }

    // ─── LogErrorAsync: with context ───

    [Fact]
    public async Task LogErrorAsync_WithContext_DoesNotThrow()
    {
        var ex = new IOException("test async io");

        var thrownEx = await Record.ExceptionAsync(() =>
            ErrorLoggerStatic.LogErrorAsync(ex, "async context"));

        Assert.Null(thrownEx);
    }

    // ─── ApplicationName: can be set ───

    [Fact]
    public void ApplicationName_CanBeSet()
    {
        var original = ErrorLogger.ApplicationName;
        try
        {
            ErrorLogger.ApplicationName = "TestApp";
            Assert.Equal("TestApp", ErrorLogger.ApplicationName);
        }
        finally
        {
            ErrorLogger.ApplicationName = original;
        }
    }

    // ─── SuppressApiCalls: toggle ───

    [Fact]
    public void SuppressApiCalls_CanBeToggled()
    {
        var original = ErrorLogger.SuppressApiCalls;
        try
        {
            ErrorLogger.SuppressApiCalls = true;
            Assert.True(ErrorLogger.SuppressApiCalls);

            ErrorLogger.SuppressApiCalls = false;
            Assert.False(ErrorLogger.SuppressApiCalls);
        }
        finally
        {
            ErrorLogger.SuppressApiCalls = original;
        }
    }
}
