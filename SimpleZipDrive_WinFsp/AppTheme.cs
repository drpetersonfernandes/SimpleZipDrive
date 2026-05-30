namespace SimpleZipDrive_WinFsp;

public static class AppTheme
{
    private const string SectionPrefix = "--- ";
    private const string SectionSuffix = " ---";

    public const string LogEntrySeparator = "--------------------------------------------------\n";

    public const string Warning = "[!]";
    public const string Critical = "[!!!]";

    public const string Bullet = "  - ";

    // WinFsp log prefix
    public const string WinFspLogPrefix = "[WinFsp] ";

    public static string Section(string title)
    {
        return $"{SectionPrefix}{title}{SectionSuffix}";
    }
}
