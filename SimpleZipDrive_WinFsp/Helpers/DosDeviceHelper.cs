using System.Diagnostics;
using System.Runtime.InteropServices;

namespace SimpleZipDrive_WinFsp.Helpers;

public static class DosDeviceHelper
{
    [DllImport("kernel32.dll", CharSet = CharSet.Auto, SetLastError = true)]
    private static extern bool DefineDosDevice(int dwFlags, string lpDeviceName, string? lpTargetPath);

    [DllImport("kernel32.dll", CharSet = CharSet.Auto, SetLastError = true)]
    private static extern int QueryDosDevice(string lpDeviceName, [Out] char[] lpTargetPath, int ucchMax);

    private const int DddRawTargetPath = 0x00000001;
    private const int DddRemoveDefinition = 0x00000002;
    private const int DddExactMatchOnRemove = 0x00000004;
    private const int DddNoBroadcastSystem = 0x00000008;

    /// <summary>
    /// Creates a raw device mapping without removing any existing mapping first.
    /// Safe to call even when another mapping exists (e.g., WinFsp's temporary link).
    /// </summary>
    public static bool TryCreateRawDeviceMapping(string driveLetter, string rawDevicePath)
    {
        if (string.IsNullOrWhiteSpace(driveLetter))
            throw new ArgumentException("Drive letter cannot be empty", nameof(driveLetter));
        if (string.IsNullOrWhiteSpace(rawDevicePath))
            throw new ArgumentException("Device path cannot be empty", nameof(rawDevicePath));

        if (!driveLetter.EndsWith(':'))
        {
            driveLetter += ":";
        }

        var result = DefineDosDevice(DddRawTargetPath | DddNoBroadcastSystem, driveLetter, rawDevicePath);
        if (!result)
        {
            var error = Marshal.GetLastWin32Error();
            Debug.WriteLine($"DosDeviceHelper: TryCreateRawDeviceMapping failed with error {error} for {driveLetter} -> {rawDevicePath}");
        }
        else
        {
            Debug.WriteLine($"DosDeviceHelper: TryCreateRawDeviceMapping succeeded {driveLetter} -> {rawDevicePath}");
        }

        return result;
    }

    /// <summary>
    /// Removes any existing mapping and creates a raw device mapping.
    /// Best for admin-mode global mappings where we replace the temporary WinFsp link.
    /// </summary>
    public static bool MapDriveToRawDevice(string driveLetter, string rawDevicePath)
    {
        if (string.IsNullOrWhiteSpace(driveLetter))
            throw new ArgumentException("Drive letter cannot be empty", nameof(driveLetter));
        if (string.IsNullOrWhiteSpace(rawDevicePath))
            throw new ArgumentException("Device path cannot be empty", nameof(rawDevicePath));

        if (!driveLetter.EndsWith(':'))
        {
            driveLetter += ":";
        }

        RemoveDriveMapping(driveLetter);
        Thread.Sleep(100);

        var result = DefineDosDevice(DddRawTargetPath | DddNoBroadcastSystem, driveLetter, rawDevicePath);
        if (!result)
        {
            var error = Marshal.GetLastWin32Error();
            Debug.WriteLine($"DosDeviceHelper: MapDriveToRawDevice failed with error {error} for {driveLetter} -> {rawDevicePath}");
        }
        else
        {
            Debug.WriteLine($"DosDeviceHelper: MapDriveToRawDevice succeeded {driveLetter} -> {rawDevicePath}");
        }

        return result;
    }

    /// <summary>
    /// Queries the target path of a drive letter mapping.
    /// </summary>
    public static string? QueryDriveMapping(string driveLetter)
    {
        if (!driveLetter.EndsWith(':'))
        {
            driveLetter += ":";
        }

        var buffer = new char[1024];
        var length = QueryDosDevice(driveLetter, buffer, buffer.Length);
        if (length > 0)
        {
            var result = new string(buffer, 0, length).TrimEnd('\0');
            var nullIndex = result.IndexOf('\0');
            if (nullIndex > 0)
            {
                result = result.Substring(0, nullIndex);
            }

            return result;
        }

        return null;
    }

    /// <summary>
    /// Removes a drive letter mapping.
    /// </summary>
    public static bool RemoveDriveMapping(string driveLetter)
    {
        if (!driveLetter.EndsWith(':'))
        {
            driveLetter += ":";
        }

        var result = DefineDosDevice(
            DddRemoveDefinition | DddExactMatchOnRemove | DddNoBroadcastSystem,
            driveLetter,
            null);

        Debug.WriteLine($"DosDeviceHelper: RemoveDriveMapping({driveLetter}) = {result}");
        return result;
    }
}
