using System.Net.Security;
using System.Reflection;
using System.Security.Authentication;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace SimpleZipDrive_WinFsp.Services;

public partial class UpdateService : IUpdateService
{
    private const string RepoOwner = "drpetersonfernandes";
    private const string RepoName = "SimpleZipDrive";
    internal const string LatestApiUrl = $"https://api.github.com/repos/{RepoOwner}/{RepoName}/releases/latest";

    private static readonly SocketsHttpHandler DefaultHttpHandler = new()
    {
        SslOptions = new SslClientAuthenticationOptions
        {
            EnabledSslProtocols = SslProtocols.None
        }
    };

    private static HttpClient? _defaultHttpClient;
    private static readonly object HttpClientLock = new();

    private readonly IUserNotificationService _userNotificationService;
    private readonly HttpClient? _injectedHttpClient;

    private static HttpClient CreateDefaultHttpClient()
    {
        var client = new HttpClient(DefaultHttpHandler)
        {
            Timeout = TimeSpan.FromSeconds(15)
        };
        client.DefaultRequestHeaders.Add("User-Agent", $"{RepoName}-UpdateChecker");
        return client;
    }

    private HttpClient GetHttpClient()
    {
        if (_injectedHttpClient != null) return _injectedHttpClient;

        if (_defaultHttpClient == null)
        {
            lock (HttpClientLock)
            {
                _defaultHttpClient ??= CreateDefaultHttpClient();
            }
        }

        return _defaultHttpClient;
    }

    public UpdateService(IUserNotificationService userNotificationService)
    {
        _userNotificationService = userNotificationService ?? throw new ArgumentNullException(nameof(userNotificationService));
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="UpdateService"/> class with a custom HttpClient.
    /// This constructor is intended for testing purposes.
    /// </summary>
    /// <param name="userNotificationService">The user notification service.</param>
    /// <param name="httpClient">The HttpClient to use for HTTP requests.</param>
    public UpdateService(IUserNotificationService userNotificationService, HttpClient httpClient)
    {
        _userNotificationService = userNotificationService ?? throw new ArgumentNullException(nameof(userNotificationService));
        _injectedHttpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
    }

    public async Task CheckForUpdateAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            var current = Assembly.GetExecutingAssembly().GetName().Version
                          ?? new Version(0, 0, 0, 0);

            var client = GetHttpClient();
            using var resp = await client.GetAsync(LatestApiUrl, cancellationToken);
            if (!resp.IsSuccessStatusCode) return;

            await using var jsonStream = await resp.Content.ReadAsStreamAsync(cancellationToken);
            using var doc = await JsonDocument.ParseAsync(jsonStream, cancellationToken: cancellationToken);

            var tagName = doc.RootElement.GetProperty("tag_name").GetString();
            var htmlUrl = doc.RootElement.GetProperty("html_url").GetString();
            if (tagName is null || htmlUrl is null) return;

            var m = VersionRegex().Match(tagName);
            if (!m.Success) return;

            var latest = Version.Parse(m.Value);

            if (latest <= current)
            {
                return;
            }

            _userNotificationService.ShowUpdateAvailable(current, latest, htmlUrl);
        }
        catch (Exception ex)
        {
            await ErrorLoggerStatic.LogErrorAsync(ex, "UpdateService.CheckForUpdateAsync", cancellationToken);
        }
    }

    [GeneratedRegex(@"\d+\.\d+(?:\.\d+)?", RegexOptions.Compiled)]
    private static partial Regex VersionRegex();

}
