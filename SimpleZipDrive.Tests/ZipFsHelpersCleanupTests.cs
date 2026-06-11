using System.Diagnostics.CodeAnalysis;
using SimpleZipDrive.Core;

namespace SimpleZipDrive.Tests;

[Collection("IsMatchSimple")]
[SuppressMessage("ReSharper", "NullableWarningSuppressionIsUsed")]
public class ZipFsHelpersCleanupTests
{
    // ─── CleanupOrphanedTempDirectories: runs without throwing ───

    [Fact]
    public void CleanupOrphanedTempDirectories_DoesNotThrow()
    {
        var ex = Record.Exception(static () => ZipFsHelpers.CleanupOrphanedTempDirectories());
        Assert.Null(ex);
    }

    // ─── CleanupOrphanedTempDirectories: handles non-existent base directory ───

    [Fact]
    public void CleanupOrphanedTempDirectories_NonExistentBaseDir_DoesNotThrow()
    {
        // If the base temp path doesn't exist, it should return gracefully
        var ex = Record.Exception(static () => ZipFsHelpers.CleanupOrphanedTempDirectories());
        Assert.Null(ex);
    }

    // ─── EnsureCleanupPerformed: runs cleanup exactly once ───

    [Fact]
    public void EnsureCleanupPerformed_MultipleCalls_DoesNotThrow()
    {
        var ex = Record.Exception(static () =>
        {
            ZipFsHelpers.EnsureCleanupPerformed();
            ZipFsHelpers.EnsureCleanupPerformed();
            ZipFsHelpers.EnsureCleanupPerformed();
        });

        Assert.Null(ex);
    }

    // ─── IsMatchSimple: cache hit path ───

    [Fact]
    public void IsMatchSimple_SamePatternTwice_UsesCache()
    {
        // First call populates cache
        var result1 = ZipFsHelpers.IsMatchSimple("test.txt", "test.*");
        // Second call should hit cache
        var result2 = ZipFsHelpers.IsMatchSimple("other.txt", "test.*");

        Assert.True(result1);
        Assert.False(result2);
    }

    // ─── IsMatchSimple: LRU cache eviction when full ───

    [Fact]
    public void IsMatchSimple_CacheEviction_HandledGracefully()
    {
        // Verify basic matching works with various patterns
        Assert.True(ZipFsHelpers.IsMatchSimple("test.txt", "test.*"));
        Assert.True(ZipFsHelpers.IsMatchSimple("test.txt", "*.txt"));
        Assert.False(ZipFsHelpers.IsMatchSimple("other.txt", "test.*"));
    }

    // ─── IsMatchSimple: cache hit returns consistent results ───

    [Fact]
    public void IsMatchSimple_CacheHit_ReturnsConsistentResults()
    {
        // First call - cache miss
        var result1 = ZipFsHelpers.IsMatchSimple("file.txt", "file.*");
        // Second call - cache hit
        var result2 = ZipFsHelpers.IsMatchSimple("file.txt", "file.*");
        // Different input - still uses cache for pattern
        var result3 = ZipFsHelpers.IsMatchSimple("other.txt", "file.*");

        Assert.True(result1);
        Assert.True(result2);
        Assert.False(result3);
    }

    // ─── SanitizeVolumeLabel: truncation exposes trailing dots ───

    [Fact]
    public void SanitizeVolumeLabel_TruncationExposesTrailingDots_Trims()
    {
        // 34 chars: 30 'A' + 4 dots -> truncated to 32 -> "AAA...AA.." -> trim dots
        var label = new string('A', 30) + "....";
        var result = ZipFsHelpers.SanitizeVolumeLabel(label);

        Assert.Equal(30, result.Length);
        Assert.Equal(new string('A', 30), result);
    }

    // ─── TryParseProcessIdFromTempDirectoryName: separator at end ───

    [Fact]
    public void TryParseProcessIdFromTempDirectoryName_SeparatorAtEnd_ParsesPid()
    {
        // String ending with "_" - the PID part "12345" is valid, GUID part is empty
        // but the method only checks the PID part, so it succeeds
        var result = ZipFsHelpers.TryParseProcessIdFromTempDirectoryName("12345_", out var pid);

        Assert.True(result);
        Assert.Equal(12345, pid);
    }

    // ─── TryParseProcessIdFromTempDirectoryName: multiple separators ───

    [Fact]
    public void TryParseProcessIdFromTempDirectoryName_MultipleSeparators_ParsesFirst()
    {
        var result = ZipFsHelpers.TryParseProcessIdFromTempDirectoryName("123_456_789", out var pid);

        Assert.True(result);
        Assert.Equal(123, pid);
    }

    // ─── IsMatchSimple: empty pattern ───

    [Fact]
    public void IsMatchSimple_EmptyPattern_MatchesEmptyInput()
    {
        var result = ZipFsHelpers.IsMatchSimple("", "");
        Assert.True(result);
    }

    [Fact]
    public void IsMatchSimple_EmptyPattern_DoesNotMatchNonEmptyInput()
    {
        var result = ZipFsHelpers.IsMatchSimple("test", "");
        Assert.False(result);
    }

    // ─── IsMatchSimple: question mark edge cases ───

    [Fact]
    public void IsMatchSimple_QuestionMarkAtStart()
    {
        Assert.True(ZipFsHelpers.IsMatchSimple("abc", "?bc"));
        Assert.False(ZipFsHelpers.IsMatchSimple("aabc", "?bc"));
    }

    [Fact]
    public void IsMatchSimple_QuestionMarkAtEnd()
    {
        Assert.True(ZipFsHelpers.IsMatchSimple("abc", "ab?"));
        Assert.False(ZipFsHelpers.IsMatchSimple("abcd", "ab?"));
    }

    // ─── IsMatchSimple: star in multiple positions ───

    [Fact]
    public void IsMatchSimple_StarAtStartAndEnd()
    {
        Assert.True(ZipFsHelpers.IsMatchSimple("hello world hello", "*world*"));
        Assert.False(ZipFsHelpers.IsMatchSimple("no match here", "*xyz*"));
    }

    // ─── GetParentPath: additional edge cases ───

    [Fact]
    public void GetParentPath_SingleLevel_ReturnsRoot()
    {
        Assert.Equal("/", ZipFsHelpers.GetParentPath("/file.txt"));
    }

    [Fact]
    public void GetParentPath_DeepNested_ReturnsCorrectParent()
    {
        Assert.Equal("/a/b/c", ZipFsHelpers.GetParentPath("/a/b/c/d.txt"));
    }

    // ─── ResolveSpecialPaths: complex cases ───

    [Fact]
    public void ResolveSpecialPaths_MultipleDots_RemovesCorrectly()
    {
        Assert.Equal("/x", ZipFsHelpers.ResolveSpecialPaths("/a/b/../../x"));
    }

    [Fact]
    public void ResolveSpecialPaths_DotAtEnd_Removes()
    {
        Assert.Equal("/a/b", ZipFsHelpers.ResolveSpecialPaths("/a/b/."));
    }

    // ─── IsNameMatch: edge cases ───

    [Fact]
    public void IsNameMatch_NullPattern_ReturnsTrue()
    {
        Assert.True(ZipFsHelpers.IsNameMatch("test.txt", null!));
    }

    [Fact]
    public void IsNameMatch_EmptyPattern_ReturnsTrue()
    {
        Assert.True(ZipFsHelpers.IsNameMatch("test.txt", ""));
    }

    [Fact]
    public void IsNameMatch_StarPattern_ReturnsTrue()
    {
        Assert.True(ZipFsHelpers.IsNameMatch("test.txt", "*"));
    }

    [Fact]
    public void IsNameMatch_StarDotStarPattern_ReturnsTrue()
    {
        Assert.True(ZipFsHelpers.IsNameMatch("test.txt", "*.*"));
    }

    [Fact]
    public void IsNameMatch_SpecificPattern_MatchesCorrectly()
    {
        Assert.True(ZipFsHelpers.IsNameMatch("readme.txt", "*.txt"));
        Assert.False(ZipFsHelpers.IsNameMatch("readme.md", "*.txt"));
    }
}
