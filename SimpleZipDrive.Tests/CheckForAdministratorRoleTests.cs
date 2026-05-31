using SimpleZipDrive.Core.Services;

namespace SimpleZipDrive.Tests;

public class CheckForAdministratorRoleTests
{
    [Fact]
    public void IsAdministratorReturnsBooleanWithoutException()
    {
        var result = CheckForAdministratorRole.IsAdministrator();

        Assert.IsType<bool>(result);
    }

    [Fact]
    public void IsAdministratorCanBeCalledMultipleTimes()
    {
        var result1 = CheckForAdministratorRole.IsAdministrator();
        var result2 = CheckForAdministratorRole.IsAdministrator();

        Assert.Equal(result1, result2);
    }
}
