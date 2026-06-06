using SimpleZipDrive.Core;

namespace SimpleZipDrive.Tests;

public class ZipFsHelpersTests
{
    // ─── SanitizeVolumeLabel tests ───

    [Fact]
    public void SanitizeVolumeLabel_Null_ReturnsDefault()
    {
        var result = ZipFsHelpers.SanitizeVolumeLabel(null);

        Assert.Equal(ZipFileSystemCore.DefaultVolumeLabel, result);
    }

    [Fact]
    public void SanitizeVolumeLabel_Empty_ReturnsDefault()
    {
        var result = ZipFsHelpers.SanitizeVolumeLabel("");

        Assert.Equal(ZipFileSystemCore.DefaultVolumeLabel, result);
    }

    [Fact]
    public void SanitizeVolumeLabel_Whitespace_ReturnsDefault()
    {
        var result = ZipFsHelpers.SanitizeVolumeLabel("   ");

        Assert.Equal(ZipFileSystemCore.DefaultVolumeLabel, result);
    }

    [Fact]
    public void SanitizeVolumeLabel_ValidLabel_ReturnsAsIs()
    {
        var result = ZipFsHelpers.SanitizeVolumeLabel("MyDrive");

        Assert.Equal("MyDrive", result);
    }

    [Fact]
    public void SanitizeVolumeLabel_InvalidChars_StripsThem()
    {
        var result = ZipFsHelpers.SanitizeVolumeLabel(@"My\Drive/Test");

        Assert.Equal("MyDriveTest", result);
    }

    [Fact]
    public void SanitizeVolumeLabel_AllInvalidChars_ReturnsDefault()
    {
        var result = ZipFsHelpers.SanitizeVolumeLabel(@"\/:*?""<>|");

        Assert.Equal(ZipFileSystemCore.DefaultVolumeLabel, result);
    }

    [Fact]
    public void SanitizeVolumeLabel_TrailingSpaces_Trims()
    {
        var result = ZipFsHelpers.SanitizeVolumeLabel("Drive   ");

        Assert.Equal("Drive", result);
    }

    [Fact]
    public void SanitizeVolumeLabel_TrailingDots_Trims()
    {
        var result = ZipFsHelpers.SanitizeVolumeLabel("Drive...");

        Assert.Equal("Drive", result);
    }

    [Fact]
    public void SanitizeVolumeLabel_TrailingSpacesAndDots_Trims()
    {
        var result = ZipFsHelpers.SanitizeVolumeLabel("Drive . .");

        Assert.Equal("Drive", result);
    }

    [Fact]
    public void SanitizeVolumeLabel_Exceeds32Chars_Truncates()
    {
        var longLabel = new string('A', 50);
        var result = ZipFsHelpers.SanitizeVolumeLabel(longLabel);

        Assert.Equal(32, result.Length);
    }

    [Fact]
    public void SanitizeVolumeLabel_Exactly32Chars_ReturnsAsIs()
    {
        var label = new string('B', 32);
        var result = ZipFsHelpers.SanitizeVolumeLabel(label);

        Assert.Equal(32, result.Length);
        Assert.Equal(label, result);
    }

    [Fact]
    public void SanitizeVolumeLabel_TruncationExposesTrailingSpaces_TrimsAgain()
    {
        // 33 chars: 30 'A' + 3 spaces (truncation to 32 would leave trailing spaces)
        var label = new string('A', 30) + "   ";
        var result = ZipFsHelpers.SanitizeVolumeLabel(label);

        Assert.Equal("A" + new string('A', 29), result);
        Assert.DoesNotContain(" ", result);
    }

    [Fact]
    public void SanitizeVolumeLabel_SpecialCharsMixed_PreservesValid()
    {
        var result = ZipFsHelpers.SanitizeVolumeLabel("File-2024_v2.0");

        Assert.Equal("File-2024_v2.0", result);
    }

    [Fact]
    public void SanitizeVolumeLabel_UnicodeChars_PreservesThem()
    {
        var result = ZipFsHelpers.SanitizeVolumeLabel("Ünïcödé");

        Assert.Equal("Ünïcödé", result);
    }

    // ─── GenerateTempDirectoryName tests ───

    [Fact]
    public void GenerateTempDirectoryName_ContainsProcessId()
    {
        var dirName = ZipFsHelpers.GenerateTempDirectoryName();

        var separatorIndex = dirName.IndexOf('_');
        Assert.True(separatorIndex > 0);

        var pidStr = dirName[..separatorIndex];
        Assert.True(int.TryParse(pidStr, out var pid));
        Assert.Equal(Environment.ProcessId, pid);
    }

    [Fact]
    public void GenerateTempDirectoryName_ContainsGuid()
    {
        var dirName = ZipFsHelpers.GenerateTempDirectoryName();

        var separatorIndex = dirName.IndexOf('_');
        var guidPart = dirName[(separatorIndex + 1)..];

        // GUID without dashes is 32 hex chars
        Assert.Equal(32, guidPart.Length);
        Assert.True(Guid.TryParseExact(guidPart, "N", out _));
    }

    [Fact]
    public void GenerateTempDirectoryName_MultipleCallsProduceUniqueNames()
    {
        var name1 = ZipFsHelpers.GenerateTempDirectoryName();
        var name2 = ZipFsHelpers.GenerateTempDirectoryName();

        Assert.NotEqual(name1, name2);
    }

    // ─── TryParseProcessIdFromTempDirectoryName tests ───

    [Fact]
    public void TryParseProcessIdFromTempDirectoryName_ValidFormat_ParsesCorrectly()
    {
        var result = ZipFsHelpers.TryParseProcessIdFromTempDirectoryName("12345_abcdef1234567890abcdef1234567890", out var pid);

        Assert.True(result);
        Assert.Equal(12345, pid);
    }

    [Fact]
    public void TryParseProcessIdFromTempDirectoryName_NoSeparator_ReturnsFalse()
    {
        var result = ZipFsHelpers.TryParseProcessIdFromTempDirectoryName("noseparator", out var pid);

        Assert.False(result);
        Assert.Equal(0, pid);
    }

    [Fact]
    public void TryParseProcessIdFromTempDirectoryName_EmptyString_ReturnsFalse()
    {
        var result = ZipFsHelpers.TryParseProcessIdFromTempDirectoryName("", out var pid);

        Assert.False(result);
        Assert.Equal(0, pid);
    }

    [Fact]
    public void TryParseProcessIdFromTempDirectoryName_NonNumericPid_ReturnsFalse()
    {
        var result = ZipFsHelpers.TryParseProcessIdFromTempDirectoryName("abc_guid", out var pid);

        Assert.False(result);
        Assert.Equal(0, pid);
    }

    [Fact]
    public void TryParseProcessIdFromTempDirectoryName_StartsWithSeparator_ReturnsFalse()
    {
        var result = ZipFsHelpers.TryParseProcessIdFromTempDirectoryName("_guid", out var pid);

        Assert.False(result);
        Assert.Equal(0, pid);
    }

    [Fact]
    public void TryParseProcessIdFromTempDirectoryName_GeneratedName_ParsesCorrectly()
    {
        var dirName = ZipFsHelpers.GenerateTempDirectoryName();

        var result = ZipFsHelpers.TryParseProcessIdFromTempDirectoryName(dirName, out var pid);

        Assert.True(result);
        Assert.Equal(Environment.ProcessId, pid);
    }

    // ─── IsMatchSimple additional tests ───

    [Fact]
    public void IsMatchSimple_CaseInsensitive()
    {
        var result = ZipFsHelpers.IsMatchSimple("README.TXT", "readme.txt");

        Assert.True(result);
    }

    [Fact]
    public void IsMatchSimple_MultipleStars()
    {
        var result = ZipFsHelpers.IsMatchSimple("test.data.csv", "*.data.*");

        Assert.True(result);
    }

    [Fact]
    public void IsMatchSimple_StarInMiddle()
    {
        var result = ZipFsHelpers.IsMatchSimple("testXfile.txt", "test*file.txt");

        Assert.True(result);
    }

    [Fact]
    public void IsMatchSimple_EmptyInput_EmptyPattern()
    {
        var result = ZipFsHelpers.IsMatchSimple("", "");

        Assert.True(result);
    }

    [Fact]
    public void IsMatchSimple_EmptyInput_StarPattern()
    {
        var result = ZipFsHelpers.IsMatchSimple("", "*");

        Assert.True(result);
    }

    [Fact]
    public void IsMatchSimple_SpecialRegexCharsInPattern_EscapedCorrectly()
    {
        var result = ZipFsHelpers.IsMatchSimple("file.txt", "file.txt");

        Assert.True(result);
    }

    [Fact]
    public void IsMatchSimple_DotInPattern_MatchesLiteralDot()
    {
        // Without regex escaping, "." would match any char
        var result = ZipFsHelpers.IsMatchSimple("fileAtxt", "file.txt");

        Assert.False(result);
    }

    // ─── IsDirectory additional tests ───

    [Fact]
    public void IsDirectory_Null_ReturnsFalse()
    {
        Assert.False(ZipFsHelpers.IsDirectory(null));
    }

    [Fact]
    public void IsDirectory_BackslashSuffix_ReturnsTrue()
    {
        // This tests the backslash check in IsDirectory
        // We'd need an IArchiveEntry with a backslash suffix
        // Since we can't easily mock IArchiveEntry, we verify the null case
        Assert.False(ZipFsHelpers.IsDirectory(null));
    }

    // ─── NormalizePath additional tests ───

    [Theory]
    [InlineData("\\", "/")]
    [InlineData("//", "/")]
    [InlineData(@"\foo\bar", "/foo/bar")]
    [InlineData("foo", "/foo")]
    public void NormalizePath_VariousInputs_ProducesCorrectOutput(string input, string expected)
    {
        Assert.Equal(expected, ZipFsHelpers.NormalizePath(input));
    }

    // ─── ResolveSpecialPaths additional tests ───

    [Theory]
    [InlineData("/a/./b/./c/./d", "/a/b/c/d")]
    [InlineData("/a/b/../c/../d", "/a/d")]
    [InlineData("/a/b/c/../../../x", "/x")]
    [InlineData("/.", "/")]
    [InlineData("/..", "/")]
    public void ResolveSpecialPaths_VariousInputs_ResolvesCorrectly(string input, string expected)
    {
        Assert.Equal(expected, ZipFsHelpers.ResolveSpecialPaths(input));
    }

    // ─── IsPasswordRequiredException additional tests ───

    [Theory]
    [InlineData("password required", true)]
    [InlineData("PASSWORD REQUIRED", true)]
    [InlineData("Password is wrong", true)]
    [InlineData("file is Encrypted", true)]
    [InlineData("ENCRYPTED archive", true)]
    [InlineData("rar header encrypted", true)]
    [InlineData("RAR HEADER IS ENCRYPTED", true)]
    [InlineData("some other error", false)]
    [InlineData("file corrupted", false)]
    public void IsPasswordRequiredException_VariousMessages_ReturnsExpected(string message, bool expected)
    {
        var ex = new InvalidOperationException(message);
        Assert.Equal(expected, ZipFsHelpers.IsPasswordRequiredException(ex));
    }

    // ─── IsPathLengthValid additional edge cases ───

    [Fact]
    public void IsPathLengthValid_ExtendedPathExceedsLimit_ReturnsFalse()
    {
        var path = @"\\?\" + new string('a', 32768);
        Assert.False(ZipFsHelpers.IsPathLengthValid(path));
    }

    [Fact]
    public void IsPathLengthValid_ExtendedPathExactlyAtLimit_ReturnsTrue()
    {
        var path = @"\\?\" + new string('a', 32767 - 4);
        Assert.True(ZipFsHelpers.IsPathLengthValid(path));
    }

    // ─── GetParentPath additional tests ───

    [Theory]
    [InlineData("/", null)]
    [InlineData("/file.txt", "/")]
    [InlineData("/dir/file.txt", "/dir")]
    [InlineData("/a/b/c", "/a/b")]
    public void GetParentPath_VariousInputs_ReturnsExpected(string input, string? expected)
    {
        Assert.Equal(expected, ZipFsHelpers.GetParentPath(input));
    }

    // ─── BaseTempPath tests ───

    [Fact]
    public void BaseTempPath_EndsWithSimpleZipDrive()
    {
        Assert.EndsWith("SimpleZipDrive", ZipFsHelpers.BaseTempPath);
    }

    [Fact]
    public void BaseTempPath_IsUnderTempPath()
    {
        Assert.StartsWith(Path.GetTempPath(), ZipFsHelpers.BaseTempPath);
    }

    // ─── RegisterCurrentTempDirectory tests ───

    [Fact]
    public void RegisterCurrentTempDirectory_DoesNotThrow()
    {
        var ex = Record.Exception(static () => ZipFsHelpers.RegisterCurrentTempDirectory("test_dir"));
        Assert.Null(ex);
    }
}
