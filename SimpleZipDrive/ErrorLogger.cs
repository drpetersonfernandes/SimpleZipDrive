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

        // File-related exceptions are typically user errors (file not found, access denied, etc.)
        if (ex is FileNotFoundException or DirectoryNotFoundException or UnauthorizedAccessException or IOException)
        {
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

        return isArchiveError || isDriveError || isPasswordError;
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

        // Synchronously wait for the async API call to complete (with timeout)
        // This ensures exceptions are not lost if the app exits immediately after logging
        if (!IsUserError(ex))
        {
            try
            {
                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
                var task = SendLogToApiAsync(logContent, cts.Token);
                task.Wait(cts.Token);

                if (task.Result)
                {
                    Console.WriteLine("Error details successfully sent to remote logging service (from sync path).");
                }
                else
                {
                    Console.Error.WriteLine("Failed to send error details to remote logging service (from sync path). Error is saved locally.");
                }
            }
            catch (OperationCanceledException)
            {
                Console.Error.WriteLine("Timeout: Failed to send error details to remote logging service (from sync path). Error is saved locally.");
            }
            catch (Exception apiEx)
            {
                WriteToCriticalLog(apiEx, "Exception in SendLogToApiAsync from LogErrorSync.");
            }
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