using System.Globalization;
using System.Text;

namespace SimpleZipDrive;

/// <summary>
/// Provides structured debug logging with thread-safe file output.
/// </summary>
public static class DiagnosticLogger
{
    private static readonly object Lock = new();
    internal static volatile bool _initialized;

    public static bool IsEnabled { get; private set; } = true;

    public static string? LogFilePath { get; private set; }

    /// <summary>
    /// Initializes the diagnostic logger with an optional directory and enabled flag.
    /// </summary>
    /// <param name="logDir">Directory for the log file. Defaults to the application base directory.</param>
    /// <param name="enabled">Whether logging is enabled.</param>
    public static void Initialize(string? logDir = null, bool enabled = true)
    {
        IsEnabled = enabled;
        if (!enabled) return;

        var dir = logDir ?? AppDomain.CurrentDomain.BaseDirectory;
        try
        {
            Directory.CreateDirectory(dir);
            LogFilePath = Path.Combine(dir, $"debug_{DateTime.Now:yyyyMMdd_HHmmss}.log");
            _initialized = true;
        }
        catch
        {
            _initialized = false;
        }
    }

    /// <summary>
    /// Writes a message to the diagnostic log.
    /// </summary>
    /// <param name="message">The message to log.</param>
    public static void Log(string message)
    {
        if (!_initialized || LogFilePath == null) return;

        var timestamp = DateTime.Now.ToString("HH:mm:ss.fff", CultureInfo.InvariantCulture);
        var threadId = Environment.CurrentManagedThreadId;
        var line = $"[{timestamp}][T{threadId}] {message}{Environment.NewLine}";

        try
        {
            lock (Lock)
            {
                File.AppendAllText(LogFilePath, line, Encoding.UTF8);
            }
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
