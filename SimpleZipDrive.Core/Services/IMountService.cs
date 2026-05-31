namespace SimpleZipDrive.Core.Services;

/// <summary>
/// Event arguments for mount status changes.
/// </summary>
public class MountStatusChangedEventArgs : EventArgs
{
    /// <summary>
    /// Gets a value indicating whether a drive is currently mounted.
    /// </summary>
    public bool IsMounted { get; init; }

    /// <summary>
    /// Gets the mount point (e.g., "M:\").
    /// </summary>
    public string? MountPoint { get; init; }

    /// <summary>
    /// Gets the archive path.
    /// </summary>
    public string? ArchivePath { get; init; }
}

/// <summary>
/// Service for managing drive mounting operations.
/// </summary>
public interface IMountService
{
    /// <summary>
    /// Occurs when the mount status changes.
    /// </summary>
    event EventHandler<MountStatusChangedEventArgs>? MountStatusChanged;

    /// <summary>
    /// Gets a value indicating whether a drive is currently mounted.
    /// </summary>
    bool IsMounted { get; }

    /// <summary>
    /// Gets the current mount point.
    /// </summary>
    string? CurrentMountPoint { get; }

    /// <summary>
    /// Gets the current archive path.
    /// </summary>
    string? CurrentArchivePath { get; }

    /// <summary>
    /// Mounts an archive file to a drive letter.
    /// </summary>
    /// <param name="archivePath">The path to the archive file.</param>
    /// <param name="mountPoint">The mount point (optional, auto-selected if null).</param>
    /// <returns>A task representing the asynchronous operation.</returns>
    Task MountAsync(string archivePath, string? mountPoint = null);

    /// <summary>
    /// Unmounts the currently mounted drive.
    /// </summary>
    /// <returns>A task representing the asynchronous operation.</returns>
    Task UnmountAsync();

    /// <summary>
    /// Gets the archive type from a file path.
    /// </summary>
    /// <param name="filePath">The file path.</param>
    /// <returns>The archive type (zip, 7z, rar).</returns>
    string GetArchiveType(string filePath);
}
