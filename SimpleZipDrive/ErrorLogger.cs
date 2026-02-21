using System.Globalization;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;

namespace SimpleZipDrive;

public static class ErrorLogger
{
    private const string ApiKey = "hjh7yu6t56tyr540o9u8767676r5674534453235264c75b6t7ggghgg76trf564e";
    private const string BugReportApiUrl = "https://www.purelogiccode.com/bugreport/api/send-bug-report";

    private const string ApplicationName = "SimpleZipDrive";
    private static readonly HttpClient HttpClientInstance;

    private static readonly string BaseDirectory = AppDomain.CurrentDomain.BaseDirectory;
    private static readonly string ErrorLogFilePath = Path.Combine(BaseDirectory, "error.log");

    static ErrorLogger()
    {
        HttpClientInstance = new HttpClient
        {
            Timeout = TimeSpan.FromSeconds(30)
        };
    }

    private static string FormatErrorMessage(Exception ex, string contextMessage)
    {
        var version = Assembly.GetExecutingAssembly().GetName().Version?.ToString() ?? "Unknown";
        var osDescription = RuntimeInformation.OSDescription;
        var osArchitecture = RuntimeInformation.OSArchitecture.ToString();
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
        fullErrorMessage.AppendLine("\n--- Stack Trace ---");
        fullErrorMessage.AppendLine(ex.StackTrace);
        if (ex.InnerException != null)
        {
            fullErrorMessage.AppendLine("\n--- Inner Exception ---");
            fullErrorMessage.AppendLine(CultureInfo.InvariantCulture, $"Type: {ex.InnerException.GetType().Name}");
            fullErrorMessage.AppendLine(CultureInfo.InvariantCulture, $"Message: {ex.InnerException.Message}");
            fullErrorMessage.AppendLine(CultureInfo.InvariantCulture, $"Stack Trace:\n{ex.InnerException.StackTrace}");
        }

        fullErrorMessage.AppendLine("--------------------------------------------------\n");
        return fullErrorMessage.ToString();
    }

    private static bool IsUserError(Exception? ex)
    {
        if (ex == null) return false;

        // "Cannot find central directory" is the standard SharpZipLib error for non-zip or corrupt zip files.
        var isZipError = ex.Message.Contains("Cannot find central directory", StringComparison.OrdinalIgnoreCase);

        // "Can't assign a drive letter" is a Dokan error, usually meaning the drive is already in use.
        var isDriveInUse = ex.Message.Contains("Can't assign a drive letter", StringComparison.OrdinalIgnoreCase);

        return isZipError || isDriveInUse;
    }

    public static void LogErrorSync(Exception? ex, string? contextMessage = null)
    {
        if (ex == null)
        {
            ex = new ArgumentNullException(nameof(ex), "ErrorLogger.LogErrorSync was called with a null exception object.");
        }

        contextMessage ??= "No additional context provided.";

        // Log to console immediately (synchronously)
        Console.Error.WriteLine("\n--- ERROR (SYNC) ---");
        Console.Error.WriteLine($"Timestamp: {DateTime.Now}");
        Console.Error.WriteLine($"Context: {contextMessage}");
        Console.Error.WriteLine($"Exception Type: {ex.GetType().Name}");
        Console.Error.WriteLine($"Exception Message: {ex.Message}");
        Console.Error.WriteLine($"Stack Trace:\n{ex.StackTrace}");
        Console.Error.WriteLine("--- END ERROR (SYNC) ---\n");

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

        // Fire-and-forget the async API call from the synchronous method
        if (!IsUserError(ex))
        {
            // Use a timeout-based cancellation token for fire-and-forget operations
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            _ = SendLogToApiAsync(logContent, cts.Token).ContinueWith(static task =>
            {
                if (task is { IsFaulted: true, Exception: not null })
                {
                    WriteToCriticalLog(task.Exception.Flatten().InnerExceptions.FirstOrDefault() ?? task.Exception,
                        "Exception in fire-and-forget SendLogToApiAsync from LogErrorSync.");
                }
                else if (task.IsCompletedSuccessfully)
                {
                    if (task.Result) // if sent successfully
                    {
                        Console.WriteLine("Error details successfully sent to remote logging service (from sync path).");
                    }
                    else
                    {
                        Console.Error.WriteLine("Failed to send error details to remote logging service (from sync path). Error is saved locally.");
                    }
                }
            }, TaskScheduler.Default);
        }
    }


    public static async Task LogErrorAsync(Exception? ex, string? contextMessage = null, CancellationToken cancellationToken = default)
    {
        if (ex == null)
        {
            ex = new ArgumentNullException(nameof(ex), "ErrorLogger.LogErrorAsync was called with a null exception object.");
        }

        contextMessage ??= "No additional context provided.";

        // Log to console immediately
        await Console.Error.WriteLineAsync("\n--- ERROR (ASYNC) ---");
        await Console.Error.WriteLineAsync($"Timestamp: {DateTime.Now}");
        await Console.Error.WriteLineAsync($"Context: {contextMessage}");
        await Console.Error.WriteLineAsync($"Exception Type: {ex.GetType().Name}");
        await Console.Error.WriteLineAsync($"Exception Message: {ex.Message}");
        await Console.Error.WriteLineAsync($"Stack Trace:\n{ex.StackTrace}");
        await Console.Error.WriteLineAsync("--- END ERROR (ASYNC) ---\n");

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

        if (!IsUserError(ex))
        {
            var sent = await SendLogToApiAsync(logContent, cancellationToken);
            if (sent)
            {
                Console.WriteLine("Error details successfully sent to remote logging service (async path).");
            }
            else
            {
                await Console.Error.WriteLineAsync("Failed to send error details to remote logging service (async path). Error is saved locally.");
            }
        }
    }

    private static async Task<bool> SendLogToApiAsync(string logContent, CancellationToken cancellationToken = default)
    {
        try
        {
            var payload = new
            {
                message = logContent,
                applicationName = ApplicationName
            };
            var jsonPayload = JsonSerializer.Serialize(payload);
            var httpContent = new StringContent(jsonPayload, Encoding.UTF8, "application/json");

            var request = new HttpRequestMessage(HttpMethod.Post, BugReportApiUrl);
            request.Headers.Add("X-API-KEY", ApiKey);
            request.Content = httpContent;

            using var response = await HttpClientInstance.SendAsync(request, cancellationToken);

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

    private static void WriteToCriticalLog(Exception ex, string contextMessage)
    {
        // Only write to console - do not attempt to write to file as it will fail
        // if the application is in a protected directory (e.g., C:\Program Files)
        // and running as non-admin. Console output is always available.
        Console.Error.WriteLine($"FATAL: Could not write to error log '{ErrorLogFilePath}'.");
        Console.Error.WriteLine($"Context: {contextMessage}");
        Console.Error.WriteLine($"Exception: {ex.GetType().Name} - {ex.Message}");
    }
}