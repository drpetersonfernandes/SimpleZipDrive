using System.Globalization;
using System.Windows;

namespace SimpleZipDrive.Core.Views;

public partial class SettingsWindow
{
    private readonly ISettingsService _settingsService;

    public SettingsWindow()
    {
        InitializeComponent();
        _settingsService = ServiceProvider.Get<ISettingsService>();
        RamLimitTextBox.Text = _settingsService.Settings.MaxMemoryPerFileMb.ToString(CultureInfo.InvariantCulture);

        MountTypeComboBox.Items.Add("Drive Letter");
        MountTypeComboBox.Items.Add("Folder");
        MountTypeComboBox.SelectedIndex = _settingsService.Settings.DefaultMountType == MountType.Folder ? 1 : 0;
    }

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            if (long.TryParse(RamLimitTextBox.Text, out var value) && value > 0)
            {
                _settingsService.Settings.MaxMemoryPerFileMb = value;
                _settingsService.Settings.DefaultMountType = MountTypeComboBox.SelectedIndex == 1 ? MountType.Folder : MountType.DriveLetter;
                _settingsService.SaveSettings();

                var actualValue = _settingsService.Settings.MaxMemoryPerFileMb;
                var loggingService = ServiceProvider.TryGet<ILoggingService>();
                loggingService?.Log($"{AppTheme.Section("SETTINGS")}");

                if (actualValue < value)
                {
                    var availableMemoryMb = GC.GetGCMemoryInfo().TotalAvailableMemoryBytes / 1024 / 1024;
                    loggingService?.Log($"RAM limit per file capped to {actualValue} MB (90% of available {availableMemoryMb} MB system memory).");
                    loggingService?.Log($"Entered value {value} MB exceeded safe limits and was adjusted.");
                }
                else
                {
                    loggingService?.Log($"RAM limit per file updated to {actualValue} MB. New mounts will use this value.");
                }

                var mountType = _settingsService.Settings.DefaultMountType;
                loggingService?.Log($"Default mount type set to: {(mountType == MountType.Folder ? "Folder" : "Drive Letter")}.");

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
