using SimpleZipDrive.Core.Models;

namespace SimpleZipDrive.Tests.WinFsp;

public class WinFspAppSettingsTests
{
    [Fact]
    public void DefaultConstructor_CreatesSettingsWithDefaultValues()
    {
        var settings = new AppSettings();

        Assert.Equal(512, settings.MaxMemoryPerFileMb);
    }

    [Fact]
    public void MaxMemoryPerFileBytes_CalculatesCorrectly()
    {
        var settings = new AppSettings { MaxMemoryPerFileMb = 100 };

        Assert.Equal(100L * 1024L * 1024L, settings.MaxMemoryPerFileBytes);
    }

    [Theory]
    [InlineData(1, 1L * 1024L * 1024L)]
    [InlineData(512, 512L * 1024L * 1024L)]
    [InlineData(1024, 1024L * 1024L * 1024L)]
    [InlineData(2048, 2048L * 1024L * 1024L)]
    public void MaxMemoryPerFileBytes_CalculatesForVariousValues(int memoryMb, long expectedBytes)
    {
        var settings = new AppSettings { MaxMemoryPerFileMb = memoryMb };

        Assert.Equal(expectedBytes, settings.MaxMemoryPerFileBytes);
    }

    [Fact]
    public void Properties_CanBeModified()
    {
        var settings = new AppSettings { MaxMemoryPerFileMb = 256 };

        Assert.Equal(256, settings.MaxMemoryPerFileMb);
    }

    [Fact]
    public void NewInstance_HasDefaultMaxMemory()
    {
        var settings = new AppSettings();

        Assert.Equal(512, settings.MaxMemoryPerFileMb);
        Assert.Equal(512L * 1024L * 1024L, settings.MaxMemoryPerFileBytes);
    }

    [Fact]
    public void ZeroMemoryLimit_CalculatesToZeroBytes()
    {
        var settings = new AppSettings { MaxMemoryPerFileMb = 0 };

        Assert.Equal(0, settings.MaxMemoryPerFileBytes);
    }

    [Fact]
    public void MaxMemoryPerFileMb_ClampedByAvailableMemory()
    {
        var settings = new AppSettings();
        var availableMemoryMb = GC.GetGCMemoryInfo().TotalAvailableMemoryBytes / 1024 / 1024;
        var maxAllowedMb = (long)(availableMemoryMb * 0.9);

        settings.MaxMemoryPerFileMb = long.MaxValue;

        Assert.True(settings.MaxMemoryPerFileMb <= maxAllowedMb);
    }
}
