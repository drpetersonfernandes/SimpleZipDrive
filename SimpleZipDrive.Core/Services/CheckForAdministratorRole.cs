using System.Security.Principal;

namespace SimpleZipDrive.Core.Services;

/// <summary>
/// Provides a helper to determine whether the current process is running with administrator privileges.
/// </summary>
public static class CheckForAdministratorRole
{
    /// <summary>
    /// Determines whether the current user is in the Windows <see cref="WindowsBuiltInRole.Administrator"/> role.
    /// </summary>
    /// <returns><see langword="true"/> if the process is running as administrator; otherwise, <see langword="false"/>.</returns>
    public static bool IsAdministrator()
    {
        using var identity = WindowsIdentity.GetCurrent();
        var principal = new WindowsPrincipal(identity);
        return principal.IsInRole(WindowsBuiltInRole.Administrator);
    }
}
