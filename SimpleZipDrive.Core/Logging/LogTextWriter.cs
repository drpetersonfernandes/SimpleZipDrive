using System.Text;
using System.Threading.Channels;

namespace SimpleZipDrive.Core.Logging;

/// <summary>
/// Redirects <see cref="Console"/> output to the <see cref="ILoggingService"/> using an async-friendly
/// channel for high throughput. Shared by both application hosts so console output is captured consistently.
/// </summary>
public sealed class LogTextWriter : TextWriter
{
    /// <inheritdoc />
    public override Encoding Encoding => Encoding.UTF8;

    private readonly Channel<string> _channel;
    private readonly CancellationTokenSource _cts = new();
    private readonly Task _processingTask;
    private readonly TextWriter? _fallbackWriter;

    /// <summary>Creates a new writer that forwards console output to the logging service.</summary>
    /// <param name="fallbackWriter">Writer used when the logging service is unavailable (e.g. during shutdown).</param>
    public LogTextWriter(TextWriter? fallbackWriter = null)
    {
        _fallbackWriter = fallbackWriter;

        // Unbounded channel for maximum throughput - messages are processed asynchronously.
        _channel = Channel.CreateUnbounded<string>(new UnboundedChannelOptions
        {
            SingleReader = true,
            SingleWriter = false
        });

        _processingTask = ProcessMessagesAsync(_cts.Token);
    }

    /// <inheritdoc />
    public override void Write(char value)
    {
        _channel.Writer.TryWrite(value.ToString());
    }

    /// <inheritdoc />
    public override void Write(string? value)
    {
        if (string.IsNullOrEmpty(value)) return;

        _channel.Writer.TryWrite(value);
    }

    /// <inheritdoc />
    public override void Write(char[] buffer, int index, int count)
    {
        if (count <= 0) return;

        _channel.Writer.TryWrite(new string(buffer, index, count));
    }

    /// <inheritdoc />
    public override void WriteLine()
    {
        _channel.Writer.TryWrite(CoreNewLine.ToString() ?? Environment.NewLine);
        _channel.Writer.TryWrite(string.Empty); // Empty string signals end of line
    }

    /// <inheritdoc />
    public override void WriteLine(string? value)
    {
        _channel.Writer.TryWrite(string.Concat(value, CoreNewLine));
        _channel.Writer.TryWrite(string.Empty); // Signal end of line to flush buffer
    }

    /// <inheritdoc />
    public override void WriteLine(ReadOnlySpan<char> value)
    {
        _channel.Writer.TryWrite(string.Concat(value, CoreNewLine));
        _channel.Writer.TryWrite(string.Empty); // Signal end of line to flush buffer
    }

    private async Task ProcessMessagesAsync(CancellationToken cancellationToken)
    {
        var buffer = new StringBuilder();

        try
        {
            await foreach (var message in _channel.Reader.ReadAllAsync(cancellationToken))
            {
                // Empty string signals a line ending.
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
            // Normal shutdown.
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

        var loggingService = ServiceProvider.TryGet<ILoggingService>();
        if (loggingService != null)
        {
            // The logging service mirrors into the single Serilog pipeline (and the session log file).
            loggingService.Log(message);
        }
        else
        {
            // Fallback: write to the original console if the service is not available.
            _fallbackWriter?.WriteLine(message);
        }
    }

    /// <inheritdoc />
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
