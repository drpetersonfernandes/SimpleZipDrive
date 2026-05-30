using SimpleZipDrive.Models;

namespace SimpleZipDrive.Tests;

public class AppSettingsTests
{
    [Fact]
    public void DefaultConstructorCreatesSettingsWithDefaultValues()
    {
        var settings = new AppSettings();

        Assert.Equal(512, settings.MaxMemoryPerFileMb);
    }

    [Fact]
    public void MaxMemoryPerFileBytesCalculatesCorrectly()
    {
        var settings = new AppSettings { MaxMemoryPerFileMb = 100 };

        Assert.Equal(100 * 1024 * 1024, settings.MaxMemoryPerFileBytes);
    }

    [Theory]
    [InlineData(1, 1 * 1024 * 1024)]
    [InlineData(512, 512 * 1024 * 1024)]
    [InlineData(1024, 1024 * 1024 * 1024)]
    [InlineData(2047, 2047 * 1024 * 1024)] // 2048 would overflow int32
    public void MaxMemoryPerFileBytesCalculatesCorrectlyForVariousValues(int memoryMb, int expectedBytes)
    {
        var settings = new AppSettings { MaxMemoryPerFileMb = memoryMb };

        Assert.Equal(expectedBytes, settings.MaxMemoryPerFileBytes);
    }

    [Fact]
    public void PropertiesCanBeModified()
    {
        var settings = new AppSettings
        {
            MaxMemoryPerFileMb = 256
        };

        Assert.Equal(256, settings.MaxMemoryPerFileMb);
    }

    [Fact]
    public void NewInstanceHasDefaultMaxMemory()
    {
        var settings = new AppSettings();

        Assert.Equal(512, settings.MaxMemoryPerFileMb);
        Assert.Equal(512 * 1024 * 1024, settings.MaxMemoryPerFileBytes);
    }

    [Fact]
    public void ZeroMemoryLimitCalculatesToZeroBytes()
    {
        var settings = new AppSettings { MaxMemoryPerFileMb = 0 };

        Assert.Equal(0, settings.MaxMemoryPerFileBytes);
    }

    [Fact]
    public void MaxMemoryPerFileMbClampedByAvailableMemory()
    {
        var settings = new AppSettings();
        var availableMemoryMb = GC.GetGCMemoryInfo().TotalAvailableMemoryBytes / 1024 / 1024;
        var maxAllowedMb = (long)(availableMemoryMb * 0.9);

        settings.MaxMemoryPerFileMb = long.MaxValue;

        Assert.True(settings.MaxMemoryPerFileMb <= maxAllowedMb);
    }

    [Fact]
    public void MaxMemoryPerFileMbSmallValueNotClamped()
    {
        var settings = new AppSettings { MaxMemoryPerFileMb = 100 };

        Assert.Equal(100, settings.MaxMemoryPerFileMb);
    }
}
