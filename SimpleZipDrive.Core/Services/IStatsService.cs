namespace SimpleZipDrive.Core.Services;

/// <summary>
/// Service for reporting application usage statistics.
/// </summary>
public interface IStatsService
{
    /// <summary>
    /// Reports application stats asynchronously.
    /// </summary>
    /// <param name="cancellationToken">Cancellation token to cancel the operation.</param>
    /// <returns>A task representing the asynchronous operation.</returns>
    Task ReportStatsAsync(CancellationToken cancellationToken = default);
}
