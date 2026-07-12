using System.Globalization;
using SimpleZipDrive.Core.Logging;

namespace SimpleZipDrive.Core;

/// <summary>
/// Convenience facade for writing structured diagnostic trace to the single global Serilog pipeline
/// owned by <see cref="AppLogger"/>. Diagnostic lines are emitted at <see cref="Serilog.Events.LogEventLevel.Debug"/>
/// and carry their own timestamp and thread id so the per-session trace remains readable.
/// </summary>
public static class DiagnosticLogger
{
    /// <summary>Gets a value indicating whether diagnostic logging is enabled.</summary>
    public static bool IsEnabled { get; internal set; }

    /// <summary>Gets a value indicating whether the underlying pipeline file sink was configured.</summary>
    public static bool Initialized { get; internal set; }

    /// <summary>Gets the file path of the current diagnostic log, or <see langword="null"/> if not initialized.</summary>
    public static string? LogFilePath { get; internal set; }

    /// <summary>
    /// Deletes all pre-existing log files (debug_*.log and error.log) from the log directory.
    /// </summary>
    /// <param name="logDir">Directory to clean. Defaults to the app's Logs folder.</param>
    public static void CleanupOldLogs(string? logDir = null)
    {
        var dir = logDir ?? GetDefaultLogDirectory();
        try
        {
            foreach (var file in Directory.GetFiles(dir, "debug_*.log"))
            {
                try
                {
                    File.Delete(file);
                }
                catch
                {
                    /* ignored */
                }
            }

            var errorLog = Path.Combine(dir, "error.log");
            if (File.Exists(errorLog))
            {
                try
                {
                    File.Delete(errorLog);
                }
                catch
                {
                    /* ignored */
                }
            }
        }
        catch
        {
            // ignored
        }
    }

    private static string GetDefaultLogDirectory()
    {
        return Path.Combine(AppSettings.SettingsDirectory, "Temp", "Logs");
    }

    /// <summary>
    /// Enables diagnostic logging and ensures the shared <see cref="AppLogger"/> pipeline is initialized.
    /// </summary>
    /// <param name="logDir">Directory for the log file. Defaults to the app's Logs folder.</param>
    /// <param name="enabled">Whether diagnostic logging is enabled.</param>
    public static void Initialize(string? logDir = null, bool enabled = true)
    {
        IsEnabled = enabled;
        if (!enabled)
        {
            Initialized = false;
            LogFilePath = null;
            return;
        }

        AppLogger.Initialize(logDir);
        LogFilePath = AppLogger.LogFilePath;
        Initialized = AppLogger.IsInitialized;
    }

    /// <summary>
    /// Flushes and closes the underlying pipeline. Call during application shutdown.
    /// </summary>
    public static void Close()
    {
        AppLogger.CloseAndFlush();
    }

    /// <summary>
    /// Writes a message to the diagnostic log.
    /// </summary>
    /// <param name="message">The message to log.</param>
    public static void Log(string message)
    {
        if (!IsEnabled) return;

        var timestamp = DateTime.Now.ToString("HH:mm:ss.fff", CultureInfo.InvariantCulture);
        var threadId = Environment.CurrentManagedThreadId;
        var line = $"[{timestamp}][T{threadId}] {message}";

        try
        {
            Serilog.Log.Debug("{DiagnosticLine}", line);
        }
        catch
        {
            // ignored
        }
    }

    /// <summary>
    /// Logs an exception with context.
    /// </summary>
    /// <param name="ex">The exception to log.</param>
    /// <param name="context">Contextual description of the exception.</param>
    public static void Log(Exception ex, string context)
    {
        Log($"{context}: {ex.GetType().Name}: {ex.Message}");
        if (!string.IsNullOrEmpty(ex.StackTrace))
            Log($"  Stack: {ex.StackTrace.Replace(Environment.NewLine, Environment.NewLine + "          ")}");
    }

    /// <summary>
    /// Logs the result of an operation with an integer status code.
    /// </summary>
    /// <param name="operation">The operation name.</param>
    /// <param name="path">The path involved.</param>
    /// <param name="status">The status code.</param>
    /// <param name="detail">Optional detail string.</param>
    public static void LogOperation(string operation, string path, int status, string? detail = null)
    {
        var statusName = status == 0 ? "SUCCESS" : $"0x{status:X8}";
        var detailStr = detail != null ? $" [{detail}]" : "";
        Log($"  {operation}: \"{path}\" → {statusName}{detailStr}");
    }

    /// <summary>
    /// Logs the result of an operation with a boolean result.
    /// </summary>
    /// <param name="operation">The operation name.</param>
    /// <param name="path">The path involved.</param>
    /// <param name="result">The boolean result.</param>
    /// <param name="detail">Optional detail string.</param>
    public static void LogOperation(string operation, string path, bool result, string? detail = null)
    {
        var resultStr = result ? "true" : "false";
        var detailStr = detail != null ? $" [{detail}]" : "";
        Log($"  {operation}: \"{path}\" → {resultStr}{detailStr}");
    }

    /// <summary>
    /// Logs a section header.
    /// </summary>
    /// <param name="title">The section title.</param>
    public static void LogSection(string title)
    {
        Log(new string('=', 80));
        Log($"  {title}");
        Log(new string('=', 80));
    }

    /// <summary>
    /// Logs a header line.
    /// </summary>
    /// <param name="text">The header text.</param>
    public static void LogHeader(string text)
    {
        Log($"--- {text} ---");
    }
}
