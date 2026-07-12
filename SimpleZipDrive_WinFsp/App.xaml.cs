using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading.Channels;
using System.Windows;
using SimpleZipDrive_WinFsp.Services;

namespace SimpleZipDrive_WinFsp;

public partial class App
{
    internal static string[] StartupArgs { get; private set; } = [];

    internal static CancellationTokenSource ShutdownCts { get; } = new();

    private static TextWriter? _originalConsoleOut;
    private static TextWriter? _originalConsoleError;
    private static LogTextWriter? _logTextWriter;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        try
        {
            StartupArgs = e.Args;

            DiagnosticLogger.CleanupOldLogs();
            DiagnosticLogger.Initialize();
            DiagnosticLogger.LogSection("APPLICATION STARTUP");
            DiagnosticLogger.Log($"  Version: {Assembly.GetExecutingAssembly().GetName().Version}");
            DiagnosticLogger.Log($"  Arguments: [{string.Join(", ", StartupArgs)}]");
            DiagnosticLogger.Log($"  Base directory: {AppContext.BaseDirectory}");
            DiagnosticLogger.Log($"  OS: {RuntimeInformation.OSDescription}");
            DiagnosticLogger.Log($"  Framework: {RuntimeInformation.FrameworkDescription}");
            DiagnosticLogger.Log($"  Working directory: {Environment.CurrentDirectory}");

            EnsureWinFspOnPath();

            RegisterServices();

            _originalConsoleOut = Console.Out;
            _originalConsoleError = Console.Error;

            _logTextWriter = new LogTextWriter(_originalConsoleOut);
            Console.SetOut(_logTextWriter);
            Console.SetError(_logTextWriter);

            var loggingService = ServiceProvider.Get<ILoggingService>();

            loggingService.Log("Archive Drive using WinFsp (Streaming Access with In-Memory Entry Cache)");
            loggingService.Log("Supports: ZIP, 7Z, RAR, and TAR archives");
            loggingService.Log("");
            if (DiagnosticLogger.LogFilePath != null)
            {
                loggingService.Log($"Debug log: {DiagnosticLogger.LogFilePath}");
                loggingService.Log("");
            }

            loggingService.Log("Usage 1 (Explicit Mount): SimpleZipDrive_WinFsp.exe <PathToArchiveFile> <MountPoint>");
            loggingService.Log("Example: SimpleZipDrive_WinFsp.exe \"C:\\path\\to\\archive.zip\" M");
            loggingService.Log("Example: SimpleZipDrive_WinFsp.exe \"C:\\path\\to\\archive.7z\" N");
            loggingService.Log("Example: SimpleZipDrive_WinFsp.exe \"C:\\path\\to\\archive.rar\" O");
            loggingService.Log(@"MountPoint can be a drive letter (e.g., M) or a path to an existing empty folder (e.g., C:\mount\zip)");
            loggingService.Log("");
            loggingService.Log("Usage 2 (Drag-and-Drop): Drag a .zip, .7z, .rar, .tar, .tar.gz, .tar.bz2, .tar.xz, .tgz, .tbz2, or .txz file onto the SimpleZipDrive_WinFsp.exe icon.");
            loggingService.Log(@"It will attempt to mount on M:\, then N:\, O:\, P:\, Q:\ automatically.");
            loggingService.Log("");

            var updateService = ServiceProvider.TryGet<IUpdateService>();
            if (updateService != null)
            {
                _ = Task.Run(async () =>
                {
                    try
                    {
                        await updateService.CheckForUpdateAsync(ShutdownCts.Token);
                    }
                    catch (OperationCanceledException)
                    {
                        // Expected during shutdown
                    }
                    catch (Exception ex)
                    {
                        ErrorLoggerStatic.ReportSilentException(ex, "App.OnStartup: Update check failed during startup", true);
                    }
                });
            }

            loggingService.Log("");

            ErrorLoggerStatic.InitializeGlobalExceptionHandlers();

            _ = RunBackgroundTasksAsync();
        }
        catch (Exception ex)
        {
            try
            {
                ErrorLoggerStatic.LogErrorSync(ex, "Critical error during application startup");
            }
            catch
            {
                MessageBox.Show($"Critical startup error: {ex.Message}\n\n{ex.StackTrace}",
                    "Startup Error", MessageBoxButton.OK, MessageBoxImage.Error);
            }

            throw;
        }
    }

    private static void EnsureWinFspOnPath()
    {
        try
        {
            var currentPath = Environment.GetEnvironmentVariable("PATH") ?? string.Empty;
            if (currentPath.Contains("WinFsp", StringComparison.OrdinalIgnoreCase))
                return;

            string? binDir = null;

            using var key = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(@"SOFTWARE\WOW6432Node\WinFsp")
                            ?? Microsoft.Win32.Registry.LocalMachine.OpenSubKey(@"SOFTWARE\WinFsp");
            var sxsDir = key?.GetValue("SxsDir") as string;
            if (!string.IsNullOrEmpty(sxsDir))
            {
                var sxsBin = Path.Combine(sxsDir, "bin");
                if (Directory.Exists(sxsBin))
                {
                    binDir = sxsBin;
                }
            }

            if (binDir == null)
            {
                var installDir = key?.GetValue("InstallDir") as string;
                if (!string.IsNullOrEmpty(installDir))
                {
                    var installBin = Path.Combine(installDir, "bin");
                    if (Directory.Exists(installBin))
                    {
                        binDir = installBin;
                    }
                }
            }

            if (binDir == null)
                return;

            var dllName = Environment.Is64BitProcess ? "winfsp-x64.dll" : "winfsp-x86.dll";
            var dllPath = Path.Combine(binDir, dllName);
            if (!File.Exists(dllPath))
                return;

            Environment.SetEnvironmentVariable("PATH", binDir + ";" + currentPath, EnvironmentVariableTarget.Process);
            DiagnosticLogger.Log($"  WinFsp PATH set to: {binDir}");
        }
        catch
        {
            // Best-effort; failure is non-fatal
        }
    }

    private static void RegisterServices()
    {
        var loggingService = new LoggingService();
        ServiceProvider.Register<ILoggingService>(loggingService);

        var settingsService = new SettingsService();
        ServiceProvider.Register<ISettingsService>(settingsService);

        var mountService = new MountService(loggingService, settingsService);
        ServiceProvider.Register<IMountService>(mountService);

        var userNotificationService = new UserNotificationService(loggingService);
        ServiceProvider.Register<IUserNotificationService>(userNotificationService);

        var screenshotService = new ScreenshotService(loggingService);
        ServiceProvider.Register<IScreenshotService>(screenshotService);

        var updateService = new UpdateService(userNotificationService);
        ServiceProvider.Register<IUpdateService>(updateService);

        var statsService = new StatsService();
        ServiceProvider.Register<IStatsService>(statsService);
    }

    private static Task RunBackgroundTasksAsync()
    {
        try
        {
            try
            {
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
                        }
                        catch (Exception ex)
                        {
                            ErrorLoggerStatic.ReportSilentException(ex, "StatsService.ReportStatsAsync failed", true);
                        }
                    }, ShutdownCts.Token);
                }
            }
            catch (OperationCanceledException)
            {
            }
            catch (Exception ex)
            {
                ErrorLoggerStatic.ReportSilentException(ex, "RunBackgroundTasksAsync failed", true);
            }

            return Task.CompletedTask;
        }
        catch (Exception exception)
        {
            return Task.FromException(exception);
        }
    }

    protected override void OnExit(ExitEventArgs e)
    {
        DiagnosticLogger.LogSection("APPLICATION SHUTDOWN");
        try
        {
            try
            {
                ShutdownCts.Cancel();
            }
            catch (ObjectDisposedException)
            {
            }

            try
            {
                _logTextWriter?.Dispose();
            }
            catch (Exception ex)
            {
                ErrorLoggerStatic.ReportSilentException(ex, "App.OnExit: Failed to dispose LogTextWriter", true);
            }

            try
            {
                if (Current.MainWindow is IDisposable disposableMainWindow)
                {
                    disposableMainWindow.Dispose();
                }
            }
            catch (Exception ex)
            {
                ErrorLoggerStatic.ReportSilentException(ex, "App.OnExit: Failed to dispose MainWindow", true);
            }

            try
            {
                ServiceProvider.DisposeAllServices();
            }
            catch (Exception ex)
            {
                ErrorLoggerStatic.ReportSilentException(ex, "App.OnExit: Failed to dispose services", true);
            }

            if (_originalConsoleOut != null)
                Console.SetOut(_originalConsoleOut);
            if (_originalConsoleError != null)
                Console.SetError(_originalConsoleError);
        }
        catch (Exception ex)
        {
            ErrorLoggerStatic.ReportSilentException(ex, "App.OnExit: Error during exit cleanup", true);
        }

        // Dispose the singleton ErrorLogger (HttpClient connection pool)
        // Must be done AFTER the outer catch, which may still need to report errors
        try
        {
            ErrorLoggerStatic.Instance.Dispose();
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Failed to dispose ErrorLogger: {ex.Message}");
        }

        base.OnExit(e);
    }
}

internal class LogTextWriter : TextWriter
{
    public override Encoding Encoding => Encoding.UTF8;

    private readonly Channel<string> _channel;
    private readonly CancellationTokenSource _cts = new();
    private readonly Task _processingTask;
    private readonly TextWriter? _fallbackWriter;

    public LogTextWriter(TextWriter? fallbackWriter = null)
    {
        _fallbackWriter = fallbackWriter;

        _channel = Channel.CreateUnbounded<string>(new UnboundedChannelOptions
        {
            SingleReader = true,
            SingleWriter = false
        });

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
        _channel.Writer.TryWrite(CoreNewLine.ToString() ?? Environment.NewLine);
        _channel.Writer.TryWrite(string.Empty);
    }

    public override void WriteLine(string? value)
    {
        _channel.Writer.TryWrite(string.Concat(value, CoreNewLine));
        _channel.Writer.TryWrite(string.Empty);
    }

    public override void WriteLine(ReadOnlySpan<char> value)
    {
        _channel.Writer.TryWrite(string.Concat(value, CoreNewLine));
        _channel.Writer.TryWrite(string.Empty);
    }

    private async Task ProcessMessagesAsync(CancellationToken cancellationToken)
    {
        var buffer = new StringBuilder();

        try
        {
            await foreach (var message in _channel.Reader.ReadAllAsync(cancellationToken))
            {
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
        }
        finally
        {
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

        DiagnosticLogger.Log($"[Console] {message}");

        var loggingService = ServiceProvider.TryGet<ILoggingService>();
        if (loggingService != null)
        {
            loggingService.Log(message);
        }
        else
        {
            _fallbackWriter?.WriteLine(message);
        }
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _channel.Writer.Complete();

            try
            {
                if (!_processingTask.Wait(TimeSpan.FromSeconds(2)))
                {
                    _cts.Cancel();
                    _processingTask.Wait(TimeSpan.FromMilliseconds(500));
                }
            }
            catch (Exception ex)
            {
                ErrorLoggerStatic.ReportSilentException(ex, "LogTextWriter.Dispose: Processing task wait failed", true);
            }

            _cts.Dispose();
        }

        base.Dispose(disposing);
    }
}