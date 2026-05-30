namespace SimpleZipDrive.Tests;

public class ErrorLoggerStaticTests
{
    [Fact]
    public void InstanceIsCreatedOnAccess()
    {
        var instance = ErrorLoggerStatic.Instance;

        Assert.NotNull(instance);
        Assert.IsType<ErrorLogger>(instance);
    }

    [Fact]
    public void InstanceIsSingleton()
    {
        var first = ErrorLoggerStatic.Instance;
        var second = ErrorLoggerStatic.Instance;

        Assert.Same(first, second);
    }

    [Fact]
    public void ReportSilentExceptionDelegatesToInstance()
    {
        var ex = new InvalidOperationException("test silent exception");

        var thrownEx = Record.Exception(() =>
            ErrorLoggerStatic.ReportSilentException(ex, "test context", true));

        Assert.Null(thrownEx);
    }

    [Fact]
    public void LogErrorSyncDelegatesToInstanceWithNullException()
    {
        var thrownEx = Record.Exception(static () =>
            ErrorLoggerStatic.LogErrorSync(null, "test null"));

        Assert.Null(thrownEx);
    }

    [Fact]
    public void LogErrorSyncDelegatesToInstanceWithException()
    {
        var ex = new IOException("test io error");

        var thrownEx = Record.Exception(() =>
            ErrorLoggerStatic.LogErrorSync(ex, "test context"));

        Assert.Null(thrownEx);
    }

    [Fact]
    public async Task LogErrorAsyncDelegatesToInstanceAsync()
    {
        var ex = new InvalidOperationException("test async log");

        var thrownEx = await Record.ExceptionAsync(() => ErrorLoggerStatic.LogErrorAsync(ex, "test async context"));

        Assert.Null(thrownEx);
    }

    [Fact]
    public async Task LogErrorAsyncDelegatesToInstanceWithNullExceptionAsync()
    {
        var thrownEx = await Record.ExceptionAsync(static () => ErrorLoggerStatic.LogErrorAsync(null, "test async null"));

        Assert.Null(thrownEx);
    }

    [Fact]
    public void InitializeGlobalExceptionHandlersThrowsWithoutWpfContext()
    {
        var ex = Record.Exception(static () => ErrorLoggerStatic.InitializeGlobalExceptionHandlers());

        Assert.IsType<NullReferenceException>(ex);
    }
}
