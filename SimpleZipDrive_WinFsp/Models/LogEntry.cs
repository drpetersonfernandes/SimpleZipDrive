namespace SimpleZipDrive_WinFsp.Models;

public class LogEntry
{
    public DateTime Timestamp { get; init; }
    public string Message { get; init; } = string.Empty;
    public bool IsError { get; init; }

    public override string ToString()
    {
        var prefix = IsError ? "[ERROR] " : string.Empty;
        return $"[{Timestamp:yyyy-MM-dd HH:mm:ss}] {prefix}{Message}";
    }
}
