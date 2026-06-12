using SimpleZipDrive.Core;

namespace SimpleZipDrive.Tests;

public class RegexCacheAndHelpersEdgeCaseTests
{
    // ─── IsMatchSimple: LRU cache eviction under pressure ───

    [Fact]
    public void IsMatchSimple_ManyDifferentPatterns_HandledGracefully()
    {
        // Generate more patterns than the cache can hold (max 100)
        for (var i = 0; i < 150; i++)
        {
            var pattern = $"*.{i:D3}";
            var input = $"file.{i:D3}";
            Assert.True(ZipFsHelpers.IsMatchSimple(input, pattern));
        }

        // Verify old patterns still work after eviction
        Assert.True(ZipFsHelpers.IsMatchSimple("file.001", "*.001"));
        Assert.False(ZipFsHelpers.IsMatchSimple("file.002", "*.001"));
    }

    // ─── IsMatchSimple: LRU order maintained with different patterns ───

    [Fact]
    public void IsMatchSimple_AccessOrder_MaintainsLRU()
    {
        // Use different patterns to verify LRU tracking works
        var resultA = ZipFsHelpers.IsMatchSimple("test.txt", "test.*");
        var resultB = ZipFsHelpers.IsMatchSimple("other.md", "other.*");
        var resultC = ZipFsHelpers.IsMatchSimple("file.bin", "file.*");

        Assert.True(resultA);
        Assert.True(resultB);
        Assert.True(resultC);

        // Verify cache hits work after multiple patterns
        Assert.True(ZipFsHelpers.IsMatchSimple("test.xyz", "test.*"));
        Assert.False(ZipFsHelpers.IsMatchSimple("test.txt", "other.*"));
    }

    // ─── IsMatchSimple: star at multiple positions ───

    [Theory]
    [InlineData("hello", "*hello", true)]
    [InlineData("hello", "hello*", true)]
    [InlineData("hello", "*hello*", true)]
    [InlineData("hello world", "*world", true)]
    [InlineData("hello world", "hello*", true)]
    [InlineData("hello world", "*lo wo*", true)]
    [InlineData("hello", "*xyz*", false)]
    [InlineData("hello", "xyz*", false)]
    [InlineData("hello", "*xyz", false)]
    public void IsMatchSimple_StarPositions_MatchCorrectly(string input, string pattern, bool expected)
    {
        Assert.Equal(expected, ZipFsHelpers.IsMatchSimple(input, pattern));
    }

    // ─── IsMatchSimple: question mark in various positions ───

    [Theory]
    [InlineData("abc", "?bc", true)]
    [InlineData("abc", "a?c", true)]
    [InlineData("abc", "ab?", true)]
    [InlineData("abcd", "?bc", false)]
    [InlineData("abcd", "ab?", false)]
    [InlineData("abc", "????", false)]
    [InlineData("abcd", "????", true)]
    public void IsMatchSimple_QuestionMark_MatchesCorrectly(string input, string pattern, bool expected)
    {
        Assert.Equal(expected, ZipFsHelpers.IsMatchSimple(input, pattern));
    }

    // ─── IsMatchSimple: mixed wildcards ───

    [Theory]
    [InlineData("test.txt", "*.txt", true)]
    [InlineData("test.txt", "t*.txt", true)]
    [InlineData("test.txt", "t*.t?t", true)]
    [InlineData("test.txt", "?est.*", true)]
    [InlineData("test.txt", "*.*t", true)]
    [InlineData("test.txt", "*.md", false)]
    [InlineData("test.txt", "?.*.txt", false)]
    public void IsMatchSimple_MixedWildcards_MatchCorrectly(string input, string pattern, bool expected)
    {
        Assert.Equal(expected, ZipFsHelpers.IsMatchSimple(input, pattern));
    }

    // ─── IsMatchSimple: unicode characters ───

    [Fact]
    public void IsMatchSimple_UnicodeCharacters_MatchCorrectly()
    {
        Assert.True(ZipFsHelpers.IsMatchSimple("файл.txt", "*.txt"));
        Assert.True(ZipFsHelpers.IsMatchSimple("файл.txt", "файл.*"));
        Assert.False(ZipFsHelpers.IsMatchSimple("файл.txt", "other.*"));
    }

    // ─── IsMatchSimple: special regex characters are escaped ───

    [Theory]
    [InlineData("file.txt", "file.txt", true)]
    [InlineData("fileAtxt", "file.txt", false)] // . should not match A
    [InlineData("file+plus.txt", "file+plus.txt", true)]
    [InlineData("file[1].txt", "file[1].txt", true)]
    [InlineData("file(1).txt", "file(1).txt", true)]
    [InlineData("file{1}.txt", "file{1}.txt", true)]
    [InlineData("file^start.txt", "file^start.txt", true)]
    [InlineData("file$end.txt", "file$end.txt", true)]
    public void IsMatchSimple_SpecialRegexChars_EscapedCorrectly(string input, string pattern, bool expected)
    {
        Assert.Equal(expected, ZipFsHelpers.IsMatchSimple(input, pattern));
    }

    // ─── IsMatchSimple: very long input ───

    [Fact]
    public void IsMatchSimple_VeryLongInput_MatchesCorrectly()
    {
        var longName = new string('a', 200) + ".txt";
        Assert.True(ZipFsHelpers.IsMatchSimple(longName, "*.txt"));
        Assert.False(ZipFsHelpers.IsMatchSimple(longName, "*.md"));
    }

    // ─── SanitizeVolumeLabel: all invalid characters ───

    [Fact]
    public void SanitizeVolumeLabel_AllInvalidChars_ReturnsDefault()
    {
        var result = ZipFsHelpers.SanitizeVolumeLabel(@"\/*?""<>|:");
        Assert.Equal(ZipFileSystemCore.DefaultVolumeLabel, result);
    }

    // ─── SanitizeVolumeLabel: mixed valid and invalid ───

    [Fact]
    public void SanitizeVolumeLabel_MixedValidInvalid_StripsInvalid()
    {
        var result = ZipFsHelpers.SanitizeVolumeLabel("My:Drive/Test");
        Assert.Equal("MyDriveTest", result);
    }

    // ─── SanitizeFolderName: exactly at boundary ───

    [Fact]
    public void SanitizeFolderName_ExactlyAt200Chars_ReturnsAsIs()
    {
        var name = new string('X', 200);
        var result = ZipFsHelpers.SanitizeFolderName(name);
        Assert.Equal(200, result.Length);
    }

    // ─── SanitizeFolderName: one over boundary ───

    [Fact]
    public void SanitizeFolderName_OneOver200_Truncates()
    {
        var name = new string('X', 201);
        var result = ZipFsHelpers.SanitizeFolderName(name);
        Assert.Equal(200, result.Length);
    }

    // ─── ResolveSpecialPaths: only dots ───

    [Theory]
    [InlineData("/.", "/")]
    [InlineData("/..", "/")]
    [InlineData("/./..", "/")]
    [InlineData("/../.", "/")]
    [InlineData("/././.", "/")]
    [InlineData("/../../..", "/")]
    public void ResolveSpecialPaths_OnlyDots_ResolvesToRoot(string input, string expected)
    {
        Assert.Equal(expected, ZipFsHelpers.ResolveSpecialPaths(input));
    }

    // ─── GetParentPath: root returns null ───

    [Fact]
    public void GetParentPath_Root_ReturnsNull()
    {
        Assert.Null(ZipFsHelpers.GetParentPath("/"));
    }

    // ─── GetParentPath: single segment ───

    [Fact]
    public void GetParentPath_SingleSegment_ReturnsRoot()
    {
        Assert.Equal("/", ZipFsHelpers.GetParentPath("/file.txt"));
    }

    // ─── IsPathLengthValid: extended path prefix detection ───

    [Fact]
    public void IsPathLengthValid_ExtendedPathPrefix_DetectedCorrectly()
    {
        // \\?\ prefix allows up to 32767 chars
        var path = @"\\?\" + new string('a', 260);
        Assert.True(ZipFsHelpers.IsPathLengthValid(path));
    }

    // ─── IsPathLengthValid: just under standard limit ───

    [Fact]
    public void IsPathLengthValid_JustUnderLimit_ReturnsTrue()
    {
        var path = new string('a', 259);
        Assert.True(ZipFsHelpers.IsPathLengthValid(path));
    }

    // ─── IsPasswordRequiredException: case insensitive ───

    [Theory]
    [InlineData("PASSWORD required", true)]
    [InlineData("Password Required", true)]
    [InlineData("ENCRYPTED data", true)]
    [InlineData("Encrypted", true)]
    [InlineData("RAR HEADER encrypted", true)]
    [InlineData("rar header ENCRYPTED", true)]
    public void IsPasswordRequiredException_CaseInsensitive_WorksCorrectly(string message, bool expected)
    {
        Assert.Equal(expected, ZipFsHelpers.IsPasswordRequiredException(new InvalidOperationException(message)));
    }

    // ─── IsDataErrorException: type name detection via custom exception ───

    [Fact]
    public void IsDataErrorException_TypeNameWithDataError_ReturnsTrue()
    {
        var ex = new TestDataErrorException("test");
        Assert.True(ZipFsHelpers.IsDataErrorException(ex));
    }

    private class TestDataErrorException : Exception
    {
        public TestDataErrorException(string message) : base(message) { }
    }

    // ─── GenerateTempDirectoryName: format validation ───

    [Fact]
    public void GenerateTempDirectoryName_FormatIsValid()
    {
        var name = ZipFsHelpers.GenerateTempDirectoryName();

        // Should be {pid}_{guid}
        var parts = name.Split('_');
        Assert.Equal(2, parts.Length);
        Assert.True(int.TryParse(parts[0], out _));
        Assert.Equal(32, parts[1].Length);
    }

    // ─── TryParseProcessIdFromTempDirectoryName: very large PID ───

    [Fact]
    public void TryParseProcessIdFromTempDirectoryName_VeryLargePid_ParsesCorrectly()
    {
        var result = ZipFsHelpers.TryParseProcessIdFromTempDirectoryName("999999_abcdef1234567890abcdef1234567890", out var pid);

        Assert.True(result);
        Assert.Equal(999999, pid);
    }

    // ─── BaseTempPath: consistent across calls ───

    [Fact]
    public void BaseTempPath_ConsistentAcrossCalls()
    {
        var path1 = ZipFsHelpers.BaseTempPath;
        var path2 = ZipFsHelpers.BaseTempPath;

        Assert.Equal(path1, path2);
    }
}
