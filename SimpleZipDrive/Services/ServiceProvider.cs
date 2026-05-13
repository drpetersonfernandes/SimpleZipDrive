namespace SimpleZipDrive.Services;

/// <summary>
/// Provides access to application services.
/// </summary>
public static class ServiceProvider
{
    private static readonly Dictionary<Type, object> Services = [];

    /// <summary>
    /// Registers a service instance.
    /// </summary>
    /// <typeparam name="T">The service interface type.</typeparam>
    /// <param name="implementation">The service implementation.</param>
    public static void Register<T>(T implementation) where T : class
    {
        Services[typeof(T)] = implementation ?? throw new ArgumentNullException(nameof(implementation));
    }

    /// <summary>
    /// Gets a registered service.
    /// </summary>
    /// <typeparam name="T">The service interface type.</typeparam>
    /// <returns>The service implementation.</returns>
    /// <exception cref="InvalidOperationException">Thrown when the service is not registered.</exception>
    public static T Get<T>() where T : class
    {
        if (Services.TryGetValue(typeof(T), out var service))
        {
            return (T)service;
        }

        throw new InvalidOperationException($"Service {typeof(T).Name} is not registered.");
    }

    /// <summary>
    /// Gets a registered service or null if not registered.
    /// </summary>
    /// <typeparam name="T">The service interface type.</typeparam>
    /// <returns>The service implementation or null.</returns>
    public static T? TryGet<T>() where T : class
    {
        if (Services.TryGetValue(typeof(T), out var service))
        {
            return (T)service;
        }

        return null;
    }

    /// <summary>
    /// Disposes all registered services that implement IDisposable.
    /// </summary>
    public static void DisposeAllServices()
    {
        foreach (var service in Services.Values)
        {
            if (service is IDisposable disposable)
            {
                try
                {
                    disposable.Dispose();
                }
                catch
                {
                    // Ignore disposal errors during shutdown
                }
            }
        }

        Services.Clear();
    }
}
