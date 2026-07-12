using System.Globalization;
using Serilog;
using Serilog.Events;

namespace SimpleZipDrive.Core.Logging;

/// <summary>
/// Configures and owns the single global Serilog pipeline for the application.
/// The pipeline writes to one per-session file, mirrors to the debugger output window, and forwards
/// warning-and-above events to the bug report API via <see cref="BugReportSink"/>.
/// <see cref="DiagnosticLogger"/> and the logging services all funnel into this one pipeline.
/// </summary>
public static class AppLogger
{
    // No outer timestamp: callers that want one (e.g. DiagnosticLogger) embed it in the message.
    private const string OutputTemplate = "[{Level:u3}] {Message:lj}{NewLine}{Exception}";

    private static readonly object Lock = new();

    /// <summary>Gets the directory that contains the per-session log file.</summary>
    public static string LogDirectory { get; private set; } = GetDefaultLogDirectory();

    /// <summary>Gets the path of the current session's log file, or <see langword="null"/> when unavailable.</summary>
    public static string? LogFilePath { get; private set; }

    /// <summary>Gets a value indicating whether the file sink was successfully configured.</summary>
    public static bool IsInitialized { get; private set; }

    /// <summary>
    /// Initializes (or re-initializes) the global <see cref="Log.Logger"/> pipeline. Any previously
    /// configured pipeline is flushed and replaced.
    /// </summary>
    /// <param name="logDirectory">Directory for the per-session log file. Defaults to the app's Logs folder.</param>
    /// <param name="minimumLevel">The minimum level captured by the pipeline.</param>
    public static void Initialize(string? logDirectory = null, LogEventLevel minimumLevel = LogEventLevel.Verbose)
    {
        lock (Lock)
        {
            // Re-configuring: tear down any previous pipeline first so the old file handle is released.
            Log.CloseAndFlush();

            LogDirectory = logDirectory ?? GetDefaultLogDirectory();

            var configuration = new LoggerConfiguration()
                .MinimumLevel.Is(minimumLevel)
                .Enrich.WithProperty("Application", ErrorLogger.ApplicationName)
                .WriteTo.Debug(
                    LogEventLevel.Information,
                    OutputTemplate,
                    CultureInfo.InvariantCulture)
                // Forward warning+ events to the remote bug report API (user errors filtered inside the sink).
                .WriteTo.Sink(new BugReportSink(), LogEventLevel.Warning);

            string? logFilePath;
            try
            {
                // Throws if 'LogDirectory' is actually an existing file or otherwise invalid.
                Directory.CreateDirectory(LogDirectory);
                logFilePath = Path.Combine(LogDirectory, $"debug_{DateTime.Now:yyyyMMdd_HHmmss}_{Guid.NewGuid():N}.log");
                configuration = configuration.WriteTo.File(
                    logFilePath,
                    outputTemplate: OutputTemplate,
                    formatProvider: CultureInfo.InvariantCulture,
                    buffered: false,
                    shared: false);
            }
            catch (Exception ex)
            {
                // The file sink is best-effort (e.g. read-only install directory). The rest of the
                // pipeline (debug + API) still works without it.
                logFilePath = null;
                System.Diagnostics.Debug.WriteLine($"AppLogger: failed to configure file sink: {ex.Message}");
            }

            Log.Logger = configuration.CreateLogger();
            LogFilePath = logFilePath;
            IsInitialized = logFilePath != null;
        }
    }

    /// <summary>
    /// Flushes and disposes the global Serilog pipeline. Call once during application shutdown.
    /// </summary>
    public static void CloseAndFlush()
    {
        lock (Lock)
        {
            Log.CloseAndFlush();
            IsInitialized = false;
            LogFilePath = null;
        }
    }

    private static string GetDefaultLogDirectory()
    {
        return Path.Combine(AppSettings.SettingsDirectory, "Temp", "Logs");
    }
}
