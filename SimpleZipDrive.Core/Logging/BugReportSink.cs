using Serilog.Core;
using Serilog.Events;

namespace SimpleZipDrive.Core.Logging;

/// <summary>
/// A Serilog sink that forwards warning-and-above log events to the remote bug report API
/// using the existing <see cref="ErrorLogger"/> service.
/// Expected user/environment errors (see <see cref="ErrorLogger.IsUserError"/>) are filtered out
/// so the API is not flooded with non-actionable noise such as bad archives, wrong passwords,
/// or cancellations.
/// </summary>
public sealed class BugReportSink : ILogEventSink
{
    /// <summary>
    /// Property name that, when present and <see langword="true"/> on a log event, prevents this sink
    /// from forwarding the event to the API. Used to avoid double-reporting events that were already
    /// sent through <see cref="ErrorLogger"/> directly.
    /// </summary>
    public const string SkipProperty = "SkipBugReport";

    [ThreadStatic] private static bool _reentrancyGuard;

    /// <inheritdoc />
    public void Emit(LogEvent logEvent)
    {
        if (logEvent.Level < LogEventLevel.Warning)
            return;

        // Prevent any accidental recursion (e.g. if forwarding itself logs at warning+).
        if (_reentrancyGuard)
            return;

        if (logEvent.Properties.TryGetValue(SkipProperty, out var skip) &&
            skip is ScalarValue { Value: true })
            return;

        var exception = logEvent.Exception;
        var message = logEvent.RenderMessage();

        // Apply the same user-error filtering used for exceptions. For message-only events we
        // wrap the rendered text so the message-based heuristics still apply.
        var classification = exception ?? new InvalidOperationException(message);
        if (ErrorLogger.IsUserError(classification))
            return;

        _reentrancyGuard = true;
        try
        {
            ErrorLoggerStatic.Instance.ForwardLogEventToApi(
                logEvent.Level.ToString(),
                message,
                exception,
                $"Serilog {logEvent.Level} log event");
        }
        catch
        {
            // Forwarding is best-effort; never let logging throw.
        }
        finally
        {
            _reentrancyGuard = false;
        }
    }
}
