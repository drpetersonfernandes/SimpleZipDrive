using SimpleZipDrive.Core.Services;

namespace SimpleZipDrive.Tests;

[Collection("ServiceProvider")]
public class ServiceProviderAdditionalTests : IDisposable
{
    public ServiceProviderAdditionalTests()
    {
        ServiceProvider.DisposeAllServices();
    }

    public void Dispose()
    {
        ServiceProvider.DisposeAllServices();
        GC.SuppressFinalize(this);
    }

    // ─── Concurrent Register/Get: thread safety ───

    [Fact]
    public void ConcurrentRegisterAndGet_DoesNotThrow()
    {
        var tasks = new List<Task>();
        for (var i = 0; i < 10; i++)
        {
            var index = i;
            tasks.Add(Task.Run(() =>
            {
                ServiceProvider.Register($"value_{index}");
                var result = ServiceProvider.TryGet<string>();
                // Result may be any of the registered values
                Assert.NotNull(result);
            }));
        }

        var ex = Record.Exception(() => Task.WaitAll(tasks.ToArray()));
        Assert.Null(ex);
    }

    // ─── Register/Get: multiple types ───

    [Fact]
    public void Register_MultipleTypes_Coexist()
    {
        ServiceProvider.Register("test string");
        ServiceProvider.Register(new object());

        Assert.Equal("test string", ServiceProvider.Get<string>());
        Assert.NotNull(ServiceProvider.Get<object>());
    }

    // ─── DisposeAllServices: non-disposable services cleared ───

    [Fact]
    public void DisposeAllServices_NonDisposable_Cleared()
    {
        ServiceProvider.Register("non-disposable");

        ServiceProvider.DisposeAllServices();

        Assert.Null(ServiceProvider.TryGet<string>());
    }

    // ─── DisposeAllServices: empty registry ───

    [Fact]
    public void DisposeAllServices_EmptyRegistry_DoesNotThrow()
    {
        var ex = Record.Exception(static () => ServiceProvider.DisposeAllServices());
        Assert.Null(ex);
    }

    // ─── Register: overwrites previous ───

    [Fact]
    public void Register_Overwrite_MultipleTimes()
    {
        ServiceProvider.Register("first");
        ServiceProvider.Register("second");
        ServiceProvider.Register("third");

        Assert.Equal("third", ServiceProvider.Get<string>());
    }

    // ─── Get: throws with type name in message ───

    [Fact]
    public void Get_Unregistered_ThrowsWithTypeName()
    {
        var ex = Assert.Throws<InvalidOperationException>(static () => ServiceProvider.Get<List<int>>());
        Assert.Contains("List", ex.Message);
    }

    // ─── Register/Get: object type ───

    [Fact]
    public void RegisterGetObject_WorksCorrectly()
    {
        var obj = new List<string> { "a", "b", "c" };
        ServiceProvider.Register(obj);

        var result = ServiceProvider.Get<List<string>>();
        Assert.Equal(3, result.Count);
        Assert.Same(obj, result);
    }

    // ─── TryGet: returns null for unregistered ───

    [Fact]
    public void TryGet_UnregisteredReferenceType_ReturnsNull()
    {
        Assert.Null(ServiceProvider.TryGet<List<string>>());
    }
}
