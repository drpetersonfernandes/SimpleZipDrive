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
    private static readonly bool IsApiLoggingConfigured; // Renamed for clarity

    private static readonly string BaseDirectory = AppDomain.CurrentDomain.BaseDirectory;
    private static readonly string ErrorLogFilePath = Path.Combine(BaseDirectory, "error.log");
    private static readonly string CriticalLogFilePath = Path.Combine(BaseDirectory, "critical_error.log");

    static ErrorLogger()
    {
        HttpClientInstance = new HttpClient
        {
            Timeout = TimeSpan.FromSeconds(30)
        };

        // Check if API key and URL are actual values, not placeholders
        IsApiLoggingConfigured = !string.IsNullOrWhiteSpace(ApiKey) &&
                                 !ApiKey.Equals("YOUR_API_KEY_HERE", StringComparison.OrdinalIgnoreCase) && // General placeholder check
                                 !ApiKey.Equals("hjh7yu6t56tyr540o9u8767676r5674534453235264c75b6t7ggghgg76trf564e", StringComparison.Ordinal) && // Specific placeholder check
                                 !string.IsNullOrWhiteSpace(BugReportApiUrl) &&
                                 !BugReportApiUrl.Equals("YOUR_BUG_REPORT_API_URL_HERE", StringComparison.OrdinalIgnoreCase) && // General placeholder
                                 !BugReportApiUrl.Equals("https://www.purelogiccode.com/bugreport/api/send-bug-report", StringComparison.OrdinalIgnoreCase); // Specific placeholder

        // The logic for IsApiLoggingEnabled was inverted. It should be true if NOT placeholders.
        // Corrected logic: API logging is enabled if the configured values are NOT the placeholder values.
        // The original logic `!ApiKey.Equals("...", StringComparison.Ordinal)` would be true if ApiKey is *different* from the placeholder,
        // which means API logging *should* be enabled.
        // Let's simplify: if the key/URL *are* the hardcoded actual values, then it's enabled.
        // The previous check was effectively disabling it if the actual values were used.

        // Corrected check:
        IsApiLoggingConfigured = ApiKey == "hjh7yu6t56tyr540o9u8767676r5674534453235264c75b6t7ggghgg76trf564e" &&
                                 BugReportApiUrl == "https://www.purelogiccode.com/bugreport/api/send-bug-report";


        if (!IsApiLoggingConfigured)
        {
            Console.Error.WriteLine("ErrorLogger: API logging is disabled. API Key/URL are placeholders or not the expected configured values.");
            // No need to call WriteToCriticalLog here for placeholder issue, console message is enough.
        }
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

    public static void LogErrorSync(Exception? ex, string? contextMessage = null)
    {
        if (ex == null)
        {
            ex = new Exception("ErrorLogger.LogErrorSync was called with a null exception object.");
            try
            {
                throw ex;
            }
            catch
            {
                /* ex now has a stack trace */
            }
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

        if (IsApiLoggingConfigured)
        {
            // Fire-and-forget the async API call from the synchronous method
            _ = SendLogToApiAsync(logContent).ContinueWith(task =>
            {
                if (task.IsFaulted && task.Exception != null)
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
        else
        {
            Console.WriteLine("API error logging is disabled. Error is saved locally (sync path).");
        }
    }


    public static async Task LogErrorAsync(Exception? ex, string? contextMessage = null)
    {
        if (ex == null)
        {
            ex = new Exception("ErrorLogger.LogErrorAsync was called with a null exception object.");
            try
            {
                throw ex;
            }
            catch
            {
                /* ex now has a stack trace */
            }
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
            await File.AppendAllTextAsync(ErrorLogFilePath, logContent, Encoding.UTF8);
        }
        catch (Exception writeEx)
        {
            await Console.Error.WriteLineAsync($"Failed to write to local error log (async): {writeEx.Message}");
            WriteToCriticalLog(writeEx, $"Failed to write main error to '{ErrorLogFilePath}'. Original error: {ex.Message}");
        }

        if (IsApiLoggingConfigured)
        {
            var sent = await SendLogToApiAsync(logContent);
            if (sent)
            {
                Console.WriteLine("Error details successfully sent to remote logging service (async path).");
            }
            else
            {
                await Console.Error.WriteLineAsync("Failed to send error details to remote logging service (async path). Error is saved locally.");
            }
        }
        else
        {
            Console.WriteLine("API error logging is disabled. Error is saved locally (async path).");
        }
    }

    private static async Task<bool> SendLogToApiAsync(string logContent)
    {
        if (!IsApiLoggingConfigured) return false;

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

            using var response = await HttpClientInstance.SendAsync(request);

            if (response.IsSuccessStatusCode)
            {
                return true;
            }
            else
            {
                var responseContent = await response.Content.ReadAsStringAsync();
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
        try
        {
            var version = Assembly.GetExecutingAssembly().GetName().Version?.ToString() ?? "Unknown";
            var criticalContent = new StringBuilder();
            criticalContent.AppendLine("--- CRITICAL LOGGING ERROR ---");
            criticalContent.AppendLine(CultureInfo.InvariantCulture, $"Timestamp: {DateTime.Now:yyyy-MM-dd HH:mm:ss zzz}");
            criticalContent.AppendLine(CultureInfo.InvariantCulture, $"Application: {ApplicationName}");
            criticalContent.AppendLine(CultureInfo.InvariantCulture, $"Version: {version}");
            criticalContent.AppendLine(CultureInfo.InvariantCulture, $"Context: {contextMessage}");
            criticalContent.AppendLine(CultureInfo.InvariantCulture, $"Exception Type: {ex.GetType().Name}");
            criticalContent.AppendLine(CultureInfo.InvariantCulture, $"Exception Message: {ex.Message}");
            criticalContent.AppendLine(CultureInfo.InvariantCulture, $"Stack Trace:\n{ex.StackTrace}");
            criticalContent.AppendLine("--------------------------------------------------\n");

            File.AppendAllText(CriticalLogFilePath, criticalContent.ToString(), Encoding.UTF8);
            Console.Error.WriteLine($"Critical logging error recorded: {contextMessage} - {ex.Message}");
        }
        catch (Exception writeEx)
        {
            Console.Error.WriteLine($"FATAL: Could not write to critical error log '{CriticalLogFilePath}'. Reason: {writeEx.Message}");
            Console.Error.WriteLine($"Original critical error: {contextMessage} - {ex.Message}");
        }
    }
}
