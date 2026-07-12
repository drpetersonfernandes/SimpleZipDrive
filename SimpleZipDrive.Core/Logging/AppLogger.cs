using System.Globalization;
using Serilog;
using Serilog.Events;

namespace SimpleZipDrive.Core.Logging;

/// <summary>
/// Configures and owns the global Serilog pipeline for the application.
/// The pipeline writes to a rolling file (replacing the legacy debug_*.log mechanism),
/// the debugger output window, and forwards warning-and-above events to the bug report API
/// via <see cref="BugReportSink"/>.
/// </summary>
public static class AppLogger
{
    private const string OutputTemplate =
        "[{Timestamp:yyyy-MM-dd HH:mm:ss.fff} {Level:u3}] {Message:lj}{NewLine}{Exception}";

    private static int _initialized;

    /// <summary>Gets the directory that contains the rolling application log files.</summary>
    public static string LogDirectory { get; private set; } = GetDefaultLogDirectory();

    /// <summary>
    /// Initializes the global <see cref="Log.Logger"/> pipeline. Safe to call multiple times;
    /// only the first call has an effect.
    /// </summary>
    /// <param name="logDirectory">Directory for the rolling log files. Defaults to the app's Logs folder.</param>
    /// <param name="minimumLevel">The minimum level captured by the pipeline.</param>
    public static void Initialize(string? logDirectory = null, LogEventLevel minimumLevel = LogEventLevel.Debug)
    {
        if (Interlocked.Exchange(ref _initialized, 1) == 1)
            return;

        LogDirectory = logDirectory ?? GetDefaultLogDirectory();

        var configuration = new LoggerConfiguration()
            .MinimumLevel.Is(minimumLevel)
            .Enrich.WithProperty("Application", ErrorLogger.ApplicationName)
            .WriteTo.Debug(outputTemplate: OutputTemplate, formatProvider: CultureInfo.InvariantCulture)
            // Forward warning+ events to the remote bug report API (user errors filtered inside the sink).
            .WriteTo.Sink(new BugReportSink(), LogEventLevel.Warning);

        try
        {
            Directory.CreateDirectory(LogDirectory);
            configuration = configuration.WriteTo.File(
                Path.Combine(LogDirectory, "log-.txt"),
                rollingInterval: RollingInterval.Day,
                retainedFileCountLimit: 10,
                shared: true,
                outputTemplate: OutputTemplate,
                formatProvider: CultureInfo.InvariantCulture);
        }
        catch (Exception ex)
        {
            // The rolling file is best-effort (e.g. read-only install directory). The rest of the
            // pipeline (debug + API) still works without it.
            System.Diagnostics.Debug.WriteLine($"AppLogger: failed to configure file sink: {ex.Message}");
        }

        Log.Logger = configuration.CreateLogger();
    }

    /// <summary>
    /// Flushes and disposes the global Serilog pipeline. Call once during application shutdown.
    /// </summary>
    public static void CloseAndFlush()
    {
        Log.CloseAndFlush();
    }

    private static string GetDefaultLogDirectory()
    {
        return Path.Combine(AppSettings.SettingsDirectory, "Temp", "Logs");
    }
}
