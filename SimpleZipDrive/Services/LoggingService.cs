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
        if (string.IsNullOrWhiteSpace(message)) return;

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
        if (string.IsNullOrWhiteSpace(message)) return;

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

        // Use dispatcher if not on UI thread to avoid deadlock during shutdown
        if (Application.Current?.Dispatcher != null && !Application.Current.Dispatcher.CheckAccess())
        {
            Application.Current.Dispatcher.InvokeAsync(() =>
            {
                LogEntries.Add(entry);

                while (LogEntries.Count > MaxLogEntries)
                    LogEntries.RemoveAt(0);
            }, DispatcherPriority.Background);
        }
        else
        {
            LogEntries.Add(entry);

            while (LogEntries.Count > MaxLogEntries)
                LogEntries.RemoveAt(0);
        }
    }
}
