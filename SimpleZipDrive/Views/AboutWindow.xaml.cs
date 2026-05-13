using System.Diagnostics;
using System.Reflection;
using System.Windows;

namespace SimpleZipDrive.Views;

public partial class AboutWindow
{
    public AboutWindow()
    {
        InitializeComponent();

        var version = Assembly.GetExecutingAssembly().GetName().Version?.ToString() ?? "Unknown";
        VersionTextBlock.Text = $"Version {version}";
    }

    private void Close_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = true;
        Close();
    }

    private void DokanLink_Click(object sender, RoutedEventArgs e)
    {
        OpenUrl("https://github.com/dokan-dev/dokan-dotnet");
    }

    private void SharpCompressLink_Click(object sender, RoutedEventArgs e)
    {
        OpenUrl("https://github.com/adamhathcock/sharpcompress");
    }

    private void GitHubLink_Click(object sender, RoutedEventArgs e)
    {
        OpenUrl("https://github.com/drpetersonfernandes/SimpleZipDrive");
    }

    private static void OpenUrl(string url)
    {
        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = url,
                UseShellExecute = true
            });
        }
        catch (Exception ex)
        {
            var context = $"AboutWindow.OpenUrl: Could not open URL '{url}'";
            ErrorLoggerStatic.ReportSilentException(ex, context, true);
            Console.WriteLine($"Could not open URL: {ex.Message}");
        }
    }
}
