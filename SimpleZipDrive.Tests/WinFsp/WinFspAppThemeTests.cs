using WinFspAppTheme = SimpleZipDrive.Core.AppTheme;

namespace SimpleZipDrive.Tests.WinFsp;

public class WinFspAppThemeTests
{
    [Fact]
    public void Section_FormatsTitleCorrectly()
    {
        const string title = "Test Section";
        var result = WinFspAppTheme.Section(title);

        Assert.Equal("--- Test Section ---", result);
    }

    [Fact]
    public void Section_EmptyString_ReturnsFrameOnly()
    {
        var result = WinFspAppTheme.Section("");

        Assert.Equal("---  ---", result);
    }

    [Theory]
    [InlineData("A", "--- A ---")]
    [InlineData("Settings", "--- Settings ---")]
    [InlineData("Log Output", "--- Log Output ---")]
    public void Section_FormatsVariousTitlesCorrectly(string input, string expected)
    {
        var result = WinFspAppTheme.Section(input);

        Assert.Equal(expected, result);
    }

    [Fact]
    public void LogEntrySeparator_HasCorrectLength()
    {
        Assert.Equal(51, WinFspAppTheme.LogEntrySeparator.Length);
        Assert.All(WinFspAppTheme.LogEntrySeparator.TrimEnd('\n'), static c => Assert.Equal('-', c));
    }

    [Fact]
    public void WarningConstant_IsCorrect()
    {
        Assert.Equal("[!]", WinFspAppTheme.Warning);
    }

    [Fact]
    public void CriticalConstant_IsCorrect()
    {
        Assert.Equal("[!!!]", WinFspAppTheme.Critical);
    }

    [Fact]
    public void BulletConstant_IsCorrect()
    {
        Assert.Equal("  - ", WinFspAppTheme.Bullet);
    }
}
