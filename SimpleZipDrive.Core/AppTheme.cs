namespace SimpleZipDrive.Core;

/// <summary>
/// Provides formatting constants and helper methods for consistent console and log output styling.
/// </summary>
public static class AppTheme
{
    // Section separator pattern: "--- TITLE ---"
    private const string SectionPrefix = "--- ";
    private const string SectionSuffix = " ---";

    /// <summary>Separator line inserted between log entries.</summary>
    public const string LogEntrySeparator = "--------------------------------------------------\n";

    /// <summary>Prefix for warning-level messages.</summary>
    public const string Warning = "[!]";

    /// <summary>Prefix for critical-level messages.</summary>
    public const string Critical = "[!!!]";

    /// <summary>Indentation prefix for bulleted list items.</summary>
    public const string Bullet = "  - ";

    /// <summary>Prefix applied to DokanNet diagnostic messages.</summary>
    public const string DokanLogPrefix = "[DokanNet] ";


    /// <summary>
    /// Formats a section title with <c>--- </c> delimiters.
    /// </summary>
    /// <param name="title">The section title text.</param>
    /// <returns>A formatted string such as <c>"--- My Section ---"</c>.</returns>
    public static string Section(string title)
    {
        return $"{SectionPrefix}{title}{SectionSuffix}";
    }
}
