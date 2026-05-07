using System.Net.Http.Json;
using System.Reflection;

namespace SimpleZipDrive;

public class ReportStats
{
    private static readonly HttpClient HttpClient = new()
    {
        Timeout = TimeSpan.FromSeconds(10)
    };

    private const string StatsApiUrl = "https://www.purelogiccode.com/ApplicationStats/stats";
    private const string StatsApiKey = "hjh7yu6t56tyr540o9u8767676r5674534453235264c75b6t7ggghgg76trf564e";

    public static async Task ReportStatsAsync()
    {
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, StatsApiUrl);
            request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", StatsApiKey);
            request.Content = JsonContent.Create(new
            {
                applicationId = "SimpleZipDrive",
                version = Assembly.GetExecutingAssembly().GetName().Version?.ToString() ?? "Unknown"
            });

            var response = await HttpClient.SendAsync(request);
            response.EnsureSuccessStatusCode();
        }
        catch
        {
            // Silently ignore stats reporting failures
        }
    }
}