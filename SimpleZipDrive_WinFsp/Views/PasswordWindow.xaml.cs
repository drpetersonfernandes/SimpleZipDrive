using System.Windows;
using System.Windows.Input;

namespace SimpleZipDrive_WinFsp.Views;

public partial class PasswordWindow
{
    public string? Password { get; private set; }

    public PasswordWindow(string archivePath, string archiveType)
    {
        InitializeComponent();
        ArchiveTypeRun.Text = archiveType.ToUpperInvariant();
        ArchiveNameText.Text = Path.GetFileName(archivePath);
        Loaded += (_, _) => PasswordBox.Focus();
    }

    private void Ok_Click(object sender, RoutedEventArgs e)
    {
        Password = PasswordBox.Password;
        DialogResult = true;
        Close();
    }

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        Password = null;
        DialogResult = false;
        Close();
    }

    private void PasswordBox_KeyDown(object sender, KeyEventArgs e)
    {
        switch (e.Key)
        {
            case Key.Enter:
                Ok_Click(sender, e);
                break;
            case Key.Escape:
                Cancel_Click(sender, e);
                break;
        }
    }
}
