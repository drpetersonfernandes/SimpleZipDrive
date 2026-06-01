using SimpleZipDrive.Core.Models;

namespace SimpleZipDrive.Tests.WinFsp;

public class WinFspAppSettingsTests
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

        Assert.Equal(100L * 1024L * 1024L, settings.MaxMemoryPerFileBytes);
    }

    [Theory]
    [InlineData(1, 1L * 1024L * 1024L)]
    [InlineData(512, 512L * 1024L * 1024L)]
    [InlineData(1024, 1024L * 1024L * 1024L)]
    [InlineData(2048, 2048L * 1024L * 1024L)]
    public void MaxMemoryPerFileBytesCalculatesForVariousValues(int memoryMb, long expectedBytes)
    {
        var settings = new AppSettings { MaxMemoryPerFileMb = memoryMb };

        Assert.Equal(expectedBytes, settings.MaxMemoryPerFileBytes);
    }

    [Fact]
    public void PropertiesCanBeModified()
    {
        var settings = new AppSettings { MaxMemoryPerFileMb = 256 };

        Assert.Equal(256, settings.MaxMemoryPerFileMb);
    }

    [Fact]
    public void NewInstanceHasDefaultMaxMemory()
    {
        var settings = new AppSettings();

        Assert.Equal(512, settings.MaxMemoryPerFileMb);
        Assert.Equal(512L * 1024L * 1024L, settings.MaxMemoryPerFileBytes);
    }

    [Fact]
    public void ZeroMemoryLimitCalculatesToOneMegabyte()
    {
        var settings = new AppSettings { MaxMemoryPerFileMb = 0 };

        Assert.Equal(1 * 1024 * 1024, settings.MaxMemoryPerFileBytes);
        Assert.Equal(1, settings.MaxMemoryPerFileMb);
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
    public void DefaultMountTypeIsDriveLetter()
    {
        var settings = new AppSettings();

        Assert.Equal(MountType.DriveLetter, settings.DefaultMountType);
    }

    [Fact]
    public void MountTypeCanBeSetToFolder()
    {
        var settings = new AppSettings { DefaultMountType = MountType.Folder };

        Assert.Equal(MountType.Folder, settings.DefaultMountType);
    }
}
