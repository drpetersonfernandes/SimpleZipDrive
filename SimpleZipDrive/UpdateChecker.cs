using System.Diagnostics;
using System.Reflection;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace SimpleZipDrive;

public static class UpdateChecker
{
    private const string RepoOwner = "drpetersonfernandes";
    private const string RepoName = "SimpleZipDrive";
    private const string LatestApiUrl = $"https://api.github.com/repos/{RepoOwner}/{RepoName}/releases/latest";

    private static readonly HttpClient Http = new()
    {
        Timeout = TimeSpan.FromSeconds(10)
    };

    public static async Task CheckForUpdateAsync()
    {
        try
        {
            // GitHub rejects requests without a User-Agent header.
            if (!Http.DefaultRequestHeaders.Contains("User-Agent"))
                Http.DefaultRequestHeaders.Add("User-Agent", $"{RepoName}-UpdateChecker");

            using var resp = await Http.GetAsync(LatestApiUrl);
            if (!resp.IsSuccessStatusCode) return; // silent if offline or GitHub unhappy

            await using var jsonStream = await resp.Content.ReadAsStreamAsync();
            using var doc = await JsonDocument.ParseAsync(jsonStream);

            var tagName = doc.RootElement.GetProperty("tag_name").GetString();
            var htmlUrl = doc.RootElement.GetProperty("html_url").GetString();
            if (tagName is null || htmlUrl is null) return;

            // We expect tags like "release_1.0.2" – pick the 1.0.2 part.
            var m = Regex.Match(tagName, @"\d+\.\d+\.\d+");
            if (!m.Success) return;

            var latest = Version.Parse(m.Value);
            var current = Assembly.GetExecutingAssembly().GetName().Version
                          ?? new Version(0, 0, 0, 0);

            if (latest <= current) return; // up-to-date

            Console.WriteLine();
            Console.WriteLine($"A newer version of {RepoName} is available:");
            Console.WriteLine($"  Current : {current}");
            Console.WriteLine($"  Latest  : {latest}");
            Console.Write("Open the release page in your browser? [Y/n] ");

            if (Console.IsInputRedirected) return; // unattended run

            var key = Console.ReadKey(true).KeyChar;
            Console.WriteLine();

            if (key is 'n' or 'N')
                return;

            try
            {
                ProcessStartInfo psi = new()
                {
                    FileName = htmlUrl,
                    UseShellExecute = true
                };
                Process.Start(psi);
                Console.WriteLine("Browser opened to latest release page.");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Could not launch browser automatically: {ex.Message}");
                Console.WriteLine($"You can open the page manually: {htmlUrl}");
            }
        }
        catch (Exception ex)
        {
            // Non-fatal: log and continue silently
            ErrorLogger.LogErrorSync(ex, "UpdateChecker.CheckForUpdateAsync");
        }
    }
}
