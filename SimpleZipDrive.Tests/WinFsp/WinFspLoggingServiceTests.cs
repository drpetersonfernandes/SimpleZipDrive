using SimpleZipDrive_WinFsp.Services;

namespace SimpleZipDrive.Tests.WinFsp;

public class WinFspLoggingServiceTests : IDisposable
{
    private readonly LoggingService _service = new();

    [Fact]
    public void Constructor_CreatesEmptyLogEntries()
    {
        Assert.NotNull(_service.LogEntries);
        Assert.Empty(_service.LogEntries);
    }

    [Fact]
    public void Log_AddsEntryToCollection()
    {
        _service.Log("Test message");

        Assert.Single(_service.LogEntries);
        Assert.Equal("Test message", _service.LogEntries[0].Message);
        Assert.False(_service.LogEntries[0].IsError);
    }

    [Fact]
    public void LogError_AddsErrorEntryToCollection()
    {
        _service.LogError("Error message");

        Assert.Single(_service.LogEntries);
        Assert.Equal("Error message", _service.LogEntries[0].Message);
        Assert.True(_service.LogEntries[0].IsError);
    }

    [Fact]
    public void Log_TrimsTrailingNewlines()
    {
        _service.Log("Message with newline\r\n");

        Assert.Single(_service.LogEntries);
        Assert.Equal("Message with newline", _service.LogEntries[0].Message);
    }

    [Fact]
    public void Log_Null_ThrowsNullReferenceException()
    {
        // WinFsp LoggingService doesn't have a null guard (unlike Dokan variant)
        Assert.ThrowsAny<NullReferenceException>(() => _service.Log(null!));
    }

    [Fact]
    public void Log_EmptyString_AddsEmptyLineEntry()
    {
        _service.Log("");

        Assert.Single(_service.LogEntries);
        Assert.Equal(string.Empty, _service.LogEntries[0].Message);
        Assert.False(_service.LogEntries[0].IsError);
    }

    [Fact]
    public void LogError_TrimsTrailingNewlines()
    {
        _service.LogError("Error with newline\n");

        Assert.Single(_service.LogEntries);
        Assert.Equal("Error with newline", _service.LogEntries[0].Message);
    }

    [Fact]
    public void LogError_NullOrWhitespace_DoesNotAddEntry()
    {
        _service.LogError("");
        _service.LogError("   ");
        _service.LogError(null!);

        Assert.Empty(_service.LogEntries);
    }

    [Fact]
    public void Clear_RemovesAllEntries()
    {
        _service.Log("Message 1");
        _service.Log("Message 2");

        Assert.Equal(2, _service.LogEntries.Count);

        _service.Clear();

        Assert.Empty(_service.LogEntries);
    }

    [Fact]
    public void GetAllLogsAsText_ReturnsFormattedString()
    {
        _service.Log("First message");
        _service.LogError("Second message");

        var result = _service.GetAllLogsAsText();

        Assert.Contains("First message", result);
        Assert.Contains("Second message", result);
        Assert.Contains("[ERROR]", result);
    }

    [Fact]
    public void GetAllLogsAsText_EmptyCollection_ReturnsEmptyString()
    {
        var result = _service.GetAllLogsAsText();

        Assert.Equal(string.Empty, result);
    }

    [Fact]
    public void MultipleLogs_AreAddedInOrder()
    {
        _service.Log("First");
        _service.Log("Second");
        _service.Log("Third");

        Assert.Equal(3, _service.LogEntries.Count);
        Assert.Equal("First", _service.LogEntries[0].Message);
        Assert.Equal("Second", _service.LogEntries[1].Message);
        Assert.Equal("Third", _service.LogEntries[2].Message);
    }

    [Fact]
    public void DuplicateMessages_WithinShortTimeframe_AreDeduplicated()
    {
        _service.Log("Duplicate message");
        _service.Log("Duplicate message");
        _service.Log("Duplicate message");

        Assert.True(_service.LogEntries.Count < 3,
            $"Expected deduplication of at least one duplicate, but got {_service.LogEntries.Count} entries. " +
            "The 100ms dedup window may be exceeded on systems with coarse DateTime.Now resolution.");
    }

    [Fact]
    public void DifferentMessages_AreNotDeduplicated()
    {
        _service.Log("Message A");
        _service.Log("Message B");
        _service.Log("Message A");

        Assert.Equal(3, _service.LogEntries.Count);
    }

    [Fact]
    public void Log_MaxEntriesReached_RemovesOldestEntries()
    {
        for (var i = 0; i < 5010; i++)
        {
            _service.Log($"Message {i}");
        }

        Assert.True(_service.LogEntries.Count <= 5000,
            $"Expected at most 5000 entries, but got {_service.LogEntries.Count}.");
        Assert.Contains("Message 5009", _service.LogEntries[^1].Message);
    }

    [Fact]
    public void LogError_MaxEntriesReached_RemovesOldestEntries()
    {
        for (var i = 0; i < 5005; i++)
        {
            _service.LogError($"Error {i}");
        }

        Assert.True(_service.LogEntries.Count <= 5000,
            $"Expected at most 5000 entries, but got {_service.LogEntries.Count}.");
        Assert.Contains("Error 5004", _service.LogEntries[^1].Message);
    }

    public void Dispose()
    {
        _service.Clear();
        GC.SuppressFinalize(this);
    }
}
