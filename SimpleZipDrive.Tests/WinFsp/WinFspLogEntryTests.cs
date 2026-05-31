using WinFspLogEntry = SimpleZipDrive.Core.Models.LogEntry;

namespace SimpleZipDrive.Tests.WinFsp;

public class WinFspLogEntryTests
{
    [Fact]
    public void ToString_RegularEntry_FormatsCorrectly()
    {
        var entry = new WinFspLogEntry
        {
            Timestamp = new DateTime(2024, 1, 15, 10, 30, 45),
            Message = "Test message",
            IsError = false
        };

        var result = entry.ToString();

        Assert.Equal("Test message", result);
    }

    [Fact]
    public void ToString_ErrorEntry_IncludesErrorPrefix()
    {
        var entry = new WinFspLogEntry
        {
            Timestamp = new DateTime(2024, 1, 15, 14, 22, 10),
            Message = "Something went wrong",
            IsError = true
        };

        var result = entry.ToString();

        Assert.Equal("[ERROR] Something went wrong", result);
    }

    [Fact]
    public void ToString_EmptyMessage_FormatsCorrectly()
    {
        var entry = new WinFspLogEntry
        {
            Timestamp = new DateTime(2024, 1, 15, 0, 0, 0),
            Message = "",
            IsError = false
        };

        var result = entry.ToString();

        Assert.Equal("", result);
    }

    [Fact]
    public void Properties_AreCorrectlyAssigned()
    {
        var timestamp = DateTime.Now;
        var entry = new WinFspLogEntry
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
    public void IsErrorProperty_ReflectsValue(bool isError)
    {
        var entry = new WinFspLogEntry
        {
            Timestamp = DateTime.Now,
            Message = "Error flag test",
            IsError = isError
        };

        Assert.Equal(isError, entry.IsError);
    }
}
