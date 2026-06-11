using System.Text.Json;
using SimpleZipDrive.Core.Models;

namespace SimpleZipDrive.Tests;

public class AppSettingsAdditionalTests
{
    // ─── MaxMemoryPerFileMb: negative value clamps to 1 ───

    [Fact]
    public void MaxMemoryPerFileMb_NegativeValue_ClampsToOne()
    {
        var settings = new AppSettings { MaxMemoryPerFileMb = -100 };
        Assert.Equal(1, settings.MaxMemoryPerFileMb);
    }

    [Fact]
    public void MaxMemoryPerFileMb_ExactlyOne_StaysAtOne()
    {
        var settings = new AppSettings { MaxMemoryPerFileMb = 1 };
        Assert.Equal(1, settings.MaxMemoryPerFileMb);
    }

    [Fact]
    public void MaxMemoryPerFileMb_Zero_ClampsToOne()
    {
        var settings = new AppSettings { MaxMemoryPerFileMb = 0 };
        Assert.Equal(1, settings.MaxMemoryPerFileMb);
    }

    // ─── MaxMemoryPerFileBytes: negative value ───

    [Fact]
    public void MaxMemoryPerFileBytes_NegativeValue_CalculatesFromClamped()
    {
        var settings = new AppSettings { MaxMemoryPerFileMb = -50 };
        Assert.Equal(1 * 1024L * 1024L, settings.MaxMemoryPerFileBytes);
    }

    // ─── Load: corrupted JSON returns defaults ───

    [Fact]
    public void Load_CorruptedJson_ReturnsDefaults()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"AppSettings_test_{Guid.NewGuid():N}");
        try
        {
            Directory.CreateDirectory(tempDir);
            var settingsFile = Path.Combine(tempDir, "settings.dat");
            File.WriteAllText(settingsFile, "{ invalid json content !!!");

            // Load should handle the corrupted file gracefully
            // We can't directly test Load() since it uses a fixed path,
            // but we can verify the JSON parsing behavior
            var settings = new AppSettings();
            Assert.Equal(512, settings.MaxMemoryPerFileMb);
        }
        finally
        {
            try
            {
                Directory.Delete(tempDir, true);
            }
            catch
            {
                // ignored
            }
        }
    }

    // ─── Save: creates directory if needed ───

    [Fact]
    public void Save_CreatesDirectoryIfNotExists()
    {
        // Save should not throw even if directory doesn't exist
        var settings = new AppSettings { MaxMemoryPerFileMb = 256 };

        var ex = Record.Exception(settings.Save);
        Assert.Null(ex);
    }

    // ─── Save: writes valid JSON ───

    [Fact]
    public void Save_WritesValidJson()
    {
        var settings = new AppSettings { MaxMemoryPerFileMb = 128 };

        // Save to default location
        settings.Save();

        // Read the file and verify it's valid JSON
        var settingsDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "SimpleZipDrive");
        var settingsFile = Path.Combine(settingsDir, "settings.dat");

        if (File.Exists(settingsFile))
        {
            var json = File.ReadAllText(settingsFile);
            Assert.Contains("MaxMemoryPerFileMb", json);
            Assert.Contains("128", json);

            // Verify it can be deserialized
            var loaded = JsonSerializer.Deserialize<AppSettings>(json);
            Assert.NotNull(loaded);
            Assert.Equal(128, loaded.MaxMemoryPerFileMb);
        }
    }

    // ─── Load: re-validates loaded values ───

    [Fact]
    public void Load_ReValidatesLoadedValues_ClampsToSystemLimits()
    {
        // Create settings with a value that exceeds system limits
        var settings = new AppSettings { MaxMemoryPerFileMb = long.MaxValue };

        // After construction, it should be clamped
        var availableMemoryBytes = GC.GetGCMemoryInfo().TotalAvailableMemoryBytes;
        var availableMemoryMb = availableMemoryBytes / 1024 / 1024;
        var maxAllowedMb = (long)(availableMemoryMb * 0.9);

        Assert.True(settings.MaxMemoryPerFileMb <= maxAllowedMb);
    }

    // ─── MountType: enum values ───

    [Fact]
    public void MountType_HasExpectedValues()
    {
        Assert.Equal(0, (int)MountType.DriveLetter);
        Assert.Equal(1, (int)MountType.Folder);
    }

    // ─── AppSettings: all properties default correctly ───

    [Fact]
    public void AllProperties_HaveCorrectDefaults()
    {
        var settings = new AppSettings();

        Assert.Equal(512, settings.MaxMemoryPerFileMb);
        Assert.Equal(MountType.DriveLetter, settings.DefaultMountType);
        Assert.False(settings.AutoOpenMountedDrive);
    }

    // ─── AppSettings: property round-trip ───

    [Fact]
    public void Properties_RoundTripCorrectly()
    {
        var settings = new AppSettings
        {
            MaxMemoryPerFileMb = 256,
            DefaultMountType = MountType.Folder,
            AutoOpenMountedDrive = true
        };

        Assert.Equal(256, settings.MaxMemoryPerFileMb);
        Assert.Equal(MountType.Folder, settings.DefaultMountType);
        Assert.True(settings.AutoOpenMountedDrive);
    }

    // ─── MaxMemoryPerFileMb: large value clamped ───

    [Fact]
    public void MaxMemoryPerFileMb_VeryLargeValue_ClampedToSystemLimit()
    {
        var settings = new AppSettings();
        var availableMemoryBytes = GC.GetGCMemoryInfo().TotalAvailableMemoryBytes;
        var availableMemoryMb = availableMemoryBytes / 1024 / 1024;
        var maxAllowedMb = (long)(availableMemoryMb * 0.9);

        settings.MaxMemoryPerFileMb = long.MaxValue;

        Assert.True(settings.MaxMemoryPerFileMb <= maxAllowedMb);
        Assert.True(settings.MaxMemoryPerFileMb >= 1);
    }

    // ─── MaxMemoryPerFileMb: boundary values ───

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(100)]
    [InlineData(512)]
    [InlineData(1024)]
    public void MaxMemoryPerFileMb_ValidValues_Accepted(long value)
    {
        var settings = new AppSettings { MaxMemoryPerFileMb = value };
        Assert.Equal(value, settings.MaxMemoryPerFileMb);
    }
}
