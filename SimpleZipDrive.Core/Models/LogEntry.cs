namespace SimpleZipDrive.Core.Models;

/// <summary>
/// Represents a single log message displayed in the application's log panel.
/// </summary>
public class LogEntry
{
    /// <summary>Gets the timestamp when the log entry was created.</summary>
    public DateTime Timestamp { get; init; }

    /// <summary>Gets the log message text.</summary>
    public string Message { get; init; } = string.Empty;

    /// <summary>Gets a value indicating whether this entry represents an error.</summary>
    public bool IsError { get; init; }

    /// <summary>Returns the formatted log message, prefixed with <c>[ERROR] </c> when applicable.</summary>
    public override string ToString()
    {
        var prefix = IsError ? "[ERROR] " : string.Empty;
        return $"{prefix}{Message}";
    }
}
