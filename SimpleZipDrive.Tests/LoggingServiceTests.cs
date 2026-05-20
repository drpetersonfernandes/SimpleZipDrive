using SimpleZipDrive.Services;

namespace SimpleZipDrive.Tests;

public class LoggingServiceTests : IDisposable
{
    private readonly LoggingService _service = new();

    [Fact]
    public void ConstructorCreatesEmptyLogEntries()
    {
        Assert.NotNull(_service.LogEntries);
        Assert.Empty(_service.LogEntries);
    }

    [Fact]
    public void LogAddsEntryToCollection()
    {
        _service.Log("Test message");

        // LoggingService executes synchronously when Application.Current?.Dispatcher is null (test context)
        Assert.Single(_service.LogEntries);
        Assert.Equal("Test message", _service.LogEntries[0].Message);
        Assert.False(_service.LogEntries[0].IsError);
    }

    [Fact]
    public void LogErrorAddsErrorEntryToCollection()
    {
        _service.LogError("Error message");

        Assert.Single(_service.LogEntries);
        Assert.Equal("Error message", _service.LogEntries[0].Message);
        Assert.True(_service.LogEntries[0].IsError);
    }

    [Fact]
    public void LogTrimsTrailingNewlines()
    {
        _service.Log("Message with newline\r\n");

        Assert.Single(_service.LogEntries);
        Assert.Equal("Message with newline", _service.LogEntries[0].Message);
    }

    [Fact]
    public void LogErrorTrimsTrailingNewlines()
    {
        _service.LogError("Error with newline\n");

        Assert.Single(_service.LogEntries);
        Assert.Equal("Error with newline", _service.LogEntries[0].Message);
    }

    [Fact]
    public void LogNullDoesNotAddEntry()
    {
        _service.Log(null!);

        Assert.Empty(_service.LogEntries);
    }

    [Fact]
    public void LogEmptyStringAddsEmptyLineEntry()
    {
        _service.Log("");

        Assert.Single(_service.LogEntries);
        Assert.Equal(string.Empty, _service.LogEntries[0].Message);
        Assert.False(_service.LogEntries[0].IsError);
    }

    [Fact]
    public void LogErrorNullOrWhitespaceDoesNotAddEntry()
    {
        _service.LogError("");
        _service.LogError("   ");
        _service.LogError(null!);

        Assert.Empty(_service.LogEntries);
    }

    [Fact]
    public void ClearRemovesAllEntries()
    {
        _service.Log("Message 1");
        _service.Log("Message 2");

        Assert.Equal(2, _service.LogEntries.Count);

        _service.Clear();

        Assert.Empty(_service.LogEntries);
    }

    [Fact]
    public void GetAllLogsAsTextReturnsFormattedString()
    {
        _service.Log("First message");
        _service.LogError("Second message");

        var result = _service.GetAllLogsAsText();

        Assert.Contains("First message", result);
        Assert.Contains("Second message", result);
        Assert.Contains("[ERROR]", result);
    }

    [Fact]
    public void GetAllLogsAsTextEmptyCollectionReturnsEmptyString()
    {
        var result = _service.GetAllLogsAsText();

        Assert.Equal(string.Empty, result);
    }

    [Fact]
    public void MultipleLogsAreAddedInOrder()
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
    public void DuplicateMessagesWithinShortTimeframeAreDeduplicated()
    {
        _service.Log("Duplicate message");
        _service.Log("Duplicate message");
        _service.Log("Duplicate message");

        Assert.True(_service.LogEntries.Count < 3,
            $"Expected deduplication of at least one duplicate, but got {_service.LogEntries.Count} entries. " +
            "The 100ms dedup window may be exceeded on systems with coarse DateTime.Now resolution.");
    }

    [Fact]
    public void DifferentMessagesAreNotDeduplicated()
    {
        _service.Log("Message A");
        _service.Log("Message B");
        _service.Log("Message A"); // Same as first but different from immediate predecessor

        Assert.Equal(3, _service.LogEntries.Count);
    }

    public void Dispose()
    {
        _service.Clear();
        GC.SuppressFinalize(this);
    }
}
