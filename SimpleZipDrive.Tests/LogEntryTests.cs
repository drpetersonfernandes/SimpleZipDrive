using SimpleZipDrive.Core.Models;

namespace SimpleZipDrive.Tests;

public class LogEntryTests
{
    [Fact]
    public void ToStringRegularEntryFormatsCorrectly()
    {
        var entry = new LogEntry
        {
            Timestamp = new DateTime(2024, 1, 15, 10, 30, 45),
            Message = "Test message",
            IsError = false
        };

        var result = entry.ToString();

        Assert.Equal("[2024-01-15 10:30:45] Test message", result);
    }

    [Fact]
    public void ToStringErrorEntryIncludesErrorPrefix()
    {
        var entry = new LogEntry
        {
            Timestamp = new DateTime(2024, 1, 15, 14, 22, 10),
            Message = "Something went wrong",
            IsError = true
        };

        var result = entry.ToString();

        Assert.Equal("[2024-01-15 14:22:10] [ERROR] Something went wrong", result);
    }

    [Fact]
    public void ToStringEmptyMessageFormatsCorrectly()
    {
        var entry = new LogEntry
        {
            Timestamp = new DateTime(2024, 1, 15, 0, 0, 0),
            Message = "",
            IsError = false
        };

        var result = entry.ToString();

        Assert.Equal("[2024-01-15 00:00:00] ", result);
    }

    [Fact]
    public void PropertiesAreCorrectlyAssigned()
    {
        var timestamp = DateTime.Now;
        var entry = new LogEntry
        {
            Timestamp = timestamp,
            Message = "Property test",
            IsError = true
        };

        Assert.Equal(timestamp, entry.Timestamp);
        Assert.Equal("Property test", entry.Message);
        Assert.True(entry.IsError);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void IsErrorPropertyReflectsValue(bool isError)
    {
        var entry = new LogEntry
        {
            Timestamp = DateTime.Now,
            Message = "Error flag test",
            IsError = isError
        };

        Assert.Equal(isError, entry.IsError);
    }
}
