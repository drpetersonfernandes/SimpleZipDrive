using System.Diagnostics;
using System.Globalization;
using System.Text.RegularExpressions;
using SharpCompress.Archives;

namespace SimpleZipDrive.Core;

/// <summary>
/// Shared helper methods and constants used by both Dokan and WinFsp ZipFs implementations.
/// </summary>
public static class ZipFsHelpers
{
    // Cache for compiled regex patterns to avoid recompilation overhead.
    // Uses a plain Dictionary guarded by a lock because all accesses are protected
    // by the same lock to ensure atomic check-evict-add semantics.
    private static readonly Dictionary<string, Regex> RegexCache = new();
    private static readonly object RegexCacheLock = new();
    private const int MaxRegexCacheSize = 100; // Limit cache size to prevent unbounded growth

    private static readonly char[] Separator = ['/'];

    // Windows path length limits
    internal const int MaxPath = 260;
    internal const int MaxPathExtended = 32767;
    internal const string ExtendedPathPrefix = @"\\?\";

    // Static flag to ensure cleanup runs only once
    private static int _cleanupPerformed;

    private static readonly string CurrentProcessName = Process.GetCurrentProcess().ProcessName;
    private static volatile string? _currentInstanceDirName;

    /// <summary>
    /// The base temp directory path for SimpleZipDrive (%TEMP%\SimpleZipDrive).
    /// </summary>
    internal static string BaseTempPath { get; } = Path.Combine(Path.GetTempPath(), "SimpleZipDrive");

    /// <summary>
    /// Registers the current instance's temp directory name so cleanup can skip it.
    /// </summary>
    public static void RegisterCurrentTempDirectory(string dirName)
    {
        _currentInstanceDirName = dirName;
    }

    private const string TempDirNameSeparator = "_";

    public static string GenerateTempDirectoryName()
    {
        return string.Concat(Environment.ProcessId.ToString(CultureInfo.InvariantCulture), TempDirNameSeparator, Guid.NewGuid().ToString("N"));
    }

    public static bool TryParseProcessIdFromTempDirectoryName(string dirName, out int processId)
    {
        processId = 0;
        var separatorIndex = dirName.IndexOf(TempDirNameSeparator, StringComparison.Ordinal);
        if (separatorIndex <= 0)
            return false;

        return int.TryParse(dirName.AsSpan(0, separatorIndex), out processId);
    }

    /// <summary>
    /// Cleans up orphaned temp directories from previous crashed sessions.
    /// Directories associated with non-running processes are deleted.
    /// For running PIDs, validates the process name to avoid misinterpreting
    /// PID reuse by an unrelated process as a live SimpleZipDrive instance.
    /// </summary>
    public static void CleanupOrphanedTempDirectories()
    {
        try
        {
            var baseTempPath = BaseTempPath;
            if (!Directory.Exists(baseTempPath))
                return;

            var tempDirs = Directory.GetDirectories(baseTempPath);
            foreach (var dir in tempDirs)
            {
                try
                {
                    var dirName = Path.GetFileName(dir);
                    if (!TryParseProcessIdFromTempDirectoryName(dirName, out var pid))
                        continue;

                    // Skip the directory belonging to this running instance
                    if (dirName.Equals(_currentInstanceDirName, StringComparison.OrdinalIgnoreCase))
                        continue;

                    bool shouldDelete;
                    try
                    {
                        using var process = Process.GetProcessById(pid);
                        // PID exists – verify it belongs to a SimpleZipDrive variant.
                        // If the process name doesn't match, the original owner crashed
                        // and the PID was reassigned by the OS, so the directory is orphaned.
                        shouldDelete = !process.ProcessName.Equals(CurrentProcessName, StringComparison.OrdinalIgnoreCase);
                    }
                    catch (ArgumentException)
                    {
                        // Process no longer exists – safe to clean up
                        shouldDelete = true;
                    }
                    catch (InvalidOperationException)
                    {
                        // Process already exited between check and access
                        shouldDelete = true;
                    }

                    if (shouldDelete)
                    {
                        Directory.Delete(dir, true);
                    }
                }
                catch (Exception ex)
                {
                    ErrorLoggerStatic.ReportSilentException(ex, $"ZipFsHelpers.CleanupOrphanedTempDirectories: Error cleaning directory '{dir}'", true);
                }
            }
        }
        catch (Exception ex)
        {
            ErrorLoggerStatic.ReportSilentException(ex, "ZipFsHelpers.CleanupOrphanedTempDirectories: Error during cleanup", true);
        }
    }

    /// <summary>
    /// Performs cleanup if not already done for this app domain.
    /// </summary>
    public static void EnsureCleanupPerformed()
    {
        if (Interlocked.Exchange(ref _cleanupPerformed, 1) == 0)
        {
            CleanupOrphanedTempDirectories();
        }
    }

    internal static bool IsDirectory(IArchiveEntry? entry)
    {
        if (entry == null)
            return false;

        return entry.IsDirectory || (!string.IsNullOrEmpty(entry.Key) && (entry.Key.EndsWith('/') || entry.Key.EndsWith('\\')));
    }

    internal static string NormalizePath(string? path)
    {
        if (string.IsNullOrEmpty(path)) return "/";

        if (path is "\\" or "/") return "/";

        path = path.Replace('\\', '/');
        if (path is "//") return "/";

        if (!path.StartsWith('/'))
        {
            path = "/" + path;
        }

        if (path.Length > 1)
        {
            path = path.TrimEnd('/');
        }

        return path;
    }

    internal static string ResolveSpecialPaths(string normalizedPath)
    {
        if (normalizedPath == "/")
            return "/";

        var parts = normalizedPath.Split(Separator, StringSplitOptions.RemoveEmptyEntries);
        var resolved = new List<string>(parts.Length);
        foreach (var part in parts)
        {
            switch (part)
            {
                case ".":
                    continue;
                case "..":
                {
                    if (resolved.Count > 0)
                        resolved.RemoveAt(resolved.Count - 1);
                    continue;
                }
                default:
                    resolved.Add(part);
                    break;
            }
        }

        return resolved.Count == 0 ? "/" : "/" + string.Join("/", resolved);
    }

    internal static bool IsPathLengthValid(string? path)
    {
        if (string.IsNullOrEmpty(path))
            return true;

        if (path.StartsWith(ExtendedPathPrefix, StringComparison.Ordinal))
        {
            return path.Length <= MaxPathExtended;
        }

        return path.Length <= MaxPath;
    }

    internal static bool IsPasswordRequiredException(Exception ex)
    {
        var message = ex.Message.ToLowerInvariant();
        return message.Contains("password") ||
               message.Contains("encrypted") ||
               (message.Contains("rar") && message.Contains("header"));
    }

    internal static bool IsDataErrorException(Exception ex)
    {
        var exceptionTypeName = ex.GetType().Name;
        return exceptionTypeName.Contains("DataError", StringComparison.OrdinalIgnoreCase) ||
               ex.Message.Contains("Data Error", StringComparison.OrdinalIgnoreCase);
    }

    internal static bool IsMatchSimple(string input, string pattern)
    {
        if (pattern.Length > MaxPath)
            return false;

        if (pattern.Equals("*", StringComparison.Ordinal) || pattern.Equals("*.*", StringComparison.Ordinal))
            return true;

        var regexPattern = "^" + Regex.Escape(pattern).Replace("\\*", ".*").Replace("\\?", ".") + "$";

        lock (RegexCacheLock)
        {
            if (RegexCache.TryGetValue(regexPattern, out var regex))
                return regex.IsMatch(input);

            if (RegexCache.Count >= MaxRegexCacheSize)
            {
                var oldestKey = RegexCache.Keys.FirstOrDefault();
                if (oldestKey != null)
                {
                    RegexCache.Remove(oldestKey);
                }
            }

            var newRegex = new Regex(regexPattern, RegexOptions.IgnoreCase | RegexOptions.Compiled, TimeSpan.FromMilliseconds(100));
            RegexCache[regexPattern] = newRegex;
            return newRegex.IsMatch(input);
        }
    }

    internal static string? GetParentPath(string normalizedPath)
    {
        if (normalizedPath == "/")
            return null;

        var lastSlash = normalizedPath.LastIndexOf('/');
        if (lastSlash <= 0)
            return "/";

        return normalizedPath.Substring(0, lastSlash);
    }

    internal static bool IsNameMatch(string name, string pattern)
    {
        if (string.IsNullOrEmpty(pattern) || pattern == "*" || pattern == "*.*")
            return true;

        return IsMatchSimple(name, pattern);
    }
}
