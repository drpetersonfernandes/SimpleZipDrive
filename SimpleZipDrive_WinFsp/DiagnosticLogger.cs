using System.Globalization;
using System.Text;

namespace SimpleZipDrive_WinFsp;

public static class DiagnosticLogger
{
    private static readonly object Lock = new();
    private static volatile bool _initialized;

    public static bool IsEnabled { get; private set; } = true;

    public static string? LogFilePath { get; private set; }

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

    public static void Log(Exception ex, string context)
    {
        Log($"{context}: {ex.GetType().Name}: {ex.Message}");
        if (!string.IsNullOrEmpty(ex.StackTrace))
            Log($"  Stack: {ex.StackTrace.Replace(Environment.NewLine, Environment.NewLine + "          ")}");
    }

    public static void LogOperation(string operation, string path, int status, string? detail = null)
    {
        var statusName = status == 0 ? "SUCCESS" : $"0x{status:X8}";
        var detailStr = detail != null ? $" [{detail}]" : "";
        Log($"  {operation}: \"{path}\" → {statusName}{detailStr}");
    }

    public static void LogOperation(string operation, string path, bool result, string? detail = null)
    {
        var resultStr = result ? "true" : "false";
        var detailStr = detail != null ? $" [{detail}]" : "";
        Log($"  {operation}: \"{path}\" → {resultStr}{detailStr}");
    }

    public static void LogSection(string title)
    {
        Log(new string('=', 80));
        Log($"  {title}");
        Log(new string('=', 80));
    }

    public static void LogHeader(string text)
    {
        Log($"--- {text} ---");
    }
}
