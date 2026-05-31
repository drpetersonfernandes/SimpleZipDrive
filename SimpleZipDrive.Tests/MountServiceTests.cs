using System.Collections.ObjectModel;
using SimpleZipDrive.Core.Models;
using SimpleZipDrive.Core.Services;
using SimpleZipDrive.Services;

namespace SimpleZipDrive.Tests;

public class MountServiceTests : IDisposable
{
    private readonly FakeLoggingService _loggingService = new();
    private readonly FakeSettingsService _settingsService = new();

    [Fact]
    public void Constructor_NullLoggingService_ThrowsArgumentNullException()
    {
        Assert.Throws<ArgumentNullException>(() => new MountService(null!, _settingsService));
    }

    [Fact]
    public void Constructor_NullSettingsService_ThrowsArgumentNullException()
    {
        Assert.Throws<ArgumentNullException>(() => new MountService(_loggingService, null!));
    }

    [Fact]
    public void Constructor_ValidArguments_CreatesInstance()
    {
        var service = new MountService(_loggingService, _settingsService);
        Assert.NotNull(service);
        Assert.False(service.IsMounted);
        Assert.Null(service.CurrentMountPoint);
        Assert.Null(service.CurrentArchivePath);
    }

    [Theory]
    [InlineData("archive.zip", "zip")]
    [InlineData("archive.7z", "7z")]
    [InlineData("archive.rar", "rar")]
    public void GetArchiveType_KnownExtensions_ReturnsCorrectType(string filePath, string expected)
    {
        var service = new MountService(_loggingService, _settingsService);

        var result = service.GetArchiveType(filePath);

        Assert.Equal(expected, result);
    }

    [Theory]
    [InlineData("archive.txt", "txt")]
    [InlineData("archive.exe", "exe")]
    [InlineData("archive.dll", "dll")]
    public void GetArchiveType_UnknownExtensions_ReturnsExtensionWithoutDot(string filePath, string expected)
    {
        var service = new MountService(_loggingService, _settingsService);

        var result = service.GetArchiveType(filePath);

        Assert.Equal(expected, result);
    }

    [Theory]
    [InlineData("ARCHIVE.ZIP", "zip")]
    [InlineData("Archive.Rar", "rar")]
    [InlineData("archive.7Z", "7z")]
    public void GetArchiveType_IsCaseInsensitive(string filePath, string expected)
    {
        var service = new MountService(_loggingService, _settingsService);

        var result = service.GetArchiveType(filePath);

        Assert.Equal(expected, result);
    }

    [Theory]
    [InlineData(@"C:\Users\test\Documents\archive.zip", "zip")]
    [InlineData("/home/user/backup.7z", "7z")]
    [InlineData(@"\\network\share\documents\data.rar", "rar")]
    public void GetArchiveType_FullPaths_ReturnsCorrectType(string filePath, string expected)
    {
        var service = new MountService(_loggingService, _settingsService);

        var result = service.GetArchiveType(filePath);

        Assert.Equal(expected, result);
    }

    [Fact]
    public void GetArchiveType_NoExtension_ReturnsEmptyString()
    {
        var service = new MountService(_loggingService, _settingsService);

        var result = service.GetArchiveType("fileWithoutExtension");

        Assert.Equal(string.Empty, result);
    }

    public void Dispose()
    {
        (_loggingService as IDisposable).Dispose();
        (_settingsService as IDisposable).Dispose();
        GC.SuppressFinalize(this);
    }

    private class FakeLoggingService : ILoggingService, IDisposable
    {
        public ObservableCollection<LogEntry> LogEntries { get; } = [];

        public void Log(string message)
        {
        }

        public void LogError(string message)
        {
        }

        public void Clear()
        {
        }

        public string GetAllLogsAsText()
        {
            return string.Empty;
        }

        public void Dispose()
        {
            GC.SuppressFinalize(this);
        }
    }

    private class FakeSettingsService : ISettingsService, IDisposable
    {
        public AppSettings Settings { get; } = new();

        public void SaveSettings()
        {
        }

        public void ReloadSettings()
        {
        }

        public void UpdateRamLimit(int maxMemoryPerFileMb)
        {
            if (maxMemoryPerFileMb > 0)
            {
                Settings.MaxMemoryPerFileMb = maxMemoryPerFileMb;
            }
        }

        public void Dispose()
        {
            GC.SuppressFinalize(this);
        }
    }
}
