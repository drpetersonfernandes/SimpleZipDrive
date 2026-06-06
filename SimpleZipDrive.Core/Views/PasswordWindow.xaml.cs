using System.Windows;
using System.Windows.Input;

namespace SimpleZipDrive.Core.Views;

/// <summary>
/// WPF dialog that prompts the user for a password to open an encrypted archive.
/// </summary>
public partial class PasswordWindow
{
    /// <summary>Gets the password entered by the user, or <see langword="null"/> if the dialog was cancelled.</summary>
    public string? Password { get; private set; }

    /// <summary>
    /// Initializes a new instance of the <see cref="PasswordWindow"/> class.
    /// </summary>
    /// <param name="archivePath">Full path to the archive file (used to display the file name).</param>
    /// <param name="archiveType">Archive format identifier (e.g., "zip", "7z", "rar") shown in the dialog.</param>
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
        PasswordBox.Clear();
        DialogResult = true;
        Close();
    }

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        Password = null;
        PasswordBox.Clear();
        DialogResult = false;
        Close();
    }

    /// <summary>
    /// Clears the stored password and the password input field.
    /// </summary>
    public void ClearPassword()
    {
        Password = null;
        PasswordBox.Clear();
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
