using System.Collections.ObjectModel;

namespace SimpleZipDrive.Core.Services;

/// <summary>
/// Service for managing application logs.
/// </summary>
public interface ILoggingService
{
    /// <summary>
    /// Gets the collection of log entries.
    /// </summary>
    ObservableCollection<LogEntry> LogEntries { get; }

    /// <summary>
    /// Logs a message to the application log.
    /// </summary>
    /// <param name="message">The message to log.</param>
    void Log(string message);

    /// <summary>
    /// Logs an error message to the application log.
    /// </summary>
    /// <param name="message">The error message to log.</param>
    void LogError(string message);

    /// <summary>
    /// Clears all log entries.
    /// </summary>
    void Clear();

    /// <summary>
    /// Gets all log entries as a single string.
    /// </summary>
    /// <returns>All log entries joined by newlines.</returns>
    string GetAllLogsAsText();
}
