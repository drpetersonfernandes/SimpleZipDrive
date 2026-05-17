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
    private readonly object _lock = new();

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
        var dispatcher = Application.Current?.Dispatcher;

        if (dispatcher != null)
        {
            if (!dispatcher.CheckAccess())
            {
                dispatcher.Invoke(() => AddEntryCore(entry), DispatcherPriority.Normal);
            }
            else
            {
                AddEntryCore(entry);
            }
        }
        else
        {
            lock (_lock)
            {
                AddEntryCore(entry);
            }
        }
    }

    private void AddEntryCore(LogEntry entry)
    {
        if (LogEntries.Count > 0)
        {
            var lastEntry = LogEntries[^1];
            if (lastEntry.Message == entry.Message &&
                (entry.Timestamp - lastEntry.Timestamp).TotalMilliseconds < 100)
                return;
        }

        LogEntries.Add(entry);
        while (LogEntries.Count > MaxLogEntries)
            LogEntries.RemoveAt(0);
    }
}
