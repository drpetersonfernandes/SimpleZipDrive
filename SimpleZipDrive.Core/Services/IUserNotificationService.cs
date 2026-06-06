namespace SimpleZipDrive.Core.Services;

/// <summary>
/// Defines user-facing notification capabilities such as update prompts.
/// </summary>
public interface IUserNotificationService
{
    /// <summary>
    /// Displays a dialog informing the user that a newer version is available and optionally opens the download page.
    /// </summary>
    /// <param name="currentVersion">The currently installed version.</param>
    /// <param name="latestVersion">The latest available version.</param>
    /// <param name="downloadUrl">The URL to open for downloading the update.</param>
    /// <returns><see langword="true"/> if the user chose to open the download page; <see langword="false"/> if declined or unavailable.</returns>
    bool ShowUpdateAvailable(Version currentVersion, Version latestVersion, string downloadUrl);
}
