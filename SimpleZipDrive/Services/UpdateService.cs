using System.Net.Security;
using System.Reflection;
using System.Security.Authentication;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace SimpleZipDrive.Services;

/// <summary>
/// Implementation of the update service.
/// </summary>
public partial class UpdateService : IUpdateService, IDisposable
{
    private const string RepoOwner = "drpetersonfernandes";
    private const string RepoName = "SimpleZipDrive";
    private const string LatestApiUrl = $"https://api.github.com/repos/{RepoOwner}/{RepoName}/releases/latest";

    private static readonly SocketsHttpHandler HttpHandler = new()
    {
        SslOptions = new SslClientAuthenticationOptions
        {
            EnabledSslProtocols = SslProtocols.None
        }
    };

    private static HttpClient? _httpClient;
    private static readonly object HttpClientLock = new();

    private readonly IUserNotificationService _userNotificationService;
    private bool _disposed;

    private static HttpClient Http
    {
        get
        {
            if (_httpClient == null)
            {
                lock (HttpClientLock)
                {
                    if (_httpClient == null)
                    {
                        var client = new HttpClient(HttpHandler)
                        {
                            Timeout = TimeSpan.FromSeconds(15)
                        };
                        client.DefaultRequestHeaders.Add("User-Agent", $"{RepoName}-UpdateChecker");
                        _httpClient = client;
                    }
                }
            }

            return _httpClient;
        }
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="UpdateService"/> class.
    /// </summary>
    /// <param name="userNotificationService">The user notification service.</param>
    public UpdateService(IUserNotificationService userNotificationService)
    {
        _userNotificationService = userNotificationService ?? throw new ArgumentNullException(nameof(userNotificationService));
    }

    /// <inheritdoc />
    public async Task CheckForUpdateAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            var current = Assembly.GetExecutingAssembly().GetName().Version
                          ?? new Version(0, 0, 0, 0);

            using var resp = await Http.GetAsync(LatestApiUrl, cancellationToken);
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
                // _loggingService.Log("You are using the most updated version.");
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

    /// <summary>
    /// Disposes the update service resources.
    /// </summary>
    public void Dispose()
    {
        if (_disposed) return;

        lock (HttpClientLock)
        {
            if (_httpClient != null)
            {
                _httpClient.Dispose();
                _httpClient = null;
            }
        }

        _disposed = true;
        GC.SuppressFinalize(this);
    }
}
