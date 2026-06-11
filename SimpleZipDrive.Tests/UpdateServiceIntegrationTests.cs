using System.Diagnostics.CodeAnalysis;
using System.Net;
using System.Reflection;
using System.Text.Json;
using SimpleZipDrive.Core.Services;
using SimpleZipDrive.Tests.Fakes;

namespace SimpleZipDrive.Tests;

/// <summary>
/// Integration tests for the UpdateService that verify the full update check flow
/// with mocked HTTP responses.
/// </summary>
[SuppressMessage("ReSharper", "NullableWarningSuppressionIsUsed")]
public class UpdateServiceIntegrationTests
{
    private readonly FakeUserNotificationService _fakeNotificationService = new();

    private readonly Version _currentVersion = Assembly.GetExecutingAssembly().GetName().Version
                                               ?? new Version(0, 0, 0, 0);

    #region Helper Methods

    private static HttpClient CreateMockHttpClient(string jsonContent, HttpStatusCode statusCode = HttpStatusCode.OK)
    {
        var handler = new MockHttpMessageHandler(jsonContent, statusCode);
        return new HttpClient(handler);
    }

    private static HttpClient CreateMockHttpClient(Exception exception)
    {
        var handler = new MockHttpMessageHandler(exception);
        return new HttpClient(handler);
    }

    private static string CreateGitHubReleaseJson(string tagName, string htmlUrl = "https://github.com/drpetersonfernandes/SimpleZipDrive/releases/tag/test")
    {
        return JsonSerializer.Serialize(new
        {
            tag_name = tagName,
            html_url = htmlUrl,
            name = $"Release {tagName}",
            published_at = "2024-01-15T10:30:00Z"
        });
    }

    #endregion

    #region Update Available Tests

    [Fact]
    public async Task CheckForUpdateAsync_WhenNewerVersionAvailable_NotifiesUser()
    {
        // Arrange - use a version that's definitely higher than the current assembly version
        const string tagName = "release_99.0.1";
        const string htmlUrl = $"https://github.com/drpetersonfernandes/SimpleZipDrive/releases/tag/{tagName}";
        var json = CreateGitHubReleaseJson(tagName, htmlUrl);

        using var httpClient = CreateMockHttpClient(json);
        var updateService = new UpdateService(_fakeNotificationService, httpClient);

        // Act
        await updateService.CheckForUpdateAsync(CancellationToken.None);

        // Assert
        Assert.True(_fakeNotificationService.ShowUpdateAvailableCalled);
        Assert.Equal(new Version(99, 0, 1), _fakeNotificationService.CalledWithLatestVersion);
        Assert.Equal(htmlUrl, _fakeNotificationService.CalledWithDownloadUrl);
    }

    [Fact]
    public async Task CheckForUpdateAsync_WhenMajorVersionUpdate_NotifiesUser()
    {
        // Arrange
        const string tagName = "release_99.0.0";
        const string htmlUrl = $"https://github.com/drpetersonfernandes/SimpleZipDrive/releases/tag/{tagName}";
        var json = CreateGitHubReleaseJson(tagName, htmlUrl);

        using var httpClient = CreateMockHttpClient(json);
        var updateService = new UpdateService(_fakeNotificationService, httpClient);

        // Act
        await updateService.CheckForUpdateAsync(CancellationToken.None);

        // Assert
        Assert.True(_fakeNotificationService.ShowUpdateAvailableCalled);
        Assert.Equal(new Version(99, 0, 0), _fakeNotificationService.CalledWithLatestVersion);
    }

    [Fact]
    public async Task CheckForUpdateAsync_WhenMinorVersionUpdate_NotifiesUser()
    {
        // Arrange - use a version that's definitely higher than the current assembly version
        const string tagName = "v99.1.0";
        const string htmlUrl = $"https://github.com/drpetersonfernandes/SimpleZipDrive/releases/tag/{tagName}";
        var json = CreateGitHubReleaseJson(tagName, htmlUrl);

        using var httpClient = CreateMockHttpClient(json);
        var updateService = new UpdateService(_fakeNotificationService, httpClient);

        // Act
        await updateService.CheckForUpdateAsync(CancellationToken.None);

        // Assert
        Assert.True(_fakeNotificationService.ShowUpdateAvailableCalled);
    }

    #endregion

    #region No Update Available Tests

    [Fact]
    public async Task CheckForUpdateAsync_WhenSameVersion_DoesNotNotifyUser()
    {
        // Arrange
        var tagName = $"release_{_currentVersion}";
        var json = CreateGitHubReleaseJson(tagName);

        using var httpClient = CreateMockHttpClient(json);
        var updateService = new UpdateService(_fakeNotificationService, httpClient);

        // Act
        await updateService.CheckForUpdateAsync(CancellationToken.None);

        // Assert
        Assert.False(_fakeNotificationService.ShowUpdateAvailableCalled);
    }

    [Fact]
    public async Task CheckForUpdateAsync_WhenOlderVersion_DoesNotNotifyUser()
    {
        // Arrange
        const string tagName = "release_0.0.1";
        var json = CreateGitHubReleaseJson(tagName);

        using var httpClient = CreateMockHttpClient(json);
        var updateService = new UpdateService(_fakeNotificationService, httpClient);

        // Act
        await updateService.CheckForUpdateAsync(CancellationToken.None);

        // Assert
        Assert.False(_fakeNotificationService.ShowUpdateAvailableCalled);
    }

    #endregion

    #region Error Handling Tests

    [Fact]
    public async Task CheckForUpdateAsync_WhenHttpRequestFails_DoesNotNotifyUser()
    {
        // Arrange
        using var httpClient = CreateMockHttpClient(new HttpRequestException("Network error"));
        var updateService = new UpdateService(_fakeNotificationService, httpClient);

        // Act
        await updateService.CheckForUpdateAsync(CancellationToken.None);

        // Assert
        Assert.False(_fakeNotificationService.ShowUpdateAvailableCalled);
    }

    [Fact]
    public async Task CheckForUpdateAsync_WhenRequestTimesOut_DoesNotNotifyUser()
    {
        // Arrange
        using var httpClient = CreateMockHttpClient(new TaskCanceledException("Request timed out"));
        var updateService = new UpdateService(_fakeNotificationService, httpClient);

        // Act
        await updateService.CheckForUpdateAsync(CancellationToken.None);

        // Assert
        Assert.False(_fakeNotificationService.ShowUpdateAvailableCalled);
    }

    [Fact]
    public async Task CheckForUpdateAsync_WhenServerReturnsError_DoesNotNotifyUser()
    {
        // Arrange
        const string json = "{}";
        using var httpClient = CreateMockHttpClient(json, HttpStatusCode.NotFound);
        var updateService = new UpdateService(_fakeNotificationService, httpClient);

        // Act
        await updateService.CheckForUpdateAsync(CancellationToken.None);

        // Assert
        Assert.False(_fakeNotificationService.ShowUpdateAvailableCalled);
    }

    [Fact]
    public async Task CheckForUpdateAsync_WhenServerReturnsServerError_DoesNotNotifyUser()
    {
        // Arrange
        const string json = "{}";
        using var httpClient = CreateMockHttpClient(json, HttpStatusCode.InternalServerError);
        var updateService = new UpdateService(_fakeNotificationService, httpClient);

        // Act
        await updateService.CheckForUpdateAsync(CancellationToken.None);

        // Assert
        Assert.False(_fakeNotificationService.ShowUpdateAvailableCalled);
    }

    #endregion

    #region Invalid Response Tests

    [Fact]
    public async Task CheckForUpdateAsync_WhenResponseMissingTagName_DoesNotNotifyUser()
    {
        // Arrange
        var json = JsonSerializer.Serialize(new
        {
            html_url = "https://github.com/test",
            name = "Test Release"
        });

        using var httpClient = CreateMockHttpClient(json);
        var updateService = new UpdateService(_fakeNotificationService, httpClient);

        // Act
        await updateService.CheckForUpdateAsync(CancellationToken.None);

        // Assert
        Assert.False(_fakeNotificationService.ShowUpdateAvailableCalled);
    }

    [Fact]
    public async Task CheckForUpdateAsync_WhenResponseMissingHtmlUrl_DoesNotNotifyUser()
    {
        // Arrange
        var json = JsonSerializer.Serialize(new
        {
            tag_name = "release_99.0.0",
            name = "Test Release"
        });

        using var httpClient = CreateMockHttpClient(json);
        var updateService = new UpdateService(_fakeNotificationService, httpClient);

        // Act
        await updateService.CheckForUpdateAsync(CancellationToken.None);

        // Assert
        Assert.False(_fakeNotificationService.ShowUpdateAvailableCalled);
    }

    [Fact]
    public async Task CheckForUpdateAsync_WhenTagNameHasInvalidFormat_DoesNotNotifyUser()
    {
        // Arrange
        var json = CreateGitHubReleaseJson("invalid-tag-name");

        using var httpClient = CreateMockHttpClient(json);
        var updateService = new UpdateService(_fakeNotificationService, httpClient);

        // Act
        await updateService.CheckForUpdateAsync(CancellationToken.None);

        // Assert
        Assert.False(_fakeNotificationService.ShowUpdateAvailableCalled);
    }

    [Fact]
    public async Task CheckForUpdateAsync_WhenResponseIsInvalidJson_DoesNotNotifyUser()
    {
        // Arrange
        const string json = "this is not valid json";
        using var httpClient = CreateMockHttpClient(json);
        var updateService = new UpdateService(_fakeNotificationService, httpClient);

        // Act
        await updateService.CheckForUpdateAsync(CancellationToken.None);

        // Assert
        Assert.False(_fakeNotificationService.ShowUpdateAvailableCalled);
    }

    #endregion

    #region Cancellation Tests

    [Fact]
    public async Task CheckForUpdateAsync_WhenCancelled_DoesNotNotifyUser()
    {
        // Arrange
        var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        const string tagName = "release_99.0.0";
        var json = CreateGitHubReleaseJson(tagName);

        using var httpClient = CreateMockHttpClient(json);
        var updateService = new UpdateService(_fakeNotificationService, httpClient);

        // Act - UpdateService catches all exceptions internally, so no exception is thrown
        await updateService.CheckForUpdateAsync(cts.Token);

        // Assert - user should not be notified when cancellation occurs
        Assert.False(_fakeNotificationService.ShowUpdateAvailableCalled);
    }

    #endregion

    #region Constructor Tests

    [Fact]
    public void Constructor_WithNullNotificationService_ThrowsArgumentNullException()
    {
        // Arrange & Act & Assert
        Assert.Throws<ArgumentNullException>(static () =>
            new UpdateService(null!));
    }

    [Fact]
    public void Constructor_WithNullHttpClient_ThrowsArgumentNullException()
    {
        // Arrange & Act & Assert
        Assert.Throws<ArgumentNullException>(() =>
            new UpdateService(_fakeNotificationService, null!));
    }

    #endregion

    #region Mock HttpMessageHandler

    private class MockHttpMessageHandler : HttpMessageHandler
    {
        private readonly string? _responseContent;
        private readonly HttpStatusCode _statusCode;
        private readonly Exception? _exception;

        public MockHttpMessageHandler(string responseContent, HttpStatusCode statusCode = HttpStatusCode.OK)
        {
            _responseContent = responseContent;
            _statusCode = statusCode;
        }

        public MockHttpMessageHandler(Exception exception)
        {
            _exception = exception;
        }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (_exception != null)
            {
                throw _exception;
            }

            var response = new HttpResponseMessage(_statusCode)
            {
                Content = new StringContent(_responseContent ?? "{}")
            };

            return Task.FromResult(response);
        }
    }

    #endregion
}
