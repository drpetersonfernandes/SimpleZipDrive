using System.Diagnostics.CodeAnalysis;
using SimpleZipDrive.Core;

namespace SimpleZipDrive.Tests.WinFsp;

[Collection("Logging")]
[SuppressMessage("ReSharper", "NullableWarningSuppressionIsUsed")]
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
        DiagnosticLogger.Initialized = false;
        DiagnosticLogger.LogFilePath = null;
        DiagnosticLogger.IsEnabled = true;
    }

    private static string ReadFileText(string path)
    {
        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        using var reader = new StreamReader(fs);
        return reader.ReadToEnd();
    }

    [Fact]
    public void IsEnabled_DefaultsToTrue()
    {
        ResetState();
        Assert.True(DiagnosticLogger.IsEnabled);
    }

    [Fact]
    public void LogFilePath_DefaultsToNull()
    {
        ResetState();
        Assert.Null(DiagnosticLogger.LogFilePath);
    }

    [Fact]
    public void Initialize_SetsLogFilePath()
    {
        ResetState();
        DiagnosticLogger.Initialize(_tempDir);

        Assert.NotNull(DiagnosticLogger.LogFilePath);
        Assert.StartsWith(_tempDir, DiagnosticLogger.LogFilePath);
        Assert.EndsWith(".log", DiagnosticLogger.LogFilePath);
    }

    [Fact]
    public void Initialize_Disabled_SetsIsEnabledFalse()
    {
        ResetState();
        DiagnosticLogger.Initialize(_tempDir, false);

        Assert.False(DiagnosticLogger.IsEnabled);
        Assert.Null(DiagnosticLogger.LogFilePath);
    }

    [Fact]
    public void Initialize_CreatesDirectoryIfNotExists()
    {
        ResetState();
        var newDir = Path.Combine(_tempDir, "subdir");
        DiagnosticLogger.Initialize(newDir);

        Assert.True(Directory.Exists(newDir));
    }

    [Fact]
    public void Log_WritesToFile()
    {
        ResetState();
        DiagnosticLogger.Initialize(_tempDir);

        DiagnosticLogger.Log("Test log message");

        Assert.NotNull(DiagnosticLogger.LogFilePath);
        Assert.True(File.Exists(DiagnosticLogger.LogFilePath));
        var content = ReadFileText(DiagnosticLogger.LogFilePath);
        Assert.Contains("Test log message", content);
    }

    [Fact]
    public void Log_IncludesTimestamp()
    {
        ResetState();
        DiagnosticLogger.Initialize(_tempDir);

        DiagnosticLogger.Log("Timestamped message");

        var content = ReadFileText(DiagnosticLogger.LogFilePath!);
        Assert.Matches(@"\[\d{2}:\d{2}:\d{2}\.\d{3}\]", content);
    }

    [Fact]
    public void Log_IncludesThreadId()
    {
        ResetState();
        DiagnosticLogger.Initialize(_tempDir);

        DiagnosticLogger.Log("Thread message");

        var content = ReadFileText(DiagnosticLogger.LogFilePath!);
        Assert.Contains($"T{Environment.CurrentManagedThreadId}", content);
    }

    [Fact]
    public void Log_WhenNotInitialized_DoesNotThrow()
    {
        ResetState();

        var ex = Record.Exception(static () => DiagnosticLogger.Log("Should not crash"));

        Assert.Null(ex);
    }

    [Fact]
    public void Log_WithException_IncludesTypeAndMessage()
    {
        ResetState();
        DiagnosticLogger.Initialize(_tempDir);

        var testEx = new InvalidOperationException("test error details");
        DiagnosticLogger.Log(testEx, "test context");

        var content = ReadFileText(DiagnosticLogger.LogFilePath!);
        Assert.Contains("test context", content);
        Assert.Contains("InvalidOperationException", content);
        Assert.Contains("test error details", content);
    }

    [Fact]
    public void Log_WithException_IncludesStackTrace()
    {
        ResetState();
        DiagnosticLogger.Initialize(_tempDir);

        try
        {
            throw new ArgumentException("stack trace test");
        }
        catch (Exception ex)
        {
            DiagnosticLogger.Log(ex, "stack trace context");
        }

        var content = ReadFileText(DiagnosticLogger.LogFilePath!);
        Assert.Contains("Stack:", content);
    }

    [Fact]
    public void LogOperation_IntStatus_FormatsCorrectly()
    {
        ResetState();
        DiagnosticLogger.Initialize(_tempDir);

        DiagnosticLogger.LogOperation("Read", "test.txt", 0, "detail");

        var content = ReadFileText(DiagnosticLogger.LogFilePath!);
        Assert.Contains("Read", content);
        Assert.Contains("test.txt", content);
        Assert.Contains("SUCCESS", content);
        Assert.Contains("[detail]", content);
    }

    [Fact]
    public void LogOperation_IntStatus_NonZero_FormatsAsHex()
    {
        ResetState();
        DiagnosticLogger.Initialize(_tempDir);

        DiagnosticLogger.LogOperation("Write", "bad.txt", unchecked((int)0xC0000022));

        var content = ReadFileText(DiagnosticLogger.LogFilePath!);
        Assert.Contains("0xC0000022", content);
    }

    [Fact]
    public void LogOperation_BoolStatus_TrueFormatsAsTrue()
    {
        ResetState();
        DiagnosticLogger.Initialize(_tempDir);

        DiagnosticLogger.LogOperation("Check", "file.txt", true);

        var content = ReadFileText(DiagnosticLogger.LogFilePath!);
        Assert.Contains("true", content);
    }

    [Fact]
    public void LogOperation_BoolStatus_FalseFormatsAsFalse()
    {
        ResetState();
        DiagnosticLogger.Initialize(_tempDir);

        DiagnosticLogger.LogOperation("Check", "file.txt", false);

        var content = ReadFileText(DiagnosticLogger.LogFilePath!);
        Assert.Contains("false", content);
    }

    [Fact]
    public void LogOperation_NullDetail_OmitsBrackets()
    {
        ResetState();
        DiagnosticLogger.Initialize(_tempDir);

        DiagnosticLogger.LogOperation("Read", "file.txt", 0);

        var content = ReadFileText(DiagnosticLogger.LogFilePath!);
        Assert.DoesNotContain("[]", content);
    }

    [Fact]
    public void LogSection_WritesSectionHeader()
    {
        ResetState();
        DiagnosticLogger.Initialize(_tempDir);

        DiagnosticLogger.LogSection("TEST SECTION");

        var content = ReadFileText(DiagnosticLogger.LogFilePath!);
        Assert.Contains("TEST SECTION", content);
        Assert.Contains(new string('=', 80), content);
    }

    [Fact]
    public void LogHeader_WritesDashedFormat()
    {
        ResetState();
        DiagnosticLogger.Initialize(_tempDir);

        DiagnosticLogger.LogHeader("My Header");

        var content = ReadFileText(DiagnosticLogger.LogFilePath!);
        Assert.Contains("--- My Header ---", content);
    }

    [Fact]
    public void Log_MultipleCalls_AppendsToFile()
    {
        ResetState();
        DiagnosticLogger.Initialize(_tempDir);

        DiagnosticLogger.Log("First line");
        DiagnosticLogger.Log("Second line");

        var content = ReadFileText(DiagnosticLogger.LogFilePath!);
        Assert.Contains("First line", content);
        Assert.Contains("Second line", content);
    }

    [Fact]
    public void LogOperation_NullDetail_DoesNotIncludeBrackets()
    {
        ResetState();
        DiagnosticLogger.Initialize(_tempDir);

        DiagnosticLogger.LogOperation("Test", "path", 0);

        var content = ReadFileText(DiagnosticLogger.LogFilePath!);
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
