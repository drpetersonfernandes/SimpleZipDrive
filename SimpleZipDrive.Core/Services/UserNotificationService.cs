using System.Diagnostics;
using System.Windows;

namespace SimpleZipDrive.Core.Services;

/// <summary>
/// WPF-based implementation of <see cref="IUserNotificationService"/> that displays
/// MessageBox dialogs and launches the browser for update downloads.
/// </summary>
public class UserNotificationService : IUserNotificationService
{
    private const string RepoName = "SimpleZipDrive";
    private readonly ILoggingService _loggingService;

    /// <summary>
    /// Initializes a new instance of the <see cref="UserNotificationService"/> class.
    /// </summary>
    /// <param name="loggingService">The logging service used to record user actions.</param>
    public UserNotificationService(ILoggingService loggingService)
    {
        _loggingService = loggingService ?? throw new ArgumentNullException(nameof(loggingService));
    }

    /// <inheritdoc/>
    public bool ShowUpdateAvailable(Version currentVersion, Version latestVersion, string downloadUrl)
    {
        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher == null)
            return false;

        return dispatcher.Invoke(() =>
        {
            var message = $"A newer version of {RepoName} is available.\n\n" +
                          $"Current version: {currentVersion}\n" +
                          $"Latest version: {latestVersion}\n\n" +
                          $"Would you like to open the download page in your browser?";

            var result = MessageBox.Show(message, "Update Available", MessageBoxButton.YesNo, MessageBoxImage.Information);

            if (result == MessageBoxResult.Yes)
            {
                try
                {
                    ProcessStartInfo psi = new()
                    {
                        FileName = downloadUrl,
                        UseShellExecute = true
                    };
                    Process.Start(psi);
                    _loggingService.Log("Browser opened to latest release page.");
                }
                catch (Exception ex)
                {
                    ErrorLoggerStatic.ReportSilentException(ex, "UserNotificationService: Failed to launch browser for update download", true);
                    _loggingService.Log($"Could not launch browser: {ex.Message}");
                    MessageBox.Show($"Could not open browser automatically.\n\nPlease visit:\n{downloadUrl}",
                        "Browser Error", MessageBoxButton.OK, MessageBoxImage.Warning);
                }

                return true;
            }

            _loggingService.Log($"Update available but user declined to open download page. Visit: {downloadUrl}");
            return false;
        });
    }
}
