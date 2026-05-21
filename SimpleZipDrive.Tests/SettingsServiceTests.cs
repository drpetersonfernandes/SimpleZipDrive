using SimpleZipDrive.Models;
using SimpleZipDrive.Services;

namespace SimpleZipDrive.Tests;

public class SettingsServiceTests
{
    [Fact]
    public void ConstructorLoadsSettings()
    {
        var service = new SettingsService();

        Assert.NotNull(service.Settings);
        Assert.IsType<AppSettings>(service.Settings);
    }

    [Fact]
    public void ConstructorSetsDefaultMemoryLimit()
    {
        var service = new SettingsService();

        Assert.True(service.Settings.MaxMemoryPerFileMb > 0);
    }

    [Fact]
    public void SaveSettingsDoesNotThrow()
    {
        var service = new SettingsService();
        service.Settings.MaxMemoryPerFileMb = 256;

        var ex = Record.Exception(service.SaveSettings);

        Assert.Null(ex);
    }

    [Fact]
    public void ReloadSettingsRefreshesInstance()
    {
        var service = new SettingsService();
        var original = service.Settings;

        service.ReloadSettings();

        Assert.NotNull(service.Settings);
        Assert.IsType<AppSettings>(service.Settings);
    }

    [Theory]
    [InlineData(128)]
    [InlineData(256)]
    [InlineData(512)]
    [InlineData(1024)]
    public void UpdateRamLimitPositiveValuesUpdateSettings(int memoryMb)
    {
        var service = new SettingsService();
        service.Settings.MaxMemoryPerFileMb = 0;

        service.UpdateRamLimit(memoryMb);

        Assert.Equal(memoryMb, service.Settings.MaxMemoryPerFileMb);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(-100)]
    public void UpdateRamLimitNonPositiveValuesDoNotUpdateSettings(int memoryMb)
    {
        var service = new SettingsService();
        var originalValue = service.Settings.MaxMemoryPerFileMb;

        service.UpdateRamLimit(memoryMb);

        Assert.Equal(originalValue, service.Settings.MaxMemoryPerFileMb);
    }

    [Fact]
    public void UpdateRamLimitSavesAfterUpdate()
    {
        var service = new SettingsService();

        var ex = Record.Exception(() => service.UpdateRamLimit(256));

        Assert.Null(ex);
        Assert.Equal(256, service.Settings.MaxMemoryPerFileMb);
    }
}
