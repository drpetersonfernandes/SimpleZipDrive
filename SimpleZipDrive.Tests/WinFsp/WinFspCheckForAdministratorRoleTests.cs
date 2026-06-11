using SimpleZipDrive.Core.Services;

namespace SimpleZipDrive.Tests.WinFsp;

public class WinFspCheckForAdministratorRoleTests
{
    [Fact]
    public void IsAdministrator_DoesNotThrow()
    {
        var ex = Record.Exception(static () => CheckForAdministratorRole.IsAdministrator());

        Assert.Null(ex);
    }

    [Fact]
    public void IsAdministrator_ReturnsBoolean()
    {
        var result = CheckForAdministratorRole.IsAdministrator();

        Assert.IsType<bool>(result);
    }
}
