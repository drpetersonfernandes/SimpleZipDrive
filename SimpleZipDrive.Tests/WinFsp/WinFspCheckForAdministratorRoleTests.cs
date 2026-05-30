namespace SimpleZipDrive.Tests.WinFsp;

public class WinFspCheckForAdministratorRoleTests
{
    [Fact]
    public void IsAdministrator_DoesNotThrow()
    {
        var ex = Record.Exception(static () => SimpleZipDrive_WinFsp.Services.CheckForAdministratorRole.IsAdministrator());

        Assert.Null(ex);
    }

    [Fact]
    public void IsAdministrator_ReturnsBoolean()
    {
        var result = SimpleZipDrive_WinFsp.Services.CheckForAdministratorRole.IsAdministrator();

        Assert.IsType<bool>(result);
    }
}
