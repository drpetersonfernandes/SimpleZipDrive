using System.Globalization;
using System.Windows;

namespace SimpleZipDrive.Views;

public partial class SettingsWindow
{
    private readonly ISettingsService _settingsService;

    public SettingsWindow()
    {
        InitializeComponent();
        _settingsService = ServiceProvider.Get<ISettingsService>();
        RamLimitTextBox.Text = _settingsService.Settings.MaxMemoryPerFileMb.ToString(CultureInfo.InvariantCulture);
    }

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            if (int.TryParse(RamLimitTextBox.Text, out var value) && value > 0)
            {
                _settingsService.Settings.MaxMemoryPerFileMb = value;
                _settingsService.SaveSettings();
                Console.WriteLine($"{AppTheme.Section("SETTINGS")}");
                Console.WriteLine($"RAM limit per file updated to {value} MB. New mounts will use this value.");
                DialogResult = true;
                Close();
            }
            else
            {
                MessageBox.Show("Please enter a valid positive number for the RAM limit.", "Invalid Input",
                    MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }
        catch (Exception ex)
        {
            const string context = "SettingsWindow.Save_Click: Error saving settings";
            ErrorLoggerStatic.LogErrorSync(ex, context);
            MessageBox.Show($"Error saving settings: {ex.Message}", "Error",
                MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
    }
}
