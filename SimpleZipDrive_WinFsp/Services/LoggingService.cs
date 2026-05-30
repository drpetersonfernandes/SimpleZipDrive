using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Threading;
using SimpleZipDrive_WinFsp.Models;

namespace SimpleZipDrive_WinFsp.Services;

public class LoggingService : ILoggingService
{
    private const int MaxLogEntries = 5000;
    private readonly object _lock = new();

    public ObservableCollection<LogEntry> LogEntries { get; } = [];

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

    public void Clear()
    {
        LogEntries.Clear();
    }

    public string GetAllLogsAsText()
    {
        return string.Join(Environment.NewLine, LogEntries.Select(static e => e.ToString()));
    }

    private void AddEntry(LogEntry entry)
    {
        var dispatcher = Application.Current?.Dispatcher;

        if (dispatcher != null && !dispatcher.CheckAccess())
        {
            dispatcher.BeginInvoke(() =>
            {
                lock (_lock)
                {
                    AddEntryCore(entry);
                }
            }, DispatcherPriority.Normal);
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
