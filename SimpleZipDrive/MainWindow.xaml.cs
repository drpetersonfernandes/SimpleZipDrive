using System.Collections.Specialized;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using SimpleZipDrive.Views;

namespace SimpleZipDrive;

public partial class MainWindow : IDisposable
{
    private readonly IMountService _mountService;
    private readonly ILoggingService _loggingService;

    public MainWindow()
    {
        InitializeComponent();

        // Get services from the service provider
        _mountService = ServiceProvider.Get<IMountService>();
        _loggingService = ServiceProvider.Get<ILoggingService>();

        _mountService.MountStatusChanged += OnMountStatusChanged;

        // Subscribe to log entries collection changes
        ((INotifyCollectionChanged)_loggingService.LogEntries).CollectionChanged += OnLogEntriesChanged;

        // Initialize log text
        UpdateLogText();

        // Wire up context menu event handlers
        WireUpContextMenuHandlers();

        Loaded += MainWindow_Loaded;
    }

    private void OnMountStatusChanged(object? sender, MountStatusChangedEventArgs e)
    {
        Dispatcher.BeginInvoke(new Action(UpdateMountStatus), DispatcherPriority.Background);
    }

    private void WireUpContextMenuHandlers()
    {
        if (LogTextBox.ContextMenu is not { } contextMenu) return;

        foreach (var item in contextMenu.Items.OfType<MenuItem>())
        {
            switch (item.Header)
            {
                case "Copy":
                    item.Click += CopySelection_Click;
                    break;
                case "Copy All":
                    item.Click += CopyLog_Click;
                    break;
                case "Clear Log":
                    item.Click += ClearLog_Click;
                    break;
            }
        }
    }

    private void OnLogEntriesChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        Dispatcher.BeginInvoke(new Action(() =>
        {
            switch (e.Action)
            {
                case NotifyCollectionChangedAction.Add:
                {
                    // Append new entries to the text box
                    foreach (var newItem in e.NewItems?.Cast<Models.LogEntry>() ?? [])
                    {
                        if (LogTextBox.Text.Length > 0)
                            LogTextBox.AppendText(Environment.NewLine);
                        LogTextBox.AppendText(newItem.ToString());
                    }

                    // Auto-scroll to the end
                    LogTextBox.ScrollToEnd();

                    // Limit log size
                    while (_loggingService.LogEntries.Count > 5000)
                    {
                        _loggingService.LogEntries.RemoveAt(0);
                    }

                    break;
                }
                case NotifyCollectionChangedAction.Reset:
                    LogTextBox.Clear();
                    break;
            }
        }), DispatcherPriority.Background);
    }

    private void UpdateLogText()
    {
        var text = string.Join(Environment.NewLine, _loggingService.LogEntries.Select(static e => e.ToString()));
        LogTextBox.Text = text;
        LogTextBox.ScrollToEnd();
    }

    private async void MainWindow_Loaded(object sender, RoutedEventArgs e)
    {
        try
        {
            var args = App.StartupArgs;
            if (args.Length > 0)
            {
                await ProcessCommandLineArgsAsync(args);
            }
        }
        catch (Exception ex)
        {
            const string context = "Error in method MainWindow_Loaded";
            await ErrorLoggerStatic.LogErrorAsync(ex, context);
        }
    }

    private async Task ProcessCommandLineArgsAsync(string[] args)
    {
        string[] supportedExtensions = [".zip", ".7z", ".rar"];

        string? zipFilePath;
        string? mountPointArg = null;

        switch (args.Length)
        {
            case 1 when
                !string.IsNullOrWhiteSpace(args[0]) &&
                File.Exists(args[0]) &&
                supportedExtensions.Any(ext => Path.GetExtension(args[0]).Equals(ext, StringComparison.OrdinalIgnoreCase)):
                zipFilePath = args[0].Trim().Trim('"');
                Console.WriteLine($"Drag-and-drop mode: Detected archive file '{zipFilePath}'.");
                break;
            case >= 2:
                zipFilePath = args[0].Trim().Trim('"');
                mountPointArg = args[1].Trim().Trim('"');
                Console.WriteLine($"Standard mode: Archive file '{zipFilePath}', Mount point arg '{mountPointArg}'.");
                break;
            default:
                return;
        }

        if (!File.Exists(zipFilePath))
        {
            Console.WriteLine($"Error: Archive file not found at '{zipFilePath}'.");
            return;
        }

        if (!supportedExtensions.Any(ext => Path.GetExtension(zipFilePath).Equals(ext, StringComparison.OrdinalIgnoreCase)))
        {
            Console.WriteLine($"\n{AppTheme.Section("INVALID FILE TYPE")}");
            Console.WriteLine($"Error: The file '{Path.GetFileName(zipFilePath)}' is not a supported archive.");
            Console.WriteLine($"Detected extension: '{Path.GetExtension(zipFilePath)}' (expected: .zip, .7z, or .rar)");
            Console.WriteLine("Simple Zip Drive can only mount ZIP, 7Z, and RAR archives.");
            return;
        }

        try
        {
            await _mountService.MountAsync(zipFilePath, mountPointArg);
        }
        catch (Exception ex)
        {
            const string context = "Error mounting archive from command line";
            await ErrorLoggerStatic.LogErrorAsync(ex, context);
            Console.WriteLine($"Error: Failed to mount '{zipFilePath}'. {ex.Message}");
        }
    }

    private void SettingsRamLimit_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new SettingsWindow { Owner = this, WindowStartupLocation = WindowStartupLocation.CenterOwner };
        dialog.ShowDialog();
    }

    private void About_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new AboutWindow { Owner = this, WindowStartupLocation = WindowStartupLocation.CenterOwner };
        dialog.ShowDialog();
    }

    private void Exit_Click(object sender, RoutedEventArgs e)
    {
        // Trigger the closing event which will handle proper cleanup
        Close();
    }

    private async void Mount_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            if (_mountService.IsMounted)
            {
                MessageBox.Show("A drive is already mounted. Please unmount it first.", "Drive Already Mounted",
                    MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            var openFileDialog = new Microsoft.Win32.OpenFileDialog
            {
                Title = "Select Archive File",
                Filter = "Archive files (*.zip;*.7z;*.rar)|*.zip;*.7z;*.rar|ZIP files (*.zip)|*.zip|7Z files (*.7z)|*.7z|RAR files (*.rar)|*.rar|All files (*.*)|*.*",
                CheckFileExists = true,
                CheckPathExists = true
            };

            if (openFileDialog.ShowDialog() == true)
            {
                await _mountService.MountAsync(openFileDialog.FileName);
            }
        }
        catch (Exception ex)
        {
            const string context = "Error in method Mount_Click";
            await ErrorLoggerStatic.LogErrorAsync(ex, context);
            MessageBox.Show($"Error mounting archive: {ex.Message}", "Mount Error",
                MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private async void Unmount_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            if (!_mountService.IsMounted)
            {
                return;
            }

            try
            {
                StatusText.Text = "Unmounting drive...";
                await _mountService.UnmountAsync();
                StatusText.Text = "Drive unmounted";
                Console.WriteLine("Drive unmounted successfully.");
            }
            catch (OperationCanceledException)
            {
                // Expected during unmount - no need to report
                StatusText.Text = "Drive unmount cancelled";
            }
            catch (Exception ex)
            {
                const string context = "Error unmounting drive";
                await ErrorLoggerStatic.LogErrorAsync(ex, context);
                MessageBox.Show($"Error unmounting drive: {ex.Message}", "Unmount Error",
                    MessageBoxButton.OK, MessageBoxImage.Error);
                StatusText.Text = "Error unmounting drive";
            }
        }
        catch (Exception ex)
        {
            const string context = "Error unmounting drive";
            await ErrorLoggerStatic.LogErrorAsync(ex, context);
        }
    }

    private void CopySelection_Click(object sender, RoutedEventArgs e)
    {
        if (!string.IsNullOrEmpty(LogTextBox.SelectedText))
        {
            Clipboard.SetText(LogTextBox.SelectedText);
            StatusText.Text = "Selection copied to clipboard.";
        }
    }

    private void CopyLog_Click(object sender, RoutedEventArgs e)
    {
        if (_loggingService.LogEntries.Count == 0) return;

        var text = string.Join(Environment.NewLine,
            _loggingService.LogEntries.Select(static item => item.ToString()));
        Clipboard.SetText(text);
        StatusText.Text = "Log copied to clipboard.";
    }

    private void ClearLog_Click(object sender, RoutedEventArgs e)
    {
        _loggingService.LogEntries.Clear();
        LogTextBox.Clear();
        StatusText.Text = "Log cleared.";
    }

    private void MainWindow_Closing(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        // Prevent the window from closing immediately while we cleanup
        e.Cancel = true;
        _ = PerformShutdownAsync();
    }

    private async Task PerformShutdownAsync()
    {
        try
        {
            // Disable UI interaction during shutdown
            Dispatcher.Invoke(() =>
            {
                IsEnabled = false;
                if (_mountService.IsMounted)
                {
                    StatusText.Text = "Unmounting drive and shutting down...";
                }
                else
                {
                    StatusText.Text = "Shutting down...";
                }
            });

            // If mounted, unmount via the service
            if (_mountService.IsMounted)
            {
                try
                {
                    await _mountService.UnmountAsync();
                }
                catch
                {
                    // Ignore unmount errors during shutdown
                }
            }

            // Signal the global shutdown
            App.ShutdownCts.Cancel();

            // Now properly shutdown the application
            Dispatcher.Invoke(() =>
            {
                try
                {
                    // Remove the closing handler to prevent recursion
                    Closing -= MainWindow_Closing;
                }
                catch
                {
                    // Ignore removal errors
                }

                // Shutdown the application
                Application.Current.Shutdown();
            });
        }
        catch (Exception ex)
        {
            await ErrorLoggerStatic.LogErrorAsync(ex, "Error during shutdown");
            // Force shutdown on error
            Application.Current.Shutdown();
        }
    }

    private void UpdateMountStatus()
    {
        if (_mountService.IsMounted)
        {
            MountStatusText.Text = $"Mounted: {_mountService.CurrentMountPoint} | Archive: {_mountService.CurrentArchivePath}";
            StatusText.Text = "Drive mounted - Click Unmount to unmount";
            UnmountButton.IsEnabled = true;
            MountButton.IsEnabled = false;
        }
        else
        {
            MountStatusText.Text = "";
            UnmountButton.IsEnabled = false;
            MountButton.IsEnabled = true;
        }
    }

    public void Dispose()
    {
        try
        {
            // Unsubscribe from log entries collection changes
            if (_loggingService.LogEntries is INotifyCollectionChanged notifyCollection)
            {
                notifyCollection.CollectionChanged -= OnLogEntriesChanged;
            }

            // Unsubscribe from mount service events
            _mountService.MountStatusChanged -= OnMountStatusChanged;

            // Dispose the mount service (which handles unmounting if needed)
            (_mountService as IDisposable)?.Dispose();
        }
        catch
        {
            // Ignore disposal errors
        }

        GC.SuppressFinalize(this);
    }
}
