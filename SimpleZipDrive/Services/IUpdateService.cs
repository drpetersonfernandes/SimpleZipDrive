namespace SimpleZipDrive.Services;

/// <summary>
/// Service for checking application updates.
/// </summary>
public interface IUpdateService
{
    /// <summary>
    /// Checks for updates asynchronously.
    /// </summary>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>A task representing the asynchronous operation.</returns>
    Task CheckForUpdateAsync(CancellationToken cancellationToken = default);
}
