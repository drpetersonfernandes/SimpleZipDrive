using System.Globalization;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;

namespace SimpleZipDrive;

/// <summary>
/// Centralized error logging and bug reporting service implementation.
/// Sends all non-user errors to the remote bug report API with full environment and exception details.
/// </summary>
public class ErrorLogger : IDisposable
{
    private const string ApiKey = "hjh7yu6t56tyr540o9u8767676r5674534453235264c75b6t7ggghgg76trf564e";
    private const string BugReportApiUrl = "https://www.purelogiccode.com/bugreport/api/send-bug-report";

    private const string ApplicationName = "SimpleZipDrive";
    private readonly HttpClient _httpClient;

    private readonly string _baseDirectory;

    /// <summary>
    /// Gets or sets the error log file path. Used for testing.
    /// </summary>
    internal string ErrorLogFilePath { get; set; }

    /// <summary>
    /// Creates a new instance of the ErrorLogger.
    /// </summary>
    /// <param name="logFilePath">Optional custom log file path. If not provided, uses default location.</param>
    public ErrorLogger(string? logFilePath = null)
    {
        _httpClient = new HttpClient
        {
            Timeout = TimeSpan.FromSeconds(30)
        };
        _baseDirectory = AppDomain.CurrentDomain.BaseDirectory;
        ErrorLogFilePath = logFilePath ?? Path.Combine(_baseDirectory, "error.log");
    }

    /// <summary>
    /// Initializes global exception handlers to catch all unhandled exceptions.
    /// Must be called once at application startup.
    /// </summary>
    public void InitializeGlobalExceptionHandlers()
    {
        // Catch UI thread exceptions (WPF)
        System.Windows.Application.Current.DispatcherUnhandledException += (_, args) =>
        {
            const string context = "Unhandled exception in UI thread (Dispatcher)";
            LogErrorSync(args.Exception, context);
            args.Handled = true; // Prevent application crash
        };

        // Catch non-UI thread exceptions
        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
        {
            const string context = "Unhandled exception in AppDomain";
            if (args.ExceptionObject is Exception ex)
            {
                LogErrorSync(ex, context);
            }
            else
            {
                LogErrorSync(null, $"{context} - Exception object: {args.ExceptionObject}");
            }
        };

        // Catch TaskScheduler unobserved task exceptions
        TaskScheduler.UnobservedTaskException += (_, args) =>
        {
            const string context = "Unobserved task exception";
            LogErrorSync(args.Exception, context);
            args.SetObserved(); // Prevent application crash
        };
    }

    /// <summary>
    /// Reports an exception that was silently caught. Use this for exceptions that were
    /// previously being ignored with empty catch blocks.
    /// </summary>
    /// <param name="ex">The exception that was caught.</param>
    /// <param name="context">Description of where/why the exception occurred.</param>
    /// <param name="silent">If true, only logs to file without showing console output.</param>
    public void ReportSilentException(Exception ex, string context, bool silent = false)
    {
        try
        {
            var logContent = FormatErrorMessage(ex, $"[SILENT CATCH] {context}");

            try
            {
                File.AppendAllText(ErrorLogFilePath, logContent, Encoding.UTF8);
            }
            catch (Exception writeEx)
            {
                WriteToCriticalLog(writeEx, $"Failed to write silent exception to log. Context: {context}");
            }

            if (!silent)
            {
                Console.Error.WriteLine($"\n{AppTheme.Section("SILENT EXCEPTION CAUGHT")}");
                Console.Error.WriteLine($"Context: {context}");
                Console.Error.WriteLine($"Exception: {ex.GetType().Name} - {ex.Message}");
            }

            // Send to API in background (don't block)
            if (!IsUserError(ex))
            {
                _ = Task.Run(async () =>
                {
                    CancellationTokenSource? cts = null;
                    try
                    {
                        cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
                        await SendLogToApiAsync(ex, $"[SILENT CATCH] {context}", cts.Token);
                    }
                    catch
                    {
                        // Ignore API failures for silent exceptions
                    }
                    finally
                    {
                        cts?.Dispose();
                    }
                });
            }
        }
        catch
        {
            // Absolute last resort - ignore any failures in the reporting itself
        }
    }

    /// <summary>
    /// Logs an error synchronously. This method blocks until logging is complete
    /// and the API call has finished (or timed out after 30 seconds).
    /// Use this when the application is about to exit or crash.
    /// </summary>
    /// <param name="ex">The exception to log.</param>
    /// <param name="contextMessage">Additional context about where the error occurred.</param>
    public void LogErrorSync(Exception? ex, string? contextMessage = null)
    {
        var originalWasNull = ex == null;
        if (ex == null)
        {
            ex = new ArgumentNullException(nameof(ex), "ErrorLogger.LogErrorSync was called with a null exception object.");
        }

        contextMessage ??= "No additional context provided.";

        // Log to console immediately (synchronously)
        Console.Error.WriteLine($"\n{AppTheme.Section("ERROR (SYNC)")}");
        Console.Error.WriteLine($"Timestamp: {DateTime.Now}");
        Console.Error.WriteLine($"Context: {contextMessage}");
        Console.Error.WriteLine($"Exception Type: {ex.GetType().Name}");
        Console.Error.WriteLine($"Exception Message: {ex.Message}");
        Console.Error.WriteLine($"Stack Trace:\n{ex.StackTrace}");
        Console.Error.WriteLine($"{AppTheme.Section("END ERROR (SYNC)")}\n");

        var logContent = FormatErrorMessage(ex, contextMessage);

        try
        {
            File.AppendAllText(ErrorLogFilePath, logContent, Encoding.UTF8);
        }
        catch (Exception writeEx)
        {
            Console.Error.WriteLine($"Failed to write to local error log (sync): {writeEx.Message}");
            WriteToCriticalLog(writeEx, $"Failed to write main error to '{ErrorLogFilePath}'. Original error: {ex.Message}");
        }

        // Synchronously wait for the async API call to complete (with timeout)
        // This ensures exceptions are not lost if the app exits immediately after logging
        // Using Task.Run to avoid sync-over-async deadlock issues in contexts with a synchronization context
        if (!originalWasNull && !IsUserError(ex))
        {
            CancellationTokenSource? cts = null;
            try
            {
                cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));

                // Offload to thread pool to avoid blocking the current synchronization context
                // ReSharper disable once AccessToDisposedClosure
                var apiTask = Task.Run(async () => await SendLogToApiAsync(ex, contextMessage, cts.Token), cts.Token);
                var timeoutTask = Task.Delay(TimeSpan.FromSeconds(30), cts.Token);

                var completedTask = Task.WhenAny(apiTask, timeoutTask).GetAwaiter().GetResult();

                if (completedTask == timeoutTask)
                {
                    Console.Error.WriteLine("Timeout: Failed to send error details to remote logging service (from sync path). Error is saved locally.");
                }
                else
                {
                    // Await the API task to get the result and propagate any exceptions
                    var result = apiTask.GetAwaiter().GetResult();
                    if (result)
                    {
                        LogToService("Error details successfully sent to remote logging service (from sync path).");
                    }
                    else
                    {
                        Console.Error.WriteLine("Failed to send error details to remote logging service (from sync path). Error is saved locally.");
                    }
                }
            }
            catch (Exception apiEx)
            {
                WriteToCriticalLog(apiEx, "Exception in SendLogToApiAsync from LogErrorSync.");
            }
            finally
            {
                cts?.Dispose();
            }
        }
    }

    /// <summary>
    /// Logs an error asynchronously. This method returns immediately and logs in the background.
    /// Use this for normal error handling where the application continues running.
    /// </summary>
    /// <param name="ex">The exception to log.</param>
    /// <param name="contextMessage">Additional context about where the error occurred.</param>
    /// <param name="cancellationToken">Cancellation token for the async operation.</param>
    public async Task LogErrorAsync(Exception? ex, string? contextMessage = null, CancellationToken cancellationToken = default)
    {
        var originalWasNull = ex == null;
        if (ex == null)
        {
            ex = new ArgumentNullException(nameof(ex), "ErrorLogger.LogErrorAsync was called with a null exception object.");
        }

        contextMessage ??= "No additional context provided.";

        // Log to console immediately
        await Console.Error.WriteLineAsync($"\n{AppTheme.Section("ERROR (ASYNC)")}");
        await Console.Error.WriteLineAsync($"Timestamp: {DateTime.Now}");
        await Console.Error.WriteLineAsync($"Context: {contextMessage}");
        await Console.Error.WriteLineAsync($"Exception Type: {ex.GetType().Name}");
        await Console.Error.WriteLineAsync($"Exception Message: {ex.Message}");
        await Console.Error.WriteLineAsync($"Stack Trace:\n{ex.StackTrace}");
        await Console.Error.WriteLineAsync($"{AppTheme.Section("END ERROR (ASYNC)")}\n");

        var logContent = FormatErrorMessage(ex, contextMessage);

        try
        {
            await File.AppendAllTextAsync(ErrorLogFilePath, logContent, Encoding.UTF8, cancellationToken);
        }
        catch (Exception writeEx)
        {
            await Console.Error.WriteLineAsync($"Failed to write to local error log (async): {writeEx.Message}");
            WriteToCriticalLog(writeEx, $"Failed to write main error to '{ErrorLogFilePath}'. Original error: {ex.Message}");
        }

        if (!originalWasNull && !IsUserError(ex))
        {
            var sent = await SendLogToApiAsync(ex, contextMessage, cancellationToken);
            if (sent)
            {
                LogToService("Error details successfully sent to remote logging service (async path).");
            }
            else
            {
                await Console.Error.WriteLineAsync("Failed to send error details to remote logging service (async path). Error is saved locally.");
            }
        }
    }

    /// <summary>
    /// Logs a message to the logging service if available, otherwise falls back to console.
    /// </summary>
    private static void LogToService(string message)
    {
        try
        {
            var loggingService = ServiceProvider.TryGet<ILoggingService>();
            loggingService?.Log(message);
        }
        catch
        {
            // Ignore logging failures
        }
    }

    private static (string Version, string OsDescription, string OsArchitecture) GetBasicEnvironmentInfo()
    {
        var version = Assembly.GetExecutingAssembly().GetName().Version?.ToString() ?? "Unknown";
        var osDescription = RuntimeInformation.OSDescription;
        var osArchitecture = RuntimeInformation.OSArchitecture.ToString();
        return (version, osDescription, osArchitecture);
    }

    private static string FormatErrorMessage(Exception ex, string contextMessage)
    {
        var (version, osDescription, osArchitecture) = GetBasicEnvironmentInfo();
        var frameworkDescription = RuntimeInformation.FrameworkDescription;

        var fullErrorMessage = new StringBuilder();
        fullErrorMessage.AppendLine(CultureInfo.InvariantCulture, $"Timestamp: {DateTime.Now:yyyy-MM-dd HH:mm:ss zzz}");
        fullErrorMessage.AppendLine(CultureInfo.InvariantCulture, $"Application: {ApplicationName}");
        fullErrorMessage.AppendLine(CultureInfo.InvariantCulture, $"Version: {version}");
        fullErrorMessage.AppendLine(CultureInfo.InvariantCulture, $"Context: {contextMessage}");
        fullErrorMessage.AppendLine(CultureInfo.InvariantCulture, $"OS: {osDescription}");
        fullErrorMessage.AppendLine(CultureInfo.InvariantCulture, $"OS Architecture: {osArchitecture}");
        fullErrorMessage.AppendLine(CultureInfo.InvariantCulture, $"Framework: {frameworkDescription}");
        fullErrorMessage.AppendLine(CultureInfo.InvariantCulture, $"Exception Type: {ex.GetType().Name}");
        fullErrorMessage.AppendLine(CultureInfo.InvariantCulture, $"Exception Message: {ex.Message}");
        fullErrorMessage.AppendLine(CultureInfo.InvariantCulture, $"\n{AppTheme.Section("Stack Trace")}");
        fullErrorMessage.AppendLine(ex.StackTrace);
        if (ex.InnerException != null)
        {
            fullErrorMessage.AppendLine(CultureInfo.InvariantCulture, $"\n{AppTheme.Section("Inner Exception")}");
            fullErrorMessage.AppendLine(CultureInfo.InvariantCulture, $"Type: {ex.InnerException.GetType().Name}");
            fullErrorMessage.AppendLine(CultureInfo.InvariantCulture, $"Message: {ex.InnerException.Message}");
            fullErrorMessage.AppendLine(CultureInfo.InvariantCulture, $"Stack Trace:\n{ex.InnerException.StackTrace}");
        }

        fullErrorMessage.AppendLine(AppTheme.LogEntrySeparator);
        return fullErrorMessage.ToString();
    }

    private string GetEnvironmentDetails()
    {
        var (version, osDescription, osArchitecture) = GetBasicEnvironmentInfo();
        var processorCount = Environment.ProcessorCount;
        var tempPath = Path.GetTempPath();

        // Determine Windows version details
        var windowsVersion = Environment.OSVersion.VersionString;
        var bitness = Environment.Is64BitOperatingSystem ? "64-bit" : "32-bit";

        var envDetails = new StringBuilder();
        envDetails.AppendLine("=== Environment Details ===");
        envDetails.AppendLine(CultureInfo.InvariantCulture, $"Date: {DateTime.Now:yyyy-MM-dd HH:mm:ss zzz}");
        envDetails.AppendLine(CultureInfo.InvariantCulture, $"Application Name: {ApplicationName}");
        envDetails.AppendLine(CultureInfo.InvariantCulture, $"Application Version: {version}");
        envDetails.AppendLine(CultureInfo.InvariantCulture, $"OS Version: {osDescription}");
        envDetails.AppendLine(CultureInfo.InvariantCulture, $"Architecture: {osArchitecture}");
        envDetails.AppendLine(CultureInfo.InvariantCulture, $"Bitness: {bitness}");
        envDetails.AppendLine(CultureInfo.InvariantCulture, $"Windows Version: {windowsVersion}");
        envDetails.AppendLine(CultureInfo.InvariantCulture, $"Processor Count: {processorCount}");
        envDetails.AppendLine(CultureInfo.InvariantCulture, $"Base Directory: {_baseDirectory}");
        envDetails.AppendLine(CultureInfo.InvariantCulture, $"Temp Path: {tempPath}");

        return envDetails.ToString();
    }

    private static string GetErrorDetails(Exception ex, string contextMessage)
    {
        var errorDetails = new StringBuilder();
        errorDetails.AppendLine("=== Error Details ===");
        errorDetails.AppendLine(CultureInfo.InvariantCulture, $"Error message: {contextMessage} - {ex.Message}");

        return errorDetails.ToString();
    }

    private static string GetExceptionDetails(Exception ex)
    {
        var exceptionDetails = new StringBuilder();
        exceptionDetails.AppendLine("=== Exception Details ===");
        exceptionDetails.AppendLine(CultureInfo.InvariantCulture, $"Type: {ex.GetType().Name}");
        exceptionDetails.AppendLine(CultureInfo.InvariantCulture, $"Message: {ex.Message}");
        exceptionDetails.AppendLine(CultureInfo.InvariantCulture, $"Source: {ex.Source ?? "Unknown"}");
        exceptionDetails.AppendLine(CultureInfo.InvariantCulture, $"StackTrace: {ex.StackTrace ?? "No stack trace available"}");

        if (ex.InnerException != null)
        {
            exceptionDetails.AppendLine(CultureInfo.InvariantCulture, $"\n{AppTheme.Section("Inner Exception")}");
            exceptionDetails.AppendLine(CultureInfo.InvariantCulture, $"Type: {ex.InnerException.GetType().Name}");
            exceptionDetails.AppendLine(CultureInfo.InvariantCulture, $"Message: {ex.InnerException.Message}");
            exceptionDetails.AppendLine(CultureInfo.InvariantCulture, $"Source: {ex.InnerException.Source ?? "Unknown"}");
            exceptionDetails.AppendLine(CultureInfo.InvariantCulture, $"StackTrace: {ex.InnerException.StackTrace ?? "No stack trace available"}");
        }

        return exceptionDetails.ToString();
    }

    private static bool IsUserError(Exception? ex)
    {
        switch (ex)
        {
            case null:
                return false;
            // OperationCanceledException is expected during shutdown - not a bug
            case OperationCanceledException or TaskCanceledException:
                return true;
        }

        // Check exception types first (most reliable)
        var exceptionType = ex.GetType();
        var typeName = exceptionType.Name;
        var fullName = exceptionType.FullName ?? string.Empty;

        // SharpCompress archive-related exceptions indicate user-provided invalid files
        if (fullName.StartsWith("SharpCompress.", StringComparison.OrdinalIgnoreCase))
        {
            // ArchiveException, InvalidFormatException, etc. from SharpCompress are user errors
            if (typeName.Contains("Archive", StringComparison.OrdinalIgnoreCase) ||
                typeName.Contains("Format", StringComparison.OrdinalIgnoreCase) ||
                typeName.Contains("Invalid", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        // Dokan exceptions related to drive letter assignment
        if (fullName.StartsWith("DokanNet.", StringComparison.OrdinalIgnoreCase))
        {
            // Drive-related Dokan exceptions are typically user/environment errors
            if (typeName.Contains("Drive", StringComparison.OrdinalIgnoreCase) ||
                ex.Message.Contains("drive letter", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        switch (ex)
        {
            // File-related exceptions are typically user errors (file not found, access denied, etc.)
            case FileNotFoundException or DirectoryNotFoundException or UnauthorizedAccessException or IOException:
            // HttpRequestException with cancellation token is also expected
            case HttpRequestException { InnerException: OperationCanceledException }:
                return true;
        }

        // Fallback to message-based detection for cases where exception types aren't specific enough
        // These patterns cover common SharpCompress errors for corrupt/non-archive files
        var messageLower = ex.Message.ToLowerInvariant();

        // SharpCompress archive format errors
        var isArchiveError =
            messageLower.Contains("cannot find central directory") || // ZIP format error
            messageLower.Contains("invalid archive") ||
            messageLower.Contains("unknown format") ||
            messageLower.Contains("not a valid") ||
            messageLower.Contains("corrupt") ||
            (messageLower.Contains("header") && messageLower.Contains("invalid"));

        // Dokan drive-related errors
        var isDriveError =
            messageLower.Contains("can't assign a drive letter") ||
            (messageLower.Contains("drive letter") && messageLower.Contains("in use")) ||
            (messageLower.Contains("mount point") && messageLower.Contains("invalid"));

        // Password-related errors (user can retry with correct password)
        var isPasswordError =
            messageLower.Contains("password") ||
            messageLower.Contains("encrypted");

        // Cancellation-related messages
        var isCancellationError =
            messageLower.Contains("canceled") ||
            messageLower.Contains("cancelled") ||
            messageLower.Contains("timeout");

        return isArchiveError || isDriveError || isPasswordError || isCancellationError;
    }

    private async Task<bool> SendLogToApiAsync(Exception ex, string contextMessage, CancellationToken cancellationToken = default)
    {
        try
        {
            var (version, _, _) = GetBasicEnvironmentInfo();
            var environmentDetails = GetEnvironmentDetails();
            var errorDetails = GetErrorDetails(ex, contextMessage);
            var exceptionDetails = GetExceptionDetails(ex);

            // Combine all details into the message field
            var fullMessage = new StringBuilder();
            fullMessage.AppendLine(environmentDetails);
            fullMessage.AppendLine(errorDetails);
            fullMessage.AppendLine(exceptionDetails);

            var payload = new
            {
                message = fullMessage.ToString(),
                applicationName = ApplicationName,
                version,
                userInfo = contextMessage,
                stackTrace = ex.StackTrace ?? "No stack trace available"
            };
            var jsonPayload = JsonSerializer.Serialize(payload);
            var httpContent = new StringContent(jsonPayload, Encoding.UTF8, "application/json");

            var request = new HttpRequestMessage(HttpMethod.Post, BugReportApiUrl);
            request.Headers.Add("X-API-KEY", ApiKey);
            request.Content = httpContent;

            using var response = await _httpClient.SendAsync(request, cancellationToken);

            if (response.IsSuccessStatusCode)
            {
                return true;
            }
            else
            {
                var responseContent = await response.Content.ReadAsStringAsync(cancellationToken);
                WriteToCriticalLog(
                    new HttpRequestException($"API request failed with status code {response.StatusCode}. Response: {responseContent}"),
                    "Error sending log to API.");
                return false;
            }
        }
        catch (Exception apiEx)
        {
            WriteToCriticalLog(apiEx, "Exception occurred while sending log to API.");
            return false;
        }
    }

    private void WriteToCriticalLog(Exception ex, string contextMessage)
    {
        // Only write to console - do not attempt to write to file as it will fail
        // if the application is in a protected directory (e.g., C:\Program Files)
        // and running as non-admin. Console output is always available.
        Console.Error.WriteLine($"FATAL: Could not write to error log '{ErrorLogFilePath}'.");
        Console.Error.WriteLine($"Context: {contextMessage}");
        Console.Error.WriteLine($"Exception: {ex.GetType().Name} - {ex.Message}");
    }

    /// <summary>
    /// Disposes the HttpClient instance.
    /// </summary>
    public void Dispose()
    {
        _httpClient.Dispose();
        GC.SuppressFinalize(this);
    }
}

/// <summary>
/// Static wrapper for the ErrorLogger instance for backward compatibility.
/// Provides global access to a singleton ErrorLogger instance.
/// </summary>
public static class ErrorLoggerStatic
{
    private static readonly Lazy<ErrorLogger> Instance2 = new(static () => new ErrorLogger());

    /// <summary>
    /// Gets the singleton ErrorLogger instance.
    /// </summary>
    public static ErrorLogger Instance => Instance2.Value;

    /// <summary>
    /// Initializes global exception handlers to catch all unhandled exceptions.
    /// Must be called once at application startup.
    /// </summary>
    public static void InitializeGlobalExceptionHandlers()
    {
        Instance2.Value.InitializeGlobalExceptionHandlers();
    }

    /// <summary>
    /// Reports an exception that was silently caught. Use this for exceptions that were
    /// previously being ignored with empty catch blocks.
    /// </summary>
    /// <param name="ex">The exception that was caught.</param>
    /// <param name="context">Description of where/why the exception occurred.</param>
    /// <param name="silent">If true, only logs to file without showing console output.</param>
    public static void ReportSilentException(Exception ex, string context, bool silent = false)
    {
        Instance2.Value.ReportSilentException(ex, context, silent);
    }

    /// <summary>
    /// Logs an error synchronously. This method blocks until logging is complete
    /// and the API call has finished (or timed out after 30 seconds).
    /// Use this when the application is about to exit or crash.
    /// </summary>
    /// <param name="ex">The exception to log.</param>
    /// <param name="contextMessage">Additional context about where the error occurred.</param>
    public static void LogErrorSync(Exception? ex, string? contextMessage = null)
    {
        Instance2.Value.LogErrorSync(ex, contextMessage);
    }

    /// <summary>
    /// Logs an error asynchronously. This method returns immediately and logs in the background.
    /// Use this for normal error handling where the application continues running.
    /// </summary>
    /// <param name="ex">The exception to log.</param>
    /// <param name="contextMessage">Additional context about where the error occurred.</param>
    /// <param name="cancellationToken">Cancellation token for the async operation.</param>
    public static Task LogErrorAsync(Exception? ex, string? contextMessage = null, CancellationToken cancellationToken = default)
    {
        return Instance2.Value.LogErrorAsync(ex, contextMessage, cancellationToken);
    }
}
