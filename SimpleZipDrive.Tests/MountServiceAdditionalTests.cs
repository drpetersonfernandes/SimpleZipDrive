using System.Collections.ObjectModel;
using SimpleZipDrive.Core.Models;
using SimpleZipDrive.Core.Services;
using SimpleZipDrive.Services;

namespace SimpleZipDrive.Tests;

public class MountServiceAdditionalTests : IDisposable
{
    private readonly FakeLoggingService _loggingService = new();
    private readonly FakeSettingsService _settingsService = new();

    // ─── MountAsync: not mounted returns immediately ───

    [Fact]
    public void UnmountAsync_NotMounted_ReturnsImmediately()
    {
        var service = new MountService(_loggingService, _settingsService);

        // Should not throw when unmounting when not mounted
        var ex = Record.Exception(() => service.UnmountAsync().GetAwaiter().GetResult());
        Assert.Null(ex);
    }

    // ─── IsMounted: default is false ───

    [Fact]
    public void IsMounted_DefaultIsFalse()
    {
        var service = new MountService(_loggingService, _settingsService);
        Assert.False(service.IsMounted);
    }

    // ─── CurrentMountPoint: default is null ───

    [Fact]
    public void CurrentMountPoint_DefaultIsNull()
    {
        var service = new MountService(_loggingService, _settingsService);
        Assert.Null(service.CurrentMountPoint);
    }

    // ─── CurrentArchivePath: default is null ───

    [Fact]
    public void CurrentArchivePath_DefaultIsNull()
    {
        var service = new MountService(_loggingService, _settingsService);
        Assert.Null(service.CurrentArchivePath);
    }

    // ─── GetArchiveType: case insensitive ───

    [Theory]
    [InlineData("TEST.ZIP", "zip")]
    [InlineData("Test.7Z", "7z")]
    [InlineData("TEST.RAR", "rar")]
    public void GetArchiveType_CaseInsensitive(string path, string expected)
    {
        var service = new MountService(_loggingService, _settingsService);
        Assert.Equal(expected, service.GetArchiveType(path));
    }

    // ─── GetArchiveType: path with spaces ───

    [Fact]
    public void GetArchiveType_PathWithSpaces()
    {
        var service = new MountService(_loggingService, _settingsService);
        Assert.Equal("zip", service.GetArchiveType(@"C:\My Documents\my archive.zip"));
    }

    // ─── MountStatusChanged: event exists ───

    [Fact]
    public void MountStatusChanged_Event_CanSubscribe()
    {
        var service = new MountService(_loggingService, _settingsService);
        var eventRaised = false;

        service.MountStatusChanged += (_, _) => { eventRaised = true; };

        // Event should be subscribable
        Assert.False(eventRaised);
    }

    // ─── Dispose: idempotent ───

    [Fact]
    public void Dispose_DoubleDispose_DoesNotThrow()
    {
        var service = new MountService(_loggingService, _settingsService);

        service.Dispose();
        var ex = Record.Exception(service.Dispose);
        Assert.Null(ex);
    }

    // ─── Dispose: while not mounted ───

    [Fact]
    public void Dispose_NotMounted_DoesNotThrow()
    {
        var service = new MountService(_loggingService, _settingsService);

        var ex = Record.Exception(service.Dispose);
        Assert.Null(ex);
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
        }

        public void Dispose()
        {
            GC.SuppressFinalize(this);
        }
    }
}
