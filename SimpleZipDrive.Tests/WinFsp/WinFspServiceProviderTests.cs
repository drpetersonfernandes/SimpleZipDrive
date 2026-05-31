using SimpleZipDrive.Core.Services;

namespace SimpleZipDrive.Tests.WinFsp;

[Collection("ServiceProvider")]
public class WinFspServiceProviderTests : IDisposable
{
    public WinFspServiceProviderTests()
    {
        ServiceProvider.DisposeAllServices();
    }

    public void Dispose()
    {
        ServiceProvider.DisposeAllServices();
        GC.SuppressFinalize(this);
    }

    [Fact]
    public void Register_ValidImplementation_Succeeds()
    {
        const string service = "test service";
        ServiceProvider.Register(service);

        var result = ServiceProvider.Get<string>();
        Assert.Equal("test service", result);
    }

    [Fact]
    public void Register_NullImplementation_ThrowsArgumentNullException()
    {
        Assert.Throws<ArgumentNullException>(static () => ServiceProvider.Register<string>(null!));
    }

    [Fact]
    public void Register_SameTypeTwice_OverwritesPrevious()
    {
        ServiceProvider.Register("first");
        ServiceProvider.Register("second");

        var result = ServiceProvider.Get<string>();
        Assert.Equal("second", result);
    }

    [Fact]
    public void Get_RegisteredService_ReturnsInstance()
    {
        var expected = new object();
        ServiceProvider.Register(expected);

        var result = ServiceProvider.Get<object>();
        Assert.Same(expected, result);
    }

    [Fact]
    public void Get_UnregisteredService_ThrowsInvalidOperationException()
    {
        var ex = Assert.Throws<InvalidOperationException>(static () => ServiceProvider.Get<WinFspFakeDisposable>());
        Assert.Contains("WinFspFakeDisposable", ex.Message);
    }

    [Fact]
    public void TryGet_RegisteredService_ReturnsInstance()
    {
        const string expected = "expected value";
        ServiceProvider.Register(expected);

        var result = ServiceProvider.TryGet<string>();
        Assert.Equal("expected value", result);
    }

    [Fact]
    public void TryGet_UnregisteredService_ReturnsNull()
    {
        var result = ServiceProvider.TryGet<object>();
        Assert.Null(result);
    }

    [Fact]
    public void DisposeAllServices_DisposableServices_DisposesAndClears()
    {
        var disposable = new WinFspFakeDisposable();
        ServiceProvider.Register(disposable);
        ServiceProvider.Register("non-disposable");

        ServiceProvider.DisposeAllServices();

        Assert.True(disposable.WasDisposed);
        Assert.Null(ServiceProvider.TryGet<WinFspFakeDisposable>());
        Assert.Null(ServiceProvider.TryGet<string>());
    }

    [Fact]
    public void DisposeAllServices_DisposeException_DoesNotThrow()
    {
        var throwing = new WinFspThrowingDisposable();
        ServiceProvider.Register(throwing);
        ServiceProvider.Register("works");

        var ex = Record.Exception(static () => ServiceProvider.DisposeAllServices());

        Assert.Null(ex);
        Assert.Null(ServiceProvider.TryGet<WinFspThrowingDisposable>());
        Assert.Null(ServiceProvider.TryGet<string>());
    }

    [Fact]
    public void DisposeAllServices_ClearsAllRegistrations()
    {
        ServiceProvider.Register("first");
        ServiceProvider.Register("second");
        ServiceProvider.Register(new object());

        ServiceProvider.DisposeAllServices();

        Assert.Null(ServiceProvider.TryGet<string>());
        Assert.Null(ServiceProvider.TryGet<object>());
    }

    private class WinFspFakeDisposable : IDisposable
    {
        public bool WasDisposed { get; private set; }

        public void Dispose()
        {
            WasDisposed = true;
        }
    }

    private class WinFspThrowingDisposable : IDisposable
    {
        public void Dispose()
        {
            throw new InvalidOperationException("Dispose intentionally fails");
        }
    }
}
