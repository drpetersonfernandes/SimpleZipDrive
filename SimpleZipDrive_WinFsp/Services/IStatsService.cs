namespace SimpleZipDrive_WinFsp.Services;

public interface IStatsService
{
    Task ReportStatsAsync(CancellationToken cancellationToken = default);
}
