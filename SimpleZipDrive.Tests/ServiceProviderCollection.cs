namespace SimpleZipDrive.Tests;

[CollectionDefinition("ServiceProvider")]
public class ServiceProviderCollection : ICollectionFixture<IDisposable>
{
    // This class has no code; it exists solely to define the collection name
    // so that tests touching shared static ServiceProvider state do not run in parallel.
}
