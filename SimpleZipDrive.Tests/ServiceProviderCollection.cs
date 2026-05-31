namespace SimpleZipDrive.Tests;

public class ServiceProviderFixture : IDisposable
{
    public void Dispose()
    {
        // Ensure shared static state is cleaned up after each collection of tests
        Core.Services.ServiceProvider.DisposeAllServices();
        GC.SuppressFinalize(this);
    }
}

[CollectionDefinition("ServiceProvider")]
public class ServiceProviderTestGroup : ICollectionFixture<ServiceProviderFixture>
{
    // This class has no code; it exists solely to define the collection name
    // so that tests touching shared static ServiceProvider state do not run in parallel.
}
