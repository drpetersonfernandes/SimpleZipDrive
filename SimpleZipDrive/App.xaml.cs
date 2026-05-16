using System.Collections.ObjectModel;
using System.Runtime.CompilerServices;
using System.Text;
using System.Threading.Channels;
using System.Windows;
using SimpleZipDrive.Models;

namespace SimpleZipDrive;

public partial class App
{
    // Legacy static access for backward compatibility
    internal static ObservableCollection<LogEntry> LogEntries => ServiceProvider.Get<ILoggingService>().LogEntries;
    internal static AppSettings Settings => ServiceProvider.Get<ISettingsService>().Settings;
    internal static string[] StartupArgs { get; private set; } = [];

    /// <summary>
    /// Global cancellation token source for graceful shutdown of background tasks.
    /// </summary>
    internal static CancellationTokenSource ShutdownCts { get; } = new();

    private static TextWriter? _originalConsoleOut;
    private static TextWriter? _originalConsoleError;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        try
        {
            StartupArgs = e.Args;

            // Register services
            RegisterServices();

            // Setup console redirection
            _originalConsoleOut = Console.Out;
            _originalConsoleError = Console.Error;

            var redirectWriter = new LogTextWriter(_originalConsoleOut);
            Console.SetOut(redirectWriter);
            Console.SetError(redirectWriter);

            // Get logging service
            var loggingService = ServiceProvider.Get<ILoggingService>();

            loggingService.Log("Archive Drive using DokanNet (Streaming Access with In-Memory Entry Cache)");
            loggingService.Log("Supports: ZIP, 7Z, and RAR archives");
            loggingService.Log("");
            loggingService.Log("Usage 1 (Explicit Mount): SimpleZipDrive.exe <PathToArchiveFile> <MountPoint>");
            loggingService.Log("Example: SimpleZipDrive.exe \"C:\\path\\to\\archive.zip\" M");
            loggingService.Log("Example: SimpleZipDrive.exe \"C:\\path\\to\\archive.7z\" N");
            loggingService.Log("Example: SimpleZipDrive.exe \"C:\\path\\to\\archive.rar\" O");
            loggingService.Log(@"MountPoint can be a drive letter (e.g., M) or a path to an existing empty folder (e.g., C:\mount\zip)");
            loggingService.Log("");
            loggingService.Log("Usage 2 (Drag-and-Drop): Drag a .zip, .7z, or .rar file onto the SimpleZipDrive.exe icon.");
            loggingService.Log(@"It will attempt to mount on M:\, then N:\, O:\, P:\, Q:\ automatically.");
            loggingService.Log("");

            var updateService = ServiceProvider.TryGet<IUpdateService>();
            if (updateService != null)
            {
                try
                {
                    updateService.CheckForUpdateAsync(ShutdownCts.Token);
                }
                catch
                {
                    // Ignore update check errors during startup
                }
            }

            loggingService.Log("");

            ErrorLoggerStatic.InitializeGlobalExceptionHandlers();

            // Run background tasks (stats)
            _ = RunBackgroundTasksAsync();
        }
        catch (Exception ex)
        {
            // Critical startup error - try to log it
            try
            {
                ErrorLoggerStatic.LogErrorSync(ex, "Critical error during application startup");
            }
            catch
            {
                // If even error logging fails, show message box as last resort
                MessageBox.Show($"Critical startup error: {ex.Message}\n\n{ex.StackTrace}",
                    "Startup Error", MessageBoxButton.OK, MessageBoxImage.Error);
            }

            throw;
        }
    }

    private static void RegisterServices()
    {
        // Register logging service first (other services depend on it)
        var loggingService = new LoggingService();
        ServiceProvider.Register<ILoggingService>(loggingService);

        // Register settings service
        var settingsService = new SettingsService();
        ServiceProvider.Register<ISettingsService>(settingsService);

        // Register mount service
        var mountService = new MountService(loggingService, settingsService);
        ServiceProvider.Register<IMountService>(mountService);

        // Register update service
        var updateService = new UpdateService(loggingService);
        ServiceProvider.Register<IUpdateService>(updateService);

        // Register stats service
        var statsService = new StatsService();
        ServiceProvider.Register<IStatsService>(statsService);
    }

    private static async Task RunBackgroundTasksAsync()
    {
        try
        {
            // Report stats (fire and forget, but respect cancellation)
            var statsService = ServiceProvider.TryGet<IStatsService>();
            if (statsService != null)
            {
                _ = Task.Run(async () =>
                {
                    try
                    {
                        await statsService.ReportStatsAsync(ShutdownCts.Token);
                    }
                    catch (OperationCanceledException)
                    {
                        // Expected during shutdown - no need to log
                    }
                    catch (Exception ex)
                    {
                        // Stats reporting failure - report silently
                        ErrorLoggerStatic.ReportSilentException(ex, "StatsService.ReportStatsAsync failed", true);
                    }
                }, ShutdownCts.Token);
            }

            // Update check already done during startup
        }
        catch (OperationCanceledException)
        {
            // Expected during shutdown - no need to log
        }
        catch (Exception ex)
        {
            // Report any other failures in background task coordination
            ErrorLoggerStatic.ReportSilentException(ex, "RunBackgroundTasksAsync failed", true);
        }

        RuntimeHelpers.RunClassConstructor(typeof(ErrorLoggerStatic).TypeHandle);
    }

    protected override void OnExit(ExitEventArgs e)
    {
        try
        {
            // Signal all background tasks to cancel
            try
            {
                ShutdownCts.Cancel();
            }
            catch (ObjectDisposedException)
            {
                // Already disposed
            }

            // Dispose all registered services that implement IDisposable
            try
            {
                ServiceProvider.DisposeAllServices();
            }
            catch
            {
                // Ensure shutdown continues even if service disposal fails
            }

            // Always dispose the shutdown token source to prevent resource leak
            ShutdownCts.Dispose();

            // Restore console
            if (_originalConsoleOut != null)
                Console.SetOut(_originalConsoleOut);
            if (_originalConsoleError != null)
                Console.SetError(_originalConsoleError);
        }
        catch
        {
            // Ensure we always complete shutdown, even if cleanup fails
        }

        base.OnExit(e);
    }

    // Legacy method for backward compatibility
    internal static void SaveSettings()
    {
        ServiceProvider.Get<ISettingsService>().SaveSettings();
    }

    // Legacy method for backward compatibility
    internal static void Log(string message)
    {
        try
        {
            ServiceProvider.TryGet<ILoggingService>()?.Log(message);
        }
        catch
        {
            // Ignore logging failures
        }
    }
}

/// <summary>
/// Text writer that redirects console output to the logging service using async-friendly Channel for high throughput.
/// </summary>
internal class LogTextWriter : TextWriter, IDisposable
{
    public override Encoding Encoding => Encoding.UTF8;

    private readonly Channel<string> _channel;
    private readonly CancellationTokenSource _cts = new();
    private readonly Task _processingTask;
    private readonly TextWriter? _fallbackWriter;

    public LogTextWriter(TextWriter? fallbackWriter = null)
    {
        _fallbackWriter = fallbackWriter;

        // Unbounded channel for maximum throughput - messages are processed asynchronously
        _channel = Channel.CreateUnbounded<string>(new UnboundedChannelOptions
        {
            SingleReader = true,
            SingleWriter = false
        });

        // Start background processing task
        _processingTask = ProcessMessagesAsync(_cts.Token);
    }

    public override void Write(char value)
    {
        _channel.Writer.TryWrite(value.ToString());
    }

    public override void Write(string? value)
    {
        if (string.IsNullOrEmpty(value)) return;

        _channel.Writer.TryWrite(value);
    }

    public override void Write(char[] buffer, int index, int count)
    {
        if (count <= 0) return;

        _channel.Writer.TryWrite(new string(buffer, index, count));
    }

    public override void WriteLine()
    {
        _channel.Writer.TryWrite(string.Empty); // Empty string signals end of line
    }

    public override void WriteLine(string? value)
    {
        _channel.Writer.TryWrite(value ?? string.Empty);
        _channel.Writer.TryWrite(string.Empty); // Signal end of line to flush buffer
    }

    public override void WriteLine(ReadOnlySpan<char> value)
    {
        _channel.Writer.TryWrite(value.ToString());
        _channel.Writer.TryWrite(string.Empty); // Signal end of line to flush buffer
    }

    private async Task ProcessMessagesAsync(CancellationToken cancellationToken)
    {
        var buffer = new StringBuilder();

        try
        {
            await foreach (var message in _channel.Reader.ReadAllAsync(cancellationToken))
            {
                // Empty string signals a line ending
                if (message.Length == 0)
                {
                    FlushBufferToLog(buffer);
                    buffer.Clear();
                }
                else
                {
                    buffer.Append(message);
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Normal shutdown - flush any remaining content
            if (buffer.Length > 0)
            {
                FlushBufferToLog(buffer);
            }
        }
    }

    private void FlushBufferToLog(StringBuilder buffer)
    {
        if (buffer.Length == 0) return;

        var message = buffer.ToString().TrimEnd('\r', '\n');
        if (string.IsNullOrWhiteSpace(message)) return;

        var loggingService = ServiceProvider.TryGet<ILoggingService>();
        if (loggingService != null)
        {
            loggingService.Log(message);
        }
        else
        {
            // Fallback: write to original console if service not available
            _fallbackWriter?.WriteLine(message);
        }
    }

    public new void Dispose()
    {
        // Signal completion and wait for processing to finish
        _channel.Writer.Complete();
        _cts.Cancel();

        try
        {
            _processingTask.Wait(TimeSpan.FromSeconds(2));
        }
        catch
        {
            // Best effort - don't block shutdown
        }

        _cts.Dispose();
        GC.SuppressFinalize(this);
    }
}