using System.Collections.ObjectModel;
using System.Diagnostics.CodeAnalysis;
using System.Reflection;
using SimpleZipDrive_WinFsp.Services;
using SimpleZipDrive.Core.Models;
using SimpleZipDrive.Core.Services;

namespace SimpleZipDrive.Tests.WinFsp;

[SuppressMessage("ReSharper", "NullableWarningSuppressionIsUsed")]
public class WinFspMountServiceAdditionalTests : IDisposable
{
    private readonly FakeLoggingService _loggingService = new();
    private readonly FakeSettingsService _settingsService = new();

    // ─── MountAsync edge cases ───

    [Fact]
    public async Task MountAsync_WhenAlreadyMounted_ThrowsInvalidOperation()
    {
        var service = new MountService(_loggingService, _settingsService);

        // Simulate mounted state via reflection
        var isMountedProp = typeof(MountService).GetProperty("IsMounted");
        isMountedProp!.SetValue(service, true);

        await Assert.ThrowsAsync<InvalidOperationException>(() => service.MountAsync("test.zip"));
    }

    [Fact]
    public async Task MountAsync_NonExistentFile_ThrowsFileNotFound()
    {
        var service = new MountService(_loggingService, _settingsService);

        await Assert.ThrowsAsync<FileNotFoundException>(() =>
            service.MountAsync(@"C:\nonexistent\path\file.zip"));
    }

    [Fact]
    public async Task MountAsync_UnsupportedExtension_ThrowsArgument()
    {
        // Create a temp file with unsupported extension
        var tempFile = Path.Combine(Path.GetTempPath(), $"test_{Guid.NewGuid():N}.txt");
        try
        {
            await File.WriteAllTextAsync(tempFile, "test");

            var service = new MountService(_loggingService, _settingsService);

            await Assert.ThrowsAsync<ArgumentException>(() => service.MountAsync(tempFile));
        }
        finally
        {
            File.Delete(tempFile);
        }
    }

    [Fact]
    public async Task MountAsync_UnsupportedExtension_ThrowsWithDescriptiveMessage()
    {
        var tempFile = Path.Combine(Path.GetTempPath(), $"test_{Guid.NewGuid():N}.tar");
        try
        {
            await File.WriteAllTextAsync(tempFile, "test");

            var service = new MountService(_loggingService, _settingsService);

            var ex = await Assert.ThrowsAsync<ArgumentException>(() => service.MountAsync(tempFile));
            Assert.Contains(".tar", ex.Message);
            Assert.Contains("not a supported archive", ex.Message);
        }
        finally
        {
            File.Delete(tempFile);
        }
    }

    // ─── UnmountAsync edge cases ───

    [Fact]
    public async Task UnmountAsync_WhenNotMounted_DoesNothing()
    {
        var service = new MountService(_loggingService, _settingsService);

        var ex = await Record.ExceptionAsync(service.UnmountAsync);
        Assert.Null(ex);
    }

    // ─── Dispose tests ───

    [Fact]
    public void Dispose_DoesNotThrow()
    {
        var service = new MountService(_loggingService, _settingsService);

        var ex = Record.Exception(service.Dispose);
        Assert.Null(ex);
    }

    [Fact]
    public void Dispose_DoubleDispose_DoesNotThrow()
    {
        var service = new MountService(_loggingService, _settingsService);

        service.Dispose();
        var ex = Record.Exception(service.Dispose);
        Assert.Null(ex);
    }

    // ─── GetArchiveType additional tests ───

    [Theory]
    [InlineData(@"C:\path\to\archive.ZIP", "zip")]
    [InlineData(@"C:\path\to\archive.7Z", "7z")]
    [InlineData(@"C:\path\to\archive.RAR", "rar")]
    public void GetArchiveType_UpperCaseExtension_ReturnsLowerCase(string path, string expected)
    {
        var service = new MountService(_loggingService, _settingsService);
        Assert.Equal(expected, service.GetArchiveType(path));
    }

    [Theory]
    [InlineData("archive.Zip", "zip")]
    [InlineData("archive.7z", "7z")]
    [InlineData("archive.Rar", "rar")]
    public void GetArchiveType_MixedCaseExtension_ReturnsLowerCase(string path, string expected)
    {
        var service = new MountService(_loggingService, _settingsService);
        Assert.Equal(expected, service.GetArchiveType(path));
    }

    [Fact]
    public void GetArchiveType_PathWithSpaces_WorksCorrectly()
    {
        var service = new MountService(_loggingService, _settingsService);
        Assert.Equal("zip", service.GetArchiveType("my archive.zip"));
    }

    [Fact]
    public void GetArchiveType_PathWithDots_WorksCorrectly()
    {
        var service = new MountService(_loggingService, _settingsService);
        Assert.Equal("zip", service.GetArchiveType("archive.v2.backup.zip"));
    }

    // ─── MountStatusChanged event tests ───

    [Fact]
    public void MountStatusChanged_InitiallyNotSubscribed_NoError()
    {
        var service = new MountService(_loggingService, _settingsService);

        // Event should be null initially (no subscribers)
        // Invoking OnMountStatusChanged via reflection should not throw
        var method = typeof(MountService).GetMethod("OnMountStatusChanged",
            BindingFlags.NonPublic | BindingFlags.Instance);

        var ex = Record.Exception(() => method!.Invoke(service, null));
        Assert.Null(ex);
    }

    [Fact]
    public void MountStatusChanged_CanSubscribe()
    {
        var service = new MountService(_loggingService, _settingsService);
        var eventRaised = false;

        service.MountStatusChanged += (_, _) => { eventRaised = true; };

        // Invoke via reflection
        var method = typeof(MountService).GetMethod("OnMountStatusChanged",
            BindingFlags.NonPublic | BindingFlags.Instance);
        method!.Invoke(service, null);

        Assert.True(eventRaised);
    }

    // ─── IsVersionMismatchError via reflection ───

    [Fact]
    public void IsVersionMismatchError_IncorrectDllVersion_ReturnsTrue()
    {
        var method = typeof(MountService).GetMethod("IsVersionMismatchError",
            BindingFlags.NonPublic | BindingFlags.Static)!;

        var ex = new TypeLoadException("incorrect dll version (need 2.2, have 2.1)");
        var result = (bool)method.Invoke(null, [ex])!;

        Assert.True(result);
    }

    [Fact]
    public void IsVersionMismatchError_TypeInitializationWithDllVersion_ReturnsTrue()
    {
        var method = typeof(MountService).GetMethod("IsVersionMismatchError",
            BindingFlags.NonPublic | BindingFlags.Static)!;

        var inner = new TypeLoadException("incorrect dll version (need 2.2, have 2.1)");
        var ex = new TypeInitializationException("Fsp.Interop.Api", inner);
        var result = (bool)method.Invoke(null, [ex])!;

        Assert.True(result);
    }

    [Fact]
    public void IsVersionMismatchError_UnrelatedException_ReturnsFalse()
    {
        var method = typeof(MountService).GetMethod("IsVersionMismatchError",
            BindingFlags.NonPublic | BindingFlags.Static)!;

        var ex = new IOException("file not found");
        var result = (bool)method.Invoke(null, [ex])!;

        Assert.False(result);
    }

    // ─── GetDeepestMessage via reflection ───

    [Fact]
    public void GetDeepestMessage_NoInnerException_ReturnsTopMessage()
    {
        var method = typeof(MountService).GetMethod("GetDeepestMessage",
            BindingFlags.NonPublic | BindingFlags.Static)!;

        var ex = new InvalidOperationException("top level message");
        var result = (string)method.Invoke(null, [ex])!;

        Assert.Equal("top level message", result);
    }

    [Fact]
    public void GetDeepestMessage_WithInnerException_ReturnsInnermostMessage()
    {
        var method = typeof(MountService).GetMethod("GetDeepestMessage",
            BindingFlags.NonPublic | BindingFlags.Static)!;

        var inner = new IOException("deepest message");
        var middle = new InvalidOperationException("middle", inner);
        var outer = new InvalidOperationException("outer", middle);
        var result = (string)method.Invoke(null, [outer])!;

        Assert.Equal("deepest message", result);
    }

    // ─── IsDriveLetterMountPoint via reflection ───

    [Theory]
    [InlineData("M:", true)]
    [InlineData("Z:", true)]
    [InlineData("m:", true)]
    [InlineData("C:\\", true)]
    [InlineData("M", false)]
    [InlineData("", false)]
    [InlineData("1:", false)]
    [InlineData("path/to/dir", false)]
    public void IsDriveLetterMountPoint_VariousInputs_ReturnsExpected(string mountPoint, bool expected)
    {
        var method = typeof(MountService).GetMethod("IsDriveLetterMountPoint",
            BindingFlags.NonPublic | BindingFlags.Static)!;

        var result = (bool)method.Invoke(null, [mountPoint])!;

        Assert.Equal(expected, result);
    }

    // ─── Settings integration ───

    [Fact]
    public void Constructor_SavesReferencesToServices()
    {
        var service = new MountService(_loggingService, _settingsService);

        Assert.NotNull(service);
        Assert.False(service.IsMounted);
        Assert.Null(service.CurrentMountPoint);
        Assert.Null(service.CurrentArchivePath);
    }

    [Fact]
    public async Task MountAsync_UnsupportedExtension_DoesNotSetCurrentArchivePath()
    {
        var tempFile = Path.Combine(Path.GetTempPath(), $"test_{Guid.NewGuid():N}.xyz");
        try
        {
            await File.WriteAllTextAsync(tempFile, "test");
            var service = new MountService(_loggingService, _settingsService);

            await Assert.ThrowsAsync<ArgumentException>(() => service.MountAsync(tempFile));

            // CurrentArchivePath should remain null because the extension check fails first
            Assert.Null(service.CurrentArchivePath);
        }
        finally
        {
            File.Delete(tempFile);
        }
    }

    public void Dispose()
    {
        GC.SuppressFinalize(this);
    }

    private class FakeLoggingService : ILoggingService
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
    }

    private class FakeSettingsService : ISettingsService
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
    }
}
