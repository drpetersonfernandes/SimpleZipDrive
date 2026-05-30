using SimpleZipDrive_WinFsp.Models;
using SimpleZipDrive_WinFsp.Services;

namespace SimpleZipDrive.Tests.WinFsp;

public class WinFspSettingsServiceTests
{
    [Fact]
    public void Constructor_LoadsSettings()
    {
        var service = new SettingsService();

        Assert.NotNull(service.Settings);
        Assert.IsType<AppSettings>(service.Settings);
    }

    [Fact]
    public void Constructor_SetsDefaultMemoryLimit()
    {
        var service = new SettingsService();

        Assert.True(service.Settings.MaxMemoryPerFileMb > 0);
    }

    [Fact]
    public void SaveSettings_DoesNotThrow()
    {
        var service = new SettingsService
        {
            Settings = { MaxMemoryPerFileMb = 256 }
        };

        var ex = Record.Exception(service.SaveSettings);

        Assert.Null(ex);
    }

    [Fact]
    public void ReloadSettings_RefreshesInstance()
    {
        var service = new SettingsService();

        service.ReloadSettings();

        Assert.NotNull(service.Settings);
        Assert.IsType<AppSettings>(service.Settings);
    }

    [Theory]
    [InlineData(128)]
    [InlineData(256)]
    [InlineData(512)]
    [InlineData(1024)]
    public void UpdateRamLimit_PositiveValues_UpdateSettings(int memoryMb)
    {
        var service = new SettingsService
        {
            Settings = { MaxMemoryPerFileMb = 0 }
        };

        service.UpdateRamLimit(memoryMb);

        Assert.Equal(memoryMb, service.Settings.MaxMemoryPerFileMb);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(-100)]
    public void UpdateRamLimit_NonPositiveValues_DoNotUpdateSettings(int memoryMb)
    {
        var service = new SettingsService();
        var originalValue = service.Settings.MaxMemoryPerFileMb;

        service.UpdateRamLimit(memoryMb);

        Assert.Equal(originalValue, service.Settings.MaxMemoryPerFileMb);
    }

    [Fact]
    public void UpdateRamLimit_SavesAfterUpdate()
    {
        var service = new SettingsService();

        var ex = Record.Exception(() => service.UpdateRamLimit(256));

        Assert.Null(ex);
        Assert.Equal(256, service.Settings.MaxMemoryPerFileMb);
    }
}
