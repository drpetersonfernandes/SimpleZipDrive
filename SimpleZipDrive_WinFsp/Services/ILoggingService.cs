using System.Collections.ObjectModel;
using SimpleZipDrive_WinFsp.Models;

namespace SimpleZipDrive_WinFsp.Services;

public interface ILoggingService
{
    ObservableCollection<LogEntry> LogEntries { get; }

    void Log(string message);

    void LogError(string message);

    void Clear();

    string GetAllLogsAsText();
}
