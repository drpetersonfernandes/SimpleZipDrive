using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace SimpleZipDrive.Core.Services;

/// <summary>
/// Captures the active application window using WPF rendering and saves it as a PNG
/// inside the "Screenshot" folder within the application folder.
/// </summary>
public class ScreenshotService : IScreenshotService
{
    private const string ScreenshotFolderName = "Screenshot";
    private static readonly string ScreenshotDirectory = Path.Combine(AppContext.BaseDirectory, ScreenshotFolderName);

    private readonly ILoggingService _loggingService;

    /// <summary>
    /// Initializes a new instance of the <see cref="ScreenshotService"/> class.
    /// </summary>
    /// <param name="loggingService">The logging service used to record screenshot activity.</param>
    public ScreenshotService(ILoggingService loggingService)
    {
        _loggingService = loggingService ?? throw new ArgumentNullException(nameof(loggingService));
    }

    /// <inheritdoc />
    public ScreenshotResult CaptureActiveWindow()
    {
        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher == null)
            return new ScreenshotResult(false, null, "No active application dispatcher.");

        return dispatcher.CheckAccess()
            ? CaptureCore()
            : dispatcher.Invoke(CaptureCore);
    }

    private ScreenshotResult CaptureCore()
    {
        try
        {
            var window = Application.Current.Windows
                             .OfType<Window>()
                             .FirstOrDefault(static w => w.IsActive)
                         ?? Application.Current.MainWindow;

            if (window == null)
                return new ScreenshotResult(false, null, "No active window to capture.");

            var encoder = RenderWindow(window);
            if (encoder == null)
                return new ScreenshotResult(false, null, "The active window has no visible content to capture.");

            return SaveScreenshot(encoder);
        }
        catch (Exception ex)
        {
            ErrorLoggerStatic.ReportSilentException(ex, "ScreenshotService.CaptureCore: Failed to capture the active window");
            _loggingService.LogError($"Screenshot capture failed: {ex.Message}");
            return new ScreenshotResult(false, null, ex.Message);
        }
    }

    private static PngBitmapEncoder? RenderWindow(Window window)
    {
        var width = window.ActualWidth;
        var height = window.ActualHeight;
        if (width <= 0 || height <= 0)
            return null;

        var dpi = VisualTreeHelper.GetDpi(window);

        var renderTarget = new RenderTargetBitmap(
            (int)Math.Ceiling(width * dpi.DpiScaleX),
            (int)Math.Ceiling(height * dpi.DpiScaleY),
            dpi.PixelsPerInchX,
            dpi.PixelsPerInchY,
            PixelFormats.Pbgra32);

        renderTarget.Render(window);

        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(renderTarget));
        return encoder;
    }

    private ScreenshotResult SaveScreenshot(PngBitmapEncoder encoder)
    {
        var fileName = $"Screenshot_{DateTime.Now:yyyyMMdd_HHmmss_fff}.png";
        var filePath = Path.Combine(ScreenshotDirectory, fileName);

        try
        {
            Directory.CreateDirectory(ScreenshotDirectory);

            using var stream = new FileStream(filePath, FileMode.Create, FileAccess.Write);
            encoder.Save(stream);

            _loggingService.Log($"Screenshot saved: {filePath}");
            return new ScreenshotResult(true, filePath, null);
        }
        catch (Exception ex)
        {
            ErrorLoggerStatic.ReportSilentException(ex, "ScreenshotService.SaveScreenshot: Failed to save the screenshot");
            _loggingService.LogError($"Failed to save screenshot to '{ScreenshotDirectory}': {ex.Message}");
            return new ScreenshotResult(false, filePath, "write permission issues");
        }
    }
}
