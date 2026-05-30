using System.Net.Http.Json;
using System.Reflection;

namespace SimpleZipDrive.Services;

/// <summary>
/// Implementation of the stats service.
/// </summary>
public class StatsService : IStatsService, IDisposable
{
    internal HttpClient _httpClient = new()
    {
        Timeout = TimeSpan.FromSeconds(10)
    };

    private const string StatsApiUrl = "https://www.purelogiccode.com/ApplicationStats/stats";
    private const string StatsApiKey = "hjh7yu6t56tyr540o9u8767676r5674534453235264c75b6t7ggghgg76trf564e";
    private bool _disposed;

    /// <inheritdoc />
    public async Task ReportStatsAsync(CancellationToken cancellationToken = default)
    {
        if (_disposed) return;

        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, StatsApiUrl);
            request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", StatsApiKey);
            request.Content = JsonContent.Create(new
            {
                applicationId = "SimpleZipDrive",
                version = Assembly.GetExecutingAssembly().GetName().Version?.ToString() ?? "Unknown"
            });

            var response = await _httpClient.SendAsync(request, cancellationToken);

            // Handle HTTP 429 (Too Many Requests) gracefully - this is expected from rate limiting
            if (response.StatusCode == System.Net.HttpStatusCode.TooManyRequests)
            {
                return; // Silently ignore rate limiting responses
            }

            response.EnsureSuccessStatusCode();
        }
        catch (OperationCanceledException)
        {
            // Expected during shutdown - rethrow to allow proper cancellation handling
            throw;
        }
        catch (HttpRequestException ex)
        {
            // Network errors during stats reporting - report silently
            ErrorLoggerStatic.ReportSilentException(ex, "StatsService: HTTP request failed", true);
        }
        catch (Exception ex)
        {
            // Other stats reporting errors - report silently
            ErrorLoggerStatic.ReportSilentException(ex, "StatsService: Unexpected error reporting stats", true);
        }
    }

    /// <summary>
    /// Disposes the stats service resources.
    /// </summary>
    public void Dispose()
    {
        if (_disposed) return;

        _httpClient.Dispose();
        _disposed = true;
        GC.SuppressFinalize(this);
    }
}
