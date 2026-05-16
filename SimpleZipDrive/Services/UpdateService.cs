using System.Diagnostics;
using System.Net.Security;
using System.Reflection;
using System.Security.Authentication;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Windows;

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
    private readonly ILoggingService _loggingService;
    private bool _disposed;

    private static HttpClient Http
    {
        get
        {
            if (_httpClient == null)
            {
                lock (HttpClientLock)
                {
                    _httpClient ??= new HttpClient(HttpHandler)
                    {
                        Timeout = TimeSpan.FromSeconds(15)
                    };
                }
            }

            return _httpClient;
        }
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="UpdateService"/> class.
    /// </summary>
    /// <param name="loggingService">The logging service.</param>
    public UpdateService(ILoggingService loggingService)
    {
        _loggingService = loggingService ?? throw new ArgumentNullException(nameof(loggingService));
    }

    /// <inheritdoc />
    public async Task CheckForUpdateAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            var current = Assembly.GetExecutingAssembly().GetName().Version
                          ?? new Version(0, 0, 0, 0);

            if (!Http.DefaultRequestHeaders.Contains("User-Agent"))
                Http.DefaultRequestHeaders.Add("User-Agent", $"{RepoName}-UpdateChecker");

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

            // Show message box to user about the available update
            var message = $"A newer version of {RepoName} is available.\n\n" +
                          $"Current version: {current}\n" +
                          $"Latest version: {latest}\n\n" +
                          $"Would you like to open the download page in your browser?";

            var result = MessageBox.Show(message, "Update Available", MessageBoxButton.YesNo, MessageBoxImage.Information);

            if (result == MessageBoxResult.Yes)
            {
                try
                {
                    ProcessStartInfo psi = new()
                    {
                        FileName = htmlUrl,
                        UseShellExecute = true
                    };
                    Process.Start(psi);
                    _loggingService.Log("Browser opened to latest release page.");
                }
                catch (Exception ex)
                {
                    _loggingService.Log($"Could not launch browser: {ex.Message}");
                    MessageBox.Show($"Could not open browser automatically.\n\nPlease visit:\n{htmlUrl}",
                        "Browser Error", MessageBoxButton.OK, MessageBoxImage.Warning);
                }
            }
            else
            {
                _loggingService.Log($"Update available but user declined to open download page. Visit: {htmlUrl}");
            }
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
