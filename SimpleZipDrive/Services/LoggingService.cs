using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Threading;
using SimpleZipDrive.Models;

namespace SimpleZipDrive.Services;

/// <summary>
/// Implementation of the logging service.
/// </summary>
public class LoggingService : ILoggingService
{
    private const int MaxLogEntries = 5000;

    /// <inheritdoc />
    public ObservableCollection<LogEntry> LogEntries { get; } = [];

    /// <inheritdoc />
    public void Log(string message)
    {
        var entry = new LogEntry
        {
            Timestamp = DateTime.Now,
            Message = message.TrimEnd('\r', '\n'),
            IsError = false
        };

        AddEntry(entry);
    }

    /// <inheritdoc />
    public void LogError(string message)
    {
        var entry = new LogEntry
        {
            Timestamp = DateTime.Now,
            Message = message.TrimEnd('\r', '\n'),
            IsError = true
        };

        AddEntry(entry);
    }

    /// <inheritdoc />
    public void Clear()
    {
        LogEntries.Clear();
    }

    /// <inheritdoc />
    public string GetAllLogsAsText()
    {
        return string.Join(Environment.NewLine, LogEntries.Select(static e => e.ToString()));
    }

    private void AddEntry(LogEntry entry)
    {
        // Prevent exact duplicate messages within short timeframe (100ms to account for async processing)
        if (LogEntries.Count > 0)
        {
            var lastEntry = LogEntries[^1];
            if (lastEntry.Message == entry.Message &&
                (entry.Timestamp - lastEntry.Timestamp).TotalMilliseconds < 100)
                return;
        }

        // Add entry directly or via dispatcher depending on thread
        if (Application.Current?.Dispatcher != null && !Application.Current.Dispatcher.CheckAccess())
        {
            // Not on UI thread - use dispatcher synchronously to preserve order
            Application.Current.Dispatcher.Invoke(() =>
            {
                LogEntries.Add(entry);
                while (LogEntries.Count > MaxLogEntries)
                    LogEntries.RemoveAt(0);
            }, DispatcherPriority.Normal);
        }
        else
        {
            // Already on UI thread - add directly
            LogEntries.Add(entry);
            while (LogEntries.Count > MaxLogEntries)
                LogEntries.RemoveAt(0);
        }
    }
}
