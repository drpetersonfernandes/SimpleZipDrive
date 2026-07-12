namespace SimpleZipDrive.Core.Services;

/// <summary>
/// Service for capturing screenshots of the active application window.
/// </summary>
public interface IScreenshotService
{
    /// <summary>
    /// Captures the currently active application window and saves it as a PNG image
    /// inside the "Screenshot" folder within the application folder.
    /// </summary>
    /// <returns>A <see cref="ScreenshotResult"/> describing the outcome of the operation.</returns>
    ScreenshotResult CaptureActiveWindow();
}

/// <summary>
/// Represents the result of a screenshot capture operation.
/// </summary>
/// <param name="Success">Whether the screenshot was captured and saved successfully.</param>
/// <param name="FilePath">The full path to the saved screenshot, or <see langword="null"/> on failure.</param>
/// <param name="ErrorMessage">A description of the failure, or <see langword="null"/> on success.</param>
public sealed record ScreenshotResult(bool Success, string? FilePath, string? ErrorMessage);
