using SimpleZipDrive_WinFsp;

namespace SimpleZipDrive.Tests.WinFsp;

public class WinFspDiagnosticLoggerTests : IDisposable
{
    private readonly string _tempDir;

    public WinFspDiagnosticLoggerTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"DiagLoggerTests_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempDir);
    }

    private static void ResetState()
    {
        global::SimpleZipDrive_WinFsp.DiagnosticLogger._initialized = false;
        global::SimpleZipDrive_WinFsp.DiagnosticLogger.LogFilePath = null;
        global::SimpleZipDrive_WinFsp.DiagnosticLogger.IsEnabled = true;
    }

    [Fact]
    public void IsEnabled_DefaultsToTrue()
    {
        ResetState();
        Assert.True(global::SimpleZipDrive_WinFsp.DiagnosticLogger.IsEnabled);
    }

    [Fact]
    public void LogFilePath_DefaultsToNull()
    {
        ResetState();
        Assert.Null(global::SimpleZipDrive_WinFsp.DiagnosticLogger.LogFilePath);
    }

    [Fact]
    public void Initialize_SetsLogFilePath()
    {
        ResetState();
        global::SimpleZipDrive_WinFsp.DiagnosticLogger.Initialize(_tempDir);

        Assert.NotNull(global::SimpleZipDrive_WinFsp.DiagnosticLogger.LogFilePath);
        Assert.StartsWith(_tempDir, global::SimpleZipDrive_WinFsp.DiagnosticLogger.LogFilePath);
        Assert.EndsWith(".log", global::SimpleZipDrive_WinFsp.DiagnosticLogger.LogFilePath);
    }

    [Fact]
    public void Initialize_Disabled_SetsIsEnabledFalse()
    {
        ResetState();
        global::SimpleZipDrive_WinFsp.DiagnosticLogger.Initialize(_tempDir, enabled: false);

        Assert.False(global::SimpleZipDrive_WinFsp.DiagnosticLogger.IsEnabled);
        Assert.Null(global::SimpleZipDrive_WinFsp.DiagnosticLogger.LogFilePath);
    }

    [Fact]
    public void Initialize_CreatesDirectoryIfNotExists()
    {
        ResetState();
        var newDir = Path.Combine(_tempDir, "subdir");
        global::SimpleZipDrive_WinFsp.DiagnosticLogger.Initialize(newDir);

        Assert.True(Directory.Exists(newDir));
    }

    [Fact]
    public void Log_WritesToFile()
    {
        ResetState();
        global::SimpleZipDrive_WinFsp.DiagnosticLogger.Initialize(_tempDir);

        global::SimpleZipDrive_WinFsp.DiagnosticLogger.Log("Test log message");

        Assert.NotNull(global::SimpleZipDrive_WinFsp.DiagnosticLogger.LogFilePath);
        Assert.True(File.Exists(global::SimpleZipDrive_WinFsp.DiagnosticLogger.LogFilePath));
        var content = File.ReadAllText(global::SimpleZipDrive_WinFsp.DiagnosticLogger.LogFilePath);
        Assert.Contains("Test log message", content);
    }

    [Fact]
    public void Log_IncludesTimestamp()
    {
        ResetState();
        global::SimpleZipDrive_WinFsp.DiagnosticLogger.Initialize(_tempDir);

        global::SimpleZipDrive_WinFsp.DiagnosticLogger.Log("Timestamped message");

        var content = File.ReadAllText(global::SimpleZipDrive_WinFsp.DiagnosticLogger.LogFilePath!);
        Assert.Matches(@"\[\d{2}:\d{2}:\d{2}\.\d{3}\]", content);
    }

    [Fact]
    public void Log_IncludesThreadId()
    {
        ResetState();
        global::SimpleZipDrive_WinFsp.DiagnosticLogger.Initialize(_tempDir);

        global::SimpleZipDrive_WinFsp.DiagnosticLogger.Log("Thread message");

        var content = File.ReadAllText(global::SimpleZipDrive_WinFsp.DiagnosticLogger.LogFilePath!);
        Assert.Contains($"T{Environment.CurrentManagedThreadId}", content);
    }

    [Fact]
    public void Log_WhenNotInitialized_DoesNotThrow()
    {
        ResetState();

        var ex = Record.Exception(() => global::SimpleZipDrive_WinFsp.DiagnosticLogger.Log("Should not crash"));

        Assert.Null(ex);
    }

    [Fact]
    public void Log_WithException_IncludesTypeAndMessage()
    {
        ResetState();
        global::SimpleZipDrive_WinFsp.DiagnosticLogger.Initialize(_tempDir);

        var testEx = new InvalidOperationException("test error details");
        global::SimpleZipDrive_WinFsp.DiagnosticLogger.Log(testEx, "test context");

        var content = File.ReadAllText(global::SimpleZipDrive_WinFsp.DiagnosticLogger.LogFilePath!);
        Assert.Contains("test context", content);
        Assert.Contains("InvalidOperationException", content);
        Assert.Contains("test error details", content);
    }

    [Fact]
    public void Log_WithException_IncludesStackTrace()
    {
        ResetState();
        global::SimpleZipDrive_WinFsp.DiagnosticLogger.Initialize(_tempDir);

        try
        {
            throw new ArgumentException("stack trace test");
        }
        catch (Exception ex)
        {
            global::SimpleZipDrive_WinFsp.DiagnosticLogger.Log(ex, "stack trace context");
        }

        var content = File.ReadAllText(global::SimpleZipDrive_WinFsp.DiagnosticLogger.LogFilePath!);
        Assert.Contains("Stack:", content);
    }

    [Fact]
    public void LogOperation_IntStatus_FormatsCorrectly()
    {
        ResetState();
        global::SimpleZipDrive_WinFsp.DiagnosticLogger.Initialize(_tempDir);

        global::SimpleZipDrive_WinFsp.DiagnosticLogger.LogOperation("Read", "test.txt", 0, "detail");

        var content = File.ReadAllText(global::SimpleZipDrive_WinFsp.DiagnosticLogger.LogFilePath!);
        Assert.Contains("Read", content);
        Assert.Contains("test.txt", content);
        Assert.Contains("SUCCESS", content);
        Assert.Contains("[detail]", content);
    }

    [Fact]
    public void LogOperation_IntStatus_NonZero_FormatsAsHex()
    {
        ResetState();
        global::SimpleZipDrive_WinFsp.DiagnosticLogger.Initialize(_tempDir);

        global::SimpleZipDrive_WinFsp.DiagnosticLogger.LogOperation("Write", "bad.txt", unchecked((int)0xC0000022));

        var content = File.ReadAllText(global::SimpleZipDrive_WinFsp.DiagnosticLogger.LogFilePath!);
        Assert.Contains("0xC0000022", content);
    }

    [Fact]
    public void LogOperation_BoolStatus_TrueFormatsAsTrue()
    {
        ResetState();
        global::SimpleZipDrive_WinFsp.DiagnosticLogger.Initialize(_tempDir);

        global::SimpleZipDrive_WinFsp.DiagnosticLogger.LogOperation("Check", "file.txt", true);

        var content = File.ReadAllText(global::SimpleZipDrive_WinFsp.DiagnosticLogger.LogFilePath!);
        Assert.Contains("true", content);
    }

    [Fact]
    public void LogOperation_BoolStatus_FalseFormatsAsFalse()
    {
        ResetState();
        global::SimpleZipDrive_WinFsp.DiagnosticLogger.Initialize(_tempDir);

        global::SimpleZipDrive_WinFsp.DiagnosticLogger.LogOperation("Check", "file.txt", false);

        var content = File.ReadAllText(global::SimpleZipDrive_WinFsp.DiagnosticLogger.LogFilePath!);
        Assert.Contains("false", content);
    }

    [Fact]
    public void LogOperation_NullDetail_OmitsBrackets()
    {
        ResetState();
        global::SimpleZipDrive_WinFsp.DiagnosticLogger.Initialize(_tempDir);

        global::SimpleZipDrive_WinFsp.DiagnosticLogger.LogOperation("Read", "file.txt", 0);

        var content = File.ReadAllText(global::SimpleZipDrive_WinFsp.DiagnosticLogger.LogFilePath!);
        Assert.DoesNotContain("[]", content);
    }

    [Fact]
    public void LogSection_WritesSectionHeader()
    {
        ResetState();
        global::SimpleZipDrive_WinFsp.DiagnosticLogger.Initialize(_tempDir);

        global::SimpleZipDrive_WinFsp.DiagnosticLogger.LogSection("TEST SECTION");

        var content = File.ReadAllText(global::SimpleZipDrive_WinFsp.DiagnosticLogger.LogFilePath!);
        Assert.Contains("TEST SECTION", content);
        Assert.Contains(new string('=', 80), content);
    }

    [Fact]
    public void LogHeader_WritesDashedFormat()
    {
        ResetState();
        global::SimpleZipDrive_WinFsp.DiagnosticLogger.Initialize(_tempDir);

        global::SimpleZipDrive_WinFsp.DiagnosticLogger.LogHeader("My Header");

        var content = File.ReadAllText(global::SimpleZipDrive_WinFsp.DiagnosticLogger.LogFilePath!);
        Assert.Contains("--- My Header ---", content);
    }

    [Fact]
    public void Log_MultipleCalls_AppendsToFile()
    {
        ResetState();
        global::SimpleZipDrive_WinFsp.DiagnosticLogger.Initialize(_tempDir);

        global::SimpleZipDrive_WinFsp.DiagnosticLogger.Log("First line");
        global::SimpleZipDrive_WinFsp.DiagnosticLogger.Log("Second line");

        var content = File.ReadAllText(global::SimpleZipDrive_WinFsp.DiagnosticLogger.LogFilePath!);
        Assert.Contains("First line", content);
        Assert.Contains("Second line", content);
    }

    [Fact]
    public void LogOperation_NullDetail_DoesNotIncludeBrackets()
    {
        ResetState();
        global::SimpleZipDrive_WinFsp.DiagnosticLogger.Initialize(_tempDir);

        global::SimpleZipDrive_WinFsp.DiagnosticLogger.LogOperation("Test", "path", 0, null);

        var content = File.ReadAllText(global::SimpleZipDrive_WinFsp.DiagnosticLogger.LogFilePath!);
        Assert.DoesNotContain("[null]", content);
        Assert.DoesNotContain("[]", content);
    }

    public void Dispose()
    {
        ResetState();
        try
        {
            if (Directory.Exists(_tempDir))
                Directory.Delete(_tempDir, true);
        }
        catch
        {
            // Best effort cleanup
        }

        GC.SuppressFinalize(this);
    }
}
