using System.Security.Principal;

namespace SimpleZipDrive_WinFsp.Services;

public static class CheckForAdministratorRole
{
    public static bool IsAdministrator()
    {
        using var identity = WindowsIdentity.GetCurrent();
        var principal = new WindowsPrincipal(identity);
        return principal.IsInRole(WindowsBuiltInRole.Administrator);
    }
}
