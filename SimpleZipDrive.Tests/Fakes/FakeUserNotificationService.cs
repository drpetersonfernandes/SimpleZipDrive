namespace SimpleZipDrive.Tests.Fakes;

public class FakeUserNotificationService : Core.Services.IUserNotificationService
{
    public bool ShowUpdateAvailableCalled { get; private set; }
    public Version? CalledWithCurrentVersion { get; private set; }
    public Version? CalledWithLatestVersion { get; private set; }
    public string? CalledWithDownloadUrl { get; private set; }
    public bool ReturnValue { get; set; }

    public bool ShowUpdateAvailable(Version currentVersion, Version latestVersion, string downloadUrl)
    {
        ShowUpdateAvailableCalled = true;
        CalledWithCurrentVersion = currentVersion;
        CalledWithLatestVersion = latestVersion;
        CalledWithDownloadUrl = downloadUrl;
        return ReturnValue;
    }

    public void Reset()
    {
        ShowUpdateAvailableCalled = false;
        CalledWithCurrentVersion = null;
        CalledWithLatestVersion = null;
        CalledWithDownloadUrl = null;
        ReturnValue = false;
    }
}
