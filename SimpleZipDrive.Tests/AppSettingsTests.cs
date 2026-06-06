using SimpleZipDrive.Core.Models;

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
    public void MaxMemoryPerFileMbSmallValueNotClamped()
    {
        var settings = new AppSettings { MaxMemoryPerFileMb = 100 };

        Assert.Equal(100, settings.MaxMemoryPerFileMb);
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

    [Fact]
    public void MountTypeCanBeSetToDriveLetter()
    {
        var settings = new AppSettings { DefaultMountType = MountType.Folder };
        settings.DefaultMountType = MountType.DriveLetter;

        Assert.Equal(MountType.DriveLetter, settings.DefaultMountType);
    }

    [Fact]
    public void AutoOpenMountedDriveDefaultsToFalse()
    {
        var settings = new AppSettings();

        Assert.False(settings.AutoOpenMountedDrive);
    }

    [Fact]
    public void AutoOpenMountedDriveCanBeSetToTrue()
    {
        var settings = new AppSettings { AutoOpenMountedDrive = true };

        Assert.True(settings.AutoOpenMountedDrive);
    }

    [Fact]
    public void AutoOpenMountedDriveCanBeToggled()
    {
        var settings = new AppSettings { AutoOpenMountedDrive = true };
        settings.AutoOpenMountedDrive = false;

        Assert.False(settings.AutoOpenMountedDrive);
    }

    [Fact]
    public void CrossIntegrityMountDefaultsToFalse()
    {
        var settings = new AppSettings();

        Assert.False(settings.CrossIntegrityMount);
    }

    [Fact]
    public void CrossIntegrityMountCanBeSetToTrue()
    {
        var settings = new AppSettings { CrossIntegrityMount = true };

        Assert.True(settings.CrossIntegrityMount);
    }

    [Fact]
    public void CrossIntegrityMountCanBeToggled()
    {
        var settings = new AppSettings { CrossIntegrityMount = true };
        settings.CrossIntegrityMount = false;

        Assert.False(settings.CrossIntegrityMount);
    }
}
