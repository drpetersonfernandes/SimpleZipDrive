using System.Collections.Specialized;
using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using SimpleZipDrive.Views;

namespace SimpleZipDrive;

public partial class MainWindow : IDisposable
{
    private readonly IMountService _mountService;
    private readonly ILoggingService _loggingService;
    private int _isShuttingDown;
    private static volatile bool _shutdownCompleted;

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

        Loaded += MainWindow_LoadedAsync;
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
                    foreach (var newItem in e.NewItems?.Cast<LogEntry>() ?? [])
                    {
                        if (LogTextBox.Text.Length > 0)
                            LogTextBox.AppendText(Environment.NewLine);
                        LogTextBox.AppendText(newItem.ToString());
                    }

                    LogTextBox.ScrollToEnd();
                    break;
                }
                case NotifyCollectionChangedAction.Remove:
                    UpdateLogText();
                    break;
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

    private async void MainWindow_LoadedAsync(object sender, RoutedEventArgs e)
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
            const string context = "Error in method MainWindow_LoadedAsync";
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
                _loggingService.Log($"Drag-and-drop mode: Detected archive file '{zipFilePath}'.");
                break;
            case >= 2 when
                !string.IsNullOrWhiteSpace(args[0]) &&
                !string.IsNullOrWhiteSpace(args[1]):
                zipFilePath = args[0].Trim().Trim('"');
                mountPointArg = args[1].Trim().Trim('"');
                _loggingService.Log($"Standard mode: Archive file '{zipFilePath}', Mount point arg '{mountPointArg}'.");
                break;
            default:
                return;
        }

        if (!File.Exists(zipFilePath))
        {
            _loggingService.LogError($"Error: Archive file not found at '{zipFilePath}'.");
            return;
        }

        if (!supportedExtensions.Any(ext => Path.GetExtension(zipFilePath).Equals(ext, StringComparison.OrdinalIgnoreCase)))
        {
            _loggingService.LogError($"{AppTheme.Section("INVALID FILE TYPE")}");
            _loggingService.LogError($"Error: The file '{Path.GetFileName(zipFilePath)}' is not a supported archive.");
            _loggingService.LogError($"Detected extension: '{Path.GetExtension(zipFilePath)}' (expected: .zip, .7z, or .rar)");
            _loggingService.LogError("Simple Zip Drive can only mount ZIP, 7Z, and RAR archives.");
            return;
        }

        try
        {
#pragma warning disable IL3002
            await _mountService.MountAsync(zipFilePath, mountPointArg);
#pragma warning restore IL3002
        }
        catch (Exception ex)
        {
            const string context = "Error mounting archive from command line";
            await ErrorLoggerStatic.LogErrorAsync(ex, context);
            _loggingService.LogError($"Error: Failed to mount '{zipFilePath}'. {ex.Message}");
        }
    }

    private void SettingsRamLimit_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new SettingsWindow { Owner = this, WindowStartupLocation = WindowStartupLocation.CenterOwner };
        dialog.ShowDialog();
    }

    private void OpenConfigPath_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var configPath = AppSettings.SettingsDirectory;
            if (!Directory.Exists(configPath))
            {
                Directory.CreateDirectory(configPath);
            }

            Process.Start("explorer.exe", configPath);
        }
        catch (Exception ex)
        {
            _loggingService.LogError($"Error opening configuration path: {ex.Message}");
        }
    }

    private void CleanTempFiles_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            _loggingService.Log($"{AppTheme.Section("CLEANUP")}");
            _loggingService.Log("Cleaning orphaned temporary files...");
            ZipFsHelpers.CleanupOrphanedTempDirectories();
            _loggingService.Log("Temporary files cleaned successfully.");
        }
        catch (Exception ex)
        {
            _loggingService.LogError($"Error cleaning temp files: {ex.Message}");
            ErrorLoggerStatic.ReportSilentException(ex, "CleanTempFiles_Click: Error cleaning temp files");
        }
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

    private async void Mount_ClickAsync(object sender, RoutedEventArgs e)
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
                var settings = ServiceProvider.Get<ISettingsService>().Settings;
                if (settings.DefaultMountType == MountType.Folder)
                {
                    await MountAsFolderAsync(openFileDialog.FileName);
                }
                else
                {
#pragma warning disable IL3002
                    await _mountService.MountAsync(openFileDialog.FileName);
#pragma warning restore IL3002
                }
            }
        }
        catch (Exception ex)
        {
            const string context = "Error in method Mount_ClickAsync";
            await ErrorLoggerStatic.LogErrorAsync(ex, context);
            MessageBox.Show($"Error mounting archive: {ex.Message}", "Mount Error",
                MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private async void MountAsDrive_ClickAsync(object sender, RoutedEventArgs e)
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
#pragma warning disable IL3002
                await _mountService.MountAsync(openFileDialog.FileName);
#pragma warning restore IL3002
            }
        }
        catch (Exception ex)
        {
            const string context = "Error in method MountAsDrive_ClickAsync";
            await ErrorLoggerStatic.LogErrorAsync(ex, context);
            MessageBox.Show($"Error mounting archive: {ex.Message}", "Mount Error",
                MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private async void MountAsFolder_ClickAsync(object sender, RoutedEventArgs e)
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
                await MountAsFolderAsync(openFileDialog.FileName);
            }
        }
        catch (Exception ex)
        {
            const string context = "Error in method MountAsFolder_ClickAsync";
            await ErrorLoggerStatic.LogErrorAsync(ex, context);
            MessageBox.Show($"Error mounting archive: {ex.Message}", "Mount Error",
                MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private Task MountAsFolderAsync(string archivePath)
    {
        var folderDialog = new Microsoft.Win32.OpenFolderDialog
        {
            Title = "Select Mount Folder"
        };

        if (folderDialog.ShowDialog() == true)
        {
#pragma warning disable IL3002
            return _mountService.MountAsync(archivePath, folderDialog.FolderName);
#pragma warning restore IL3002
        }

        return Task.CompletedTask;
    }

    private async void Unmount_ClickAsync(object sender, RoutedEventArgs e)
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
                _loggingService.Log("Drive unmounted successfully.");
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
        if (Interlocked.Exchange(ref _isShuttingDown, 1) != 0)
            return;

        e.Cancel = true;
        _ = PerformShutdownAsync();
    }

    private async Task PerformShutdownAsync()
    {
        try
        {
            // Update UI directly since we're on the UI thread context
            IsEnabled = false;
            if (_mountService.IsMounted)
            {
                StatusText.Text = "Unmounting drive and shutting down...";
            }
            else
            {
                StatusText.Text = "Shutting down...";
            }

            if (_mountService.IsMounted)
            {
                var unmountTask = _mountService.UnmountAsync();
                var timeoutTask = Task.Delay(TimeSpan.FromSeconds(5));
                var completedTask = await Task.WhenAny(unmountTask, timeoutTask);
                if (completedTask == timeoutTask)
                {
                    // Unmount timed out, force exit
                    App.ShutdownCts.Cancel();
                    ForceExit();
                    return;
                }

                try
                {
                    await unmountTask;
                }
                catch (OperationCanceledException)
                {
                }
            }

            App.ShutdownCts.Cancel();

            Closing -= MainWindow_Closing;

            Application.Current.Exit += static (_, _) => { _shutdownCompleted = true; };
            Application.Current.Shutdown();

            _ = Task.Run(static async () =>
            {
                await Task.Delay(TimeSpan.FromSeconds(3));
                if (_shutdownCompleted)
                    return;

                ForceExit();
            });
        }
        catch (Exception ex)
        {
            ErrorLoggerStatic.ReportSilentException(ex, "MainWindow.PerformShutdownAsync: Shutdown failed", true);
            ForceExit();
        }
    }

    private static void ForceExit()
    {
        try
        {
            // Kill the process to ensure full termination
            Process.GetCurrentProcess().Kill();
        }
        catch
        {
            try
            {
                Environment.Exit(0);
            }
            catch
            {
                Environment.FailFast(null);
            }
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

            if (ServiceProvider.Get<ISettingsService>().Settings.AutoOpenMountedDrive
                && _mountService.CurrentMountPoint is { } mountPoint)
            {
                try
                {
                    Process.Start("explorer.exe", $"/root,\"{mountPoint}\"");
                }
                catch (Exception ex)
                {
                    _loggingService.LogError($"Failed to open drive in File Explorer: {ex.Message}");
                }
            }
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
        catch (Exception ex)
        {
            ErrorLoggerStatic.ReportSilentException(ex, "MainWindow.Dispose: Error during disposal", true);
        }

        GC.SuppressFinalize(this);
    }
}
