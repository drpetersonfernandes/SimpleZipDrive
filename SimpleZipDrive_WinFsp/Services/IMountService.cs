namespace SimpleZipDrive_WinFsp.Services;

public class MountStatusChangedEventArgs : EventArgs
{
    public bool IsMounted { get; init; }

    public string? MountPoint { get; init; }

    public string? ArchivePath { get; init; }
}

public interface IMountService
{
    event EventHandler<MountStatusChangedEventArgs>? MountStatusChanged;

    bool IsMounted { get; }

    string? CurrentMountPoint { get; }

    string? CurrentArchivePath { get; }

    Task MountAsync(string archivePath, string? mountPoint = null);

    Task UnmountAsync();

    string GetArchiveType(string filePath);
}
