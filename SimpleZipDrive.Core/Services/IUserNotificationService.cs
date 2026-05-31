namespace SimpleZipDrive.Core.Services;

public interface IUserNotificationService
{
    bool ShowUpdateAvailable(Version currentVersion, Version latestVersion, string downloadUrl);
}
