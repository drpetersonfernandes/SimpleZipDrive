using SimpleZipDrive.Core.Services;

namespace SimpleZipDrive.Tests;

public class LoggingServiceAdditionalTests : IDisposable
{
    private readonly LoggingService _service = new();

    // ─── Log: message with only \r ───

    [Fact]
    public void Log_OnlyCarriageReturn_TrimsCorrectly()
    {
        _service.Log("Message\r");

        Assert.Single(_service.LogEntries);
        Assert.Equal("Message", _service.LogEntries[0].Message);
    }

    // ─── Log: message with \r\n ───

    [Fact]
    public void Log_CRLF_TrimsCorrectly()
    {
        _service.Log("Message\r\n");

        Assert.Single(_service.LogEntries);
        Assert.Equal("Message", _service.LogEntries[0].Message);
    }

    // ─── Log: message with \n ───

    [Fact]
    public void Log_LF_TrimsCorrectly()
    {
        _service.Log("Message\n");

        Assert.Single(_service.LogEntries);
        Assert.Equal("Message", _service.LogEntries[0].Message);
    }

    // ─── Log: message with multiple trailing newlines ───

    [Fact]
    public void Log_MultipleTrailingNewlines_TrimsAll()
    {
        _service.Log("Message\r\n\r\n");

        Assert.Single(_service.LogEntries);
        Assert.Equal("Message", _service.LogEntries[0].Message);
    }

    // ─── Log: whitespace-only message is added ───

    [Fact]
    public void Log_WhitespaceOnly_IsAdded()
    {
        _service.Log("   ");

        Assert.Single(_service.LogEntries);
        Assert.Equal("   ", _service.LogEntries[0].Message);
    }

    // ─── LogError: null does not add entry ───

    [Fact]
    public void LogError_Null_DoesNotAddEntry()
    {
        _service.LogError(null!);
        Assert.Empty(_service.LogEntries);
    }

    // ─── GetAllLogsAsText: includes log entries ───

    [Fact]
    public void GetAllLogsAsText_IncludesLogEntries()
    {
        _service.Log("Test message");

        var result = _service.GetAllLogsAsText();
        Assert.Contains("Test message", result);
    }

    // ─── GetAllLogsAsText: error prefix ───

    [Fact]
    public void GetAllLogsAsText_ErrorMessages_IncludeErrorPrefix()
    {
        _service.LogError("Error occurred");

        var result = _service.GetAllLogsAsText();
        Assert.Contains("[ERROR]", result);
    }

    public void Dispose()
    {
        _service.Clear();
        GC.SuppressFinalize(this);
    }
}
