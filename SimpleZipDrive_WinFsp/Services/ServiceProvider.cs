namespace SimpleZipDrive_WinFsp.Services;

public static class ServiceProvider
{
    private static readonly ConcurrentDictionary<Type, object> Services = new();

    public static void Register<T>(T implementation) where T : class
    {
        Services[typeof(T)] = implementation ?? throw new ArgumentNullException(nameof(implementation));
    }

    public static T Get<T>() where T : class
    {
        if (Services.TryGetValue(typeof(T), out var service))
        {
            return (T)service;
        }

        throw new InvalidOperationException($"Service {typeof(T).Name} is not registered.");
    }

    public static T? TryGet<T>() where T : class
    {
        if (Services.TryGetValue(typeof(T), out var service))
        {
            return (T)service;
        }

        return null;
    }

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
                catch (Exception ex)
                {
                    ErrorLoggerStatic.ReportSilentException(ex, $"ServiceProvider.DisposeAllServices: Error disposing {service.GetType().Name}", true);
                }
            }
        }

        Services.Clear();
    }
}
