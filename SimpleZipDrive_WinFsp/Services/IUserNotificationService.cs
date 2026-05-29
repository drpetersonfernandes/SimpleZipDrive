namespace SimpleZipDrive_WinFsp.Services;

public interface IUserNotificationService
{
    bool ShowUpdateAvailable(Version currentVersion, Version latestVersion, string downloadUrl);
}
