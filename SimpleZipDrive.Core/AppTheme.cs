namespace SimpleZipDrive.Core;

public static class AppTheme
{
    // Section separator pattern: "--- TITLE ---"
    private const string SectionPrefix = "--- ";
    private const string SectionSuffix = " ---";

    // Log entry separator (50 dashes)
    public const string LogEntrySeparator = "--------------------------------------------------\n";

    // Alert prefixes
    public const string Warning = "[!]";
    public const string Critical = "[!!!]";

    // Bullet indent
    public const string Bullet = "  - ";

    // DokanNet log prefix
    public const string DokanLogPrefix = "[DokanNet] ";

    // WinFsp log prefix
    public const string WinFspLogPrefix = "[WinFsp] ";

    public static string Section(string title)
    {
        return $"{SectionPrefix}{title}{SectionSuffix}";
    }
}
