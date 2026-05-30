using System.Globalization;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;

namespace SimpleZipDrive_WinFsp;

public class ErrorLogger : IDisposable
{
    private const string ApiKey = "hjh7yu6t56tyr540o9u8767676r5674534453235264c75b6t7ggghgg76trf564e";
    private const string BugReportApiUrl = "https://www.purelogiccode.com/bugreport/api/send-bug-report";

    private const string ApplicationName = "SimpleZipDrive_WinFsp";
    private readonly HttpClient _httpClient;

    private readonly string _baseDirectory;

    /// <summary>
    /// When true, all API calls for bug reports are suppressed (no HTTP requests are made).
    /// Use this in test environments to prevent sending test-generated errors to the bug report API.
    /// </summary>
    public static bool SuppressApiCalls { get; set; }

    internal string ErrorLogFilePath { get; set; }

    public ErrorLogger(string? logFilePath = null)
    {
        _httpClient = new HttpClient
        {
            Timeout = TimeSpan.FromSeconds(30)
        };
        _baseDirectory = AppDomain.CurrentDomain.BaseDirectory;
        ErrorLogFilePath = logFilePath ?? Path.Combine(_baseDirectory, "error.log");
    }

    public void InitializeGlobalExceptionHandlers()
    {
        System.Windows.Application.Current.DispatcherUnhandledException += (_, args) =>
        {
            const string context = "Unhandled exception in UI thread (Dispatcher)";
            FireAndForgetAsync(LogErrorAsync(args.Exception, context));
            args.Handled = true;
        };

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

        TaskScheduler.UnobservedTaskException += (_, args) =>
        {
            const string context = "Unobserved task exception";
            LogErrorSync(args.Exception, context);
            args.SetObserved();
        };
    }

    public void ReportSilentException(Exception ex, string context, bool silent = false)
    {
        DiagnosticLogger.Log(ex, $"[SILENT] {context}");
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

            if (!IsUserError(ex))
            {
                FireAndForgetAsync(Task.Run(async () =>
                {
                    CancellationTokenSource? cts = null;
                    try
                    {
                        cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
                        await SendLogToApiAsync(ex, $"[SILENT CATCH] {context}", cts.Token);
                    }
                    catch
                    {
                        // ignored
                    }
                    finally
                    {
                        cts?.Dispose();
                    }
                }));
            }
        }
        catch
        {
            // ignored
        }
    }

    public void LogErrorSync(Exception? ex, string? contextMessage = null)
    {
        var originalWasNull = ex == null;
        if (ex == null)
        {
            ex = new ArgumentNullException(nameof(ex), "ErrorLogger.LogErrorSync was called with a null exception object.");
        }

        contextMessage ??= "No additional context provided.";

        DiagnosticLogger.LogSection("ERROR (SYNC)");
        DiagnosticLogger.Log(ex, contextMessage);

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

        if (!originalWasNull && !IsUserError(ex))
        {
            CancellationTokenSource? cts = null;
            try
            {
                cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
                var token = cts.Token;
                var apiTask = Task.Run(async () => await SendLogToApiAsync(ex, contextMessage, token), token);
                var timeoutTask = Task.Delay(TimeSpan.FromSeconds(30), token);

                var completedTask = Task.WhenAny(apiTask, timeoutTask).GetAwaiter().GetResult();

                if (completedTask == timeoutTask)
                {
                    Console.Error.WriteLine("Timeout: Failed to send error details to remote logging service (from sync path). Error is saved locally.");
                }
                else
                {
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

    public async Task LogErrorAsync(Exception? ex, string? contextMessage = null, CancellationToken cancellationToken = default)
    {
        var originalWasNull = ex == null;
        if (ex == null)
        {
            ex = new ArgumentNullException(nameof(ex), "ErrorLogger.LogErrorAsync was called with a null exception object.");
        }

        contextMessage ??= "No additional context provided.";

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

    private static async void FireAndForgetAsync(Task task)
    {
        try
        {
            await task;
        }
        catch
        {
            // ignored
        }
    }

    private static void LogToService(string message)
    {
        try
        {
            var loggingService = ServiceProvider.TryGet<ILoggingService>();
            loggingService?.Log(message);
        }
        catch
        {
            // ignored
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
            case OperationCanceledException or TaskCanceledException:
                return true;
        }

        var fullName = ex.GetType().FullName ?? string.Empty;

        // Handle WinFsp errors (instead of Dokan)
        if (fullName.StartsWith("Fsp.", StringComparison.OrdinalIgnoreCase))
        {
            var typeName = ex.GetType().Name;
            if (typeName.Contains("Drive", StringComparison.OrdinalIgnoreCase) ||
                ex.Message.Contains("drive letter", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        switch (ex)
        {
            case FileNotFoundException or DirectoryNotFoundException or UnauthorizedAccessException or IOException:
            case HttpRequestException { InnerException: OperationCanceledException }:
                return true;
        }

        var messageLower = ex.Message.ToLowerInvariant();

        var isArchiveError =
            messageLower.Contains("cannot find central directory") ||
            messageLower.Contains("invalid archive") ||
            messageLower.Contains("unknown format") ||
            messageLower.Contains("not a valid") ||
            messageLower.Contains("corrupt") ||
            (messageLower.Contains("header") && messageLower.Contains("invalid"));

        var isDriveError =
            messageLower.Contains("can't assign a drive letter") ||
            (messageLower.Contains("drive letter") && messageLower.Contains("in use")) ||
            (messageLower.Contains("mount point") && messageLower.Contains("invalid"));

        var isPasswordError =
            messageLower.Contains("password") ||
            messageLower.Contains("encrypted");

        var isCancellationError =
            messageLower.Contains("canceled") ||
            messageLower.Contains("cancelled") ||
            messageLower.Contains("timeout");

        return isArchiveError || isDriveError || isPasswordError || isCancellationError;
    }

    private async Task<bool> SendLogToApiAsync(Exception ex, string contextMessage, CancellationToken cancellationToken = default)
    {
        if (SuppressApiCalls)
            return false;

        try
        {
            var (version, _, _) = GetBasicEnvironmentInfo();
            var environmentDetails = GetEnvironmentDetails();
            var errorDetails = GetErrorDetails(ex, contextMessage);
            var exceptionDetails = GetExceptionDetails(ex);

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
                environment = environmentDetails,
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
        Console.Error.WriteLine($"FATAL: Could not write to error log '{ErrorLogFilePath}'.");
        Console.Error.WriteLine($"Context: {contextMessage}");
        Console.Error.WriteLine($"Exception: {ex.GetType().Name} - {ex.Message}");
    }

    public void Dispose()
    {
        _httpClient.Dispose();
        GC.SuppressFinalize(this);
    }
}

public static class ErrorLoggerStatic
{
    private static readonly Lazy<ErrorLogger> Instance2 = new(static () => new ErrorLogger());

    public static ErrorLogger Instance => Instance2.Value;

    public static void InitializeGlobalExceptionHandlers()
    {
        Instance2.Value.InitializeGlobalExceptionHandlers();
    }

    public static void ReportSilentException(Exception ex, string context, bool silent = false)
    {
        Instance2.Value.ReportSilentException(ex, context, silent);
    }

    public static void LogErrorSync(Exception? ex, string? contextMessage = null)
    {
        Instance2.Value.LogErrorSync(ex, contextMessage);
    }

    public static Task LogErrorAsync(Exception? ex, string? contextMessage = null, CancellationToken cancellationToken = default)
    {
        return Instance2.Value.LogErrorAsync(ex, contextMessage, cancellationToken);
    }
}
