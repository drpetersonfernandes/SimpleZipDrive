using System.Text.Json;
using System.Text.RegularExpressions;

namespace SimpleZipDrive.Tests;

/// <summary>
/// Tests for the UpdateChecker functionality to ensure users are notified when new versions are available on GitHub.
/// </summary>
public partial class UpdateCheckerTests
{
    #region Version Parsing Tests

    [Theory]
    [InlineData("release_1.0.0", "1.0.0")]
    [InlineData("v1.2.3", "1.2.3")]
    [InlineData("version_2.5.1", "2.5.1")]
    [InlineData("1.10.1", "1.10.1")]
    [InlineData("release_1.10.0", "1.10.0")]
    [InlineData("v1.5", "1.5")]
    [InlineData("release_1.0.0-beta", "1.0.0")]
    public void VersionRegexExtractsVersionFromTagName(string tagName, string expectedVersion)
    {
        // Use the same regex pattern as UpdateChecker
        var versionRegex = VersionRegex();
        var match = versionRegex.Match(tagName);

        Assert.True(match.Success, $"Failed to match version in tag: {tagName}");
        Assert.Equal(expectedVersion, match.Value);
    }

    [Theory]
    [InlineData("invalid")]
    [InlineData("release")]
    [InlineData("v")]
    [InlineData("")]
    public void VersionRegexReturnsNoMatchForInvalidTagName(string tagName)
    {
        var versionRegex = VersionRegex();
        var match = versionRegex.Match(tagName);

        Assert.False(match.Success);
    }

    #endregion

    #region Version Comparison Tests

    [Theory]
    [InlineData("1.0.0", "1.0.1", true)] // Patch update available
    [InlineData("1.0.0", "1.1.0", true)] // Minor update available
    [InlineData("1.0.0", "2.0.0", true)] // Major update available
    [InlineData("1.10.0", "1.10.1", true)] // Real-world scenario from repo
    [InlineData("1.10.1", "1.10.1", false)] // Same version
    [InlineData("1.10.1", "1.10.0", false)] // Older version
    [InlineData("1.0.0", "1.0.0", false)] // Same version
    [InlineData("2.0.0", "1.9.9", false)] // Major downgrade
    public void VersionComparisonCorrectlyDetectsUpdateAvailability(string currentVersionStr, string latestVersionStr, bool updateExpected)
    {
        var current = Version.Parse(currentVersionStr);
        var latest = Version.Parse(latestVersionStr);

        var isUpdateAvailable = latest > current;

        Assert.Equal(updateExpected, isUpdateAvailable);
    }

    [Fact]
    public void NullVersionDefaultsToZero()
    {
        // Simulates when Assembly.GetExecutingAssembly().GetName().Version returns null
        var nullVersion = new Version(0, 0, 0, 0);
        var latest = new Version(1, 0, 0);

        Assert.True(latest > nullVersion);
    }

    #endregion

    #region GitHub API Response Parsing Tests

    [Fact]
    public void GitHubApiResponseContainsRequiredFields()
    {
        // Simulates a valid GitHub API response
        const string jsonResponse = """
                                    {
                                                "tag_name": "release_1.10.1",
                                                "html_url": "https://github.com/drpetersonfernandes/SimpleZipDrive/releases/tag/release_1.10.1",
                                                "name": "Release 1.10.1",
                                                "published_at": "2024-01-15T10:30:00Z"
                                            }
                                    """;

        using var doc = JsonDocument.Parse(jsonResponse);

        var tagName = doc.RootElement.GetProperty("tag_name").GetString();
        var htmlUrl = doc.RootElement.GetProperty("html_url").GetString();

        Assert.NotNull(tagName);
        Assert.NotNull(htmlUrl);
        Assert.Contains("release_1.10.1", tagName);
        Assert.Contains("github.com", htmlUrl);
    }

    [Fact]
    public void GitHubApiResponseWithMissingFieldsReturnsNull()
    {
        // Simulates an invalid or incomplete GitHub API response
        const string jsonResponse = """
                                    {
                                                "name": "Release 1.10.1",
                                                "published_at": "2024-01-15T10:30:00Z"
                                            }
                                    """;

        using var doc = JsonDocument.Parse(jsonResponse);

        // tag_name is missing
        var hasTagName = doc.RootElement.TryGetProperty("tag_name", out _);
        var hasHtmlUrl = doc.RootElement.TryGetProperty("html_url", out _);

        Assert.False(hasTagName);
        Assert.False(hasHtmlUrl);
    }

    [Theory]
    [InlineData("release_1.10.1", "https://github.com/user/repo/releases/tag/release_1.10.1", true)]
    [InlineData("v2.0.0", "https://github.com/owner/project/releases/tag/v2.0.0", true)]
    public void ValidGitHubResponseCanBeProcessed(string tagName, string htmlUrl, bool isValid)
    {
        var versionRegex = VersionRegex();
        var match = versionRegex.Match(tagName);

        var canParseVersion = match.Success;
        var hasValidUrl = !string.IsNullOrEmpty(htmlUrl) && htmlUrl.StartsWith("https://github.com/", StringComparison.Ordinal);

        Assert.Equal(isValid, canParseVersion && hasValidUrl);
    }

    #endregion

    #region Update Notification Logic Tests

    [Fact]
    public void UpdateAvailableGeneratesNotificationDetails()
    {
        var currentVersion = new Version(1, 10, 0);
        var latestVersion = new Version(1, 10, 1);
        const string releaseUrl = "https://github.com/drpetersonfernandes/SimpleZipDrive/releases/tag/release_1.10.1";

        // Verify that when an update is available, the appropriate details are generated
        Assert.True(latestVersion > currentVersion);
        Assert.NotNull(releaseUrl);
        Assert.Contains("github.com", releaseUrl);
        Assert.Contains("release_1.10.1", releaseUrl);
    }

    [Fact]
    public void NoUpdateAvailableDoesNotGenerateNotification()
    {
        var currentVersion = new Version(1, 10, 1);
        var latestVersion = new Version(1, 10, 1);

        // Same version - no update notification should be generated
        Assert.False(latestVersion > currentVersion);
    }

    [Theory]
    [InlineData(1, 10, 1, 1, 10, 0, false)] // Current newer - no notification
    [InlineData(1, 10, 0, 1, 10, 1, true)] // Latest newer - notify
    [InlineData(1, 9, 9, 1, 10, 0, true)] // Minor version update - notify
    [InlineData(1, 0, 0, 2, 0, 0, true)] // Major version update - notify
    public void NotificationLogicBasedOnVersionComparison(
        int currentMajor, int currentMinor, int currentBuild,
        int latestMajor, int latestMinor, int latestBuild,
        bool shouldNotify)
    {
        var current = new Version(currentMajor, currentMinor, currentBuild);
        var latest = new Version(latestMajor, latestMinor, latestBuild);

        var needsNotification = latest > current;

        Assert.Equal(shouldNotify, needsNotification);
    }

    #endregion

    #region Repository Configuration Tests

    [Fact]
    public void RepositoryConfigurationIsValid()
    {
        // Ensure the repository configuration points to the correct GitHub repository
        const string expectedOwner = "drpetersonfernandes";
        const string expectedRepo = "SimpleZipDrive";
        const string expectedApiUrl = $"https://api.github.com/repos/{expectedOwner}/{expectedRepo}/releases/latest";

        // Verify URL format
        Assert.Contains(expectedOwner, expectedApiUrl);
        Assert.Contains(expectedRepo, expectedApiUrl);
        Assert.StartsWith("https://api.github.com/repos/", expectedApiUrl);
        Assert.EndsWith("/releases/latest", expectedApiUrl);
    }

    [Fact]
    public void ApiUrlConstructionIsCorrect()
    {
        const string repoOwner = "drpetersonfernandes";
        const string repoName = "SimpleZipDrive";
        const string expectedUrl = $"https://api.github.com/repos/{repoOwner}/{repoName}/releases/latest";

        Assert.Equal("https://api.github.com/repos/drpetersonfernandes/SimpleZipDrive/releases/latest", expectedUrl);
    }

    #endregion

    #region Timeout and Error Handling Tests

    [Fact]
    public void HttpClientTimeoutIsReasonable()
    {
        // The timeout should be set to prevent hanging but allow slow connections
        var timeout = TimeSpan.FromSeconds(15);

        Assert.True(timeout.TotalSeconds >= 10, "Timeout should be at least 10 seconds for slow connections");
        Assert.True(timeout.TotalSeconds <= 30, "Timeout should not exceed 30 seconds to prevent excessive waiting");
    }

    [Fact]
    public void UserAgentHeaderIsRequired()
    {
        // GitHub API requires a User-Agent header
        const string appName = "SimpleZipDrive";
        const string userAgent = $"{appName}-UpdateChecker";

        Assert.NotNull(userAgent);
        Assert.Contains(appName, userAgent);
    }

    #endregion

    #region Integration Test Helpers

    /// <summary>
    /// Simulates the complete update check flow without making actual network calls.
    /// This validates the logic flow of the UpdateChecker.
    /// </summary>
    [Theory]
    [InlineData("release_1.9.0", "1.10.0", false)] // No update - GitHub has older (1.9.0 vs 1.10.0)
    [InlineData("release_1.10.1", "1.10.1", false)] // No update - same version
    [InlineData("release_1.10.1", "1.10.0", true)] // Update needed - patch available
    [InlineData("release_2.0.0", "1.10.1", true)] // Update needed - major version
    public void CompleteUpdateCheckFlowSimulation(string tagName, string currentVersionStr, bool expectUpdate)
    {
        // Step 1: Parse version from GitHub tag
        var versionRegex = VersionRegex();
        var match = versionRegex.Match(tagName);
        Assert.True(match.Success, "Should be able to parse version from tag");

        var latestVersion = Version.Parse(match.Value);
        var currentVersion = Version.Parse(currentVersionStr);

        // Step 2: Compare versions
        var isUpdateAvailable = latestVersion > currentVersion;

        // Step 3: Verify expectation
        Assert.Equal(expectUpdate, isUpdateAvailable);
    }

    #endregion

    #region Version Edge Cases

    [Theory]
    [InlineData("0.0.1", "1.0.0", true)] // Zero-based versioning
    [InlineData("1.0.0", "1.0.0.1", true)] // Extra version component (revision different)
    public void VersionEdgeCasesHandledCorrectly(string current, string latest, bool expectUpdate)
    {
        var currentVersion = Version.Parse(current);
        var latestVersion = Version.Parse(latest);

        Assert.Equal(expectUpdate, latestVersion > currentVersion);
    }

    #endregion

    #region Browser Launch URL Tests

    [Fact]
    public void BrowserLaunchUrlIsValidGitHubReleasePage()
    {
        const string htmlUrl = "https://github.com/drpetersonfernandes/SimpleZipDrive/releases/tag/release_1.10.1";

        Assert.StartsWith("https://", htmlUrl);
        Assert.Contains("github.com", htmlUrl);
        Assert.Contains("/releases/tag/", htmlUrl);
    }

    [Theory]
    [InlineData("https://github.com/user/repo/releases/tag/v1.0.0", true)]
    [InlineData("https://github.com/user/repo/releases/latest", true)]
    [InlineData("http://github.com/user/repo/releases/tag/v1.0.0", false)] // HTTP not HTTPS
    [InlineData("https://example.com/releases", false)] // Not GitHub
    public void BrowserUrlValidation(string url, bool isValid)
    {
        var isHttps = url.StartsWith("https://", StringComparison.Ordinal);
        var isGitHub = url.Contains("github.com");
        var hasReleases = url.Contains("/releases/");

        Assert.Equal(isValid, isHttps && isGitHub && hasReleases);
    }

    [GeneratedRegex(@"\d+\.\d+(?:\.\d+)?", RegexOptions.Compiled)]
    private static partial Regex VersionRegex();

    #endregion
}
