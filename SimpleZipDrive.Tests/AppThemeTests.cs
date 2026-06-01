using SimpleZipDrive.Core;

namespace SimpleZipDrive.Tests;

public class AppThemeTests
{
    [Fact]
    public void SectionFormatsTitleCorrectly()
    {
        const string title = "Test Section";
        var result = AppTheme.Section(title);

        Assert.Equal("--- Test Section ---", result);
    }

    [Fact]
    public void SectionWithEmptyStringReturnsFrameOnly()
    {
        var result = AppTheme.Section("");

        Assert.Equal("---  ---", result);
    }

    [Theory]
    [InlineData("A", "--- A ---")]
    [InlineData("Settings", "--- Settings ---")]
    [InlineData("Log Output", "--- Log Output ---")]
    public void SectionFormatsVariousTitlesCorrectly(string input, string expected)
    {
        var result = AppTheme.Section(input);

        Assert.Equal(expected, result);
    }

    [Fact]
    public void LogEntrySeparatorHasCorrectLength()
    {
        // The separator should be 50 dashes plus newline
        Assert.Equal(51, AppTheme.LogEntrySeparator.Length);
        Assert.All(AppTheme.LogEntrySeparator.TrimEnd('\n'), static c => Assert.Equal('-', c));
    }

    [Fact]
    public void WarningConstantIsCorrect()
    {
        Assert.Equal("[!]", AppTheme.Warning);
    }

    [Fact]
    public void CriticalConstantIsCorrect()
    {
        Assert.Equal("[!!!]", AppTheme.Critical);
    }

    [Fact]
    public void BulletConstantIsCorrect()
    {
        Assert.Equal("  - ", AppTheme.Bullet);
    }

    [Fact]
    public void DokanLogPrefixConstantIsCorrect()
    {
        Assert.Equal("[DokanNet] ", AppTheme.DokanLogPrefix);
    }
}
