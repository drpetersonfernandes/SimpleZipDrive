using System.Diagnostics;

namespace LauncherCompanion;

/// <summary>
/// A lightweight proxy launcher that reads the target executable path from its own filename
/// and launches it. This allows executables from virtual drives to launch without error dialogs.
/// </summary>
internal static class Program
{
    // Marker format: LauncherProxy_{base64encodedpath}.exe
    private const string MarkerPrefix = "LauncherProxy_";

    /// <summary>
    /// Main entry point for the launcher proxy.
    /// Expects to find the target path encoded in the executable filename.
    /// </summary>
    public static int Main(string[] args)
    {
        try
        {
            var executablePath = Environment.ProcessPath;
            if (string.IsNullOrEmpty(executablePath))
            {
                Console.Error.WriteLine("LauncherProxy: Unable to determine executable path.");
                return 1;
            }

            var fileName = Path.GetFileNameWithoutExtension(executablePath);
            if (!fileName.StartsWith(MarkerPrefix, StringComparison.OrdinalIgnoreCase))
            {
                Console.Error.WriteLine("LauncherProxy: Invalid filename format. Expected LauncherProxy_<encodedpath>.exe");
                return 1;
            }

            // Extract and decode the target path
            var encodedPath = fileName.Substring(MarkerPrefix.Length);
            var targetPath = DecodePath(encodedPath);

            if (string.IsNullOrEmpty(targetPath) || !File.Exists(targetPath))
            {
                Console.Error.WriteLine($"LauncherProxy: Target executable not found: '{targetPath}'");
                return 1;
            }

            // Launch the real executable with the same arguments
            var startInfo = new ProcessStartInfo
            {
                FileName = targetPath,
                UseShellExecute = true,
                WorkingDirectory = Path.GetDirectoryName(targetPath)
            };

            // Pass through any command line arguments
            if (args.Length > 0)
            {
                startInfo.Arguments = string.Join(" ", args.Select(EscapeArgument));
            }

            Console.WriteLine($"LauncherProxy: Starting '{targetPath}'");
            var process = Process.Start(startInfo);

            if (process == null)
            {
                Console.Error.WriteLine("LauncherProxy: Failed to start process.");
                return 1;
            }

            Console.WriteLine($"LauncherProxy: Started successfully (PID: {process.Id})");
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"LauncherProxy: Error: {ex.Message}");
            return 1;
        }
    }

    private static string DecodePath(string encoded)
    {
        try
        {
            // Restore base64 padding
            var base64 = encoded.Replace('-', '+').Replace('_', '/');
            switch (base64.Length % 4)
            {
                case 2: base64 += "=="; break;
                case 3: base64 += "="; break;
            }

            var bytes = Convert.FromBase64String(base64);
            return System.Text.Encoding.UTF8.GetString(bytes);
        }
        catch
        {
            return string.Empty;
        }
    }

    private static string EscapeArgument(string arg)
    {
        // Simple argument escaping for command line
        if (arg.Contains(' ') || arg.Contains('"'))
        {
            return "\"" + arg.Replace("\"", "\\\"") + "\"";
        }

        return arg;
    }
}
