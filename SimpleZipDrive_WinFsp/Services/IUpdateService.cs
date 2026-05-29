namespace SimpleZipDrive_WinFsp.Services;

public interface IUpdateService
{
    Task CheckForUpdateAsync(CancellationToken cancellationToken = default);
}
