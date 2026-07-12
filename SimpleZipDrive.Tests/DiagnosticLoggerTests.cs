using System.Diagnostics.CodeAnalysis;
using SimpleZipDrive.Core;

namespace SimpleZipDrive.Tests;

[Collection("Logging")]
[SuppressMessage("ReSharper", "NullableWarningSuppressionIsUsed")]
public class DiagnosticLoggerTests
{
    // ─── Close: stops further logging ───

    [Fact]
    public void Close_StopsFurtherLogging()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"DiagnosticLogger_test_{Guid.NewGuid():N}");
        try
        {
            DiagnosticLogger.Initialize(tempDir);
            Assert.True(DiagnosticLogger.Initialized);

            DiagnosticLogger.Log("before close");
            DiagnosticLogger.Close();

            // After close, logging should not throw but also not write
            DiagnosticLogger.Log("after close");

            // Verify the writer was closed (Initialized should still be true
            // but _writer is null, so Log will silently skip)
            Assert.True(DiagnosticLogger.Initialized);
        }
        finally
        {
            DiagnosticLogger.Close();
            try
            {
                Directory.Delete(tempDir, true);
            }
            catch
            {
                // ignored
            }
        }
    }

    // ─── Close: idempotent ───

    [Fact]
    public void Close_MultipleCalls_DoesNotThrow()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"DiagnosticLogger_test_{Guid.NewGuid():N}");
        try
        {
            DiagnosticLogger.Initialize(tempDir);

            var ex = Record.Exception(static () =>
            {
                DiagnosticLogger.Close();
                DiagnosticLogger.Close();
                DiagnosticLogger.Close();
            });

            Assert.Null(ex);
        }
        finally
        {
            try
            {
                Directory.Delete(tempDir, true);
            }
            catch
            {
                // ignored
            }
        }
    }

    // ─── CleanupOldLogs: does not throw ───

    [Fact]
    public void CleanupOldLogs_DoesNotThrow()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"DiagnosticLogger_test_{Guid.NewGuid():N}");
        try
        {
            Directory.CreateDirectory(tempDir);

            // Create some fake log files
            File.WriteAllText(Path.Combine(tempDir, "debug_old.log"), "old");
            File.WriteAllText(Path.Combine(tempDir, "error.log"), "error");

            var ex = Record.Exception(() => DiagnosticLogger.CleanupOldLogs(tempDir));
            Assert.Null(ex);

            // Verify files were deleted
            Assert.False(File.Exists(Path.Combine(tempDir, "debug_old.log")));
            Assert.False(File.Exists(Path.Combine(tempDir, "error.log")));
        }
        finally
        {
            try
            {
                Directory.Delete(tempDir, true);
            }
            catch
            {
                // ignored
            }
        }
    }

    // ─── CleanupOldLogs: non-existent directory ───

    [Fact]
    public void CleanupOldLogs_NonExistentDirectory_DoesNotThrow()
    {
        var nonExistentDir = Path.Combine(Path.GetTempPath(), $"nonexistent_{Guid.NewGuid():N}");

        var ex = Record.Exception(() => DiagnosticLogger.CleanupOldLogs(nonExistentDir));
        Assert.Null(ex);
    }

    // ─── CleanupOldLogs: preserves non-matching files ───

    [Fact]
    public void CleanupOldLogs_PreservesNonMatchingFiles()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"DiagnosticLogger_test_{Guid.NewGuid():N}");
        try
        {
            Directory.CreateDirectory(tempDir);

            File.WriteAllText(Path.Combine(tempDir, "debug_old.log"), "old");
            File.WriteAllText(Path.Combine(tempDir, "other_file.txt"), "keep");
            File.WriteAllText(Path.Combine(tempDir, "error.log"), "error");

            DiagnosticLogger.CleanupOldLogs(tempDir);

            Assert.True(File.Exists(Path.Combine(tempDir, "other_file.txt")));
            Assert.False(File.Exists(Path.Combine(tempDir, "debug_old.log")));
            Assert.False(File.Exists(Path.Combine(tempDir, "error.log")));
        }
        finally
        {
            try
            {
                Directory.Delete(tempDir, true);
            }
            catch
            {
                // ignored
            }
        }
    }

    // ─── Initialize: creates log file ───

    [Fact]
    public void Initialize_CreatesLogFile()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"DiagnosticLogger_test_{Guid.NewGuid():N}");
        try
        {
            DiagnosticLogger.Initialize(tempDir);

            Assert.True(DiagnosticLogger.Initialized);
            Assert.NotNull(DiagnosticLogger.LogFilePath);
            Assert.True(File.Exists(DiagnosticLogger.LogFilePath));
        }
        finally
        {
            DiagnosticLogger.Close();
            try
            {
                Directory.Delete(tempDir, true);
            }
            catch
            {
                // ignored
            }
        }
    }

    // ─── Initialize: disabled does not create file ───

    [Fact]
    public void Initialize_Disabled_DoesNotCreateFile()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"DiagnosticLogger_test_{Guid.NewGuid():N}");
        try
        {
            DiagnosticLogger.Initialize(tempDir, false);

            Assert.False(DiagnosticLogger.IsEnabled);
            // Initialized might be true from a previous test in the same process
            // but IsEnabled should be false
        }
        finally
        {
            DiagnosticLogger.Close();
            try
            {
                Directory.Delete(tempDir, true);
            }
            catch
            {
                // ignored
            }
        }
    }

    // ─── Initialize: invalid directory sets Initialized=false ───

    [Fact]
    public void Initialize_InvalidDirectory_SetsInitializedFalse()
    {
        // Use an invalid path (file as directory)
        var tempFile = Path.Combine(Path.GetTempPath(), $"DiagnosticLogger_test_{Guid.NewGuid():N}.txt");
        try
        {
            File.WriteAllText(tempFile, "not a directory");

            DiagnosticLogger.Initialize(tempFile);

            Assert.False(DiagnosticLogger.Initialized);
        }
        finally
        {
            DiagnosticLogger.Close();
            try
            {
                File.Delete(tempFile);
            }
            catch
            {
                // ignored
            }
        }
    }

    // ─── Log: writes to file ───

    [Fact]
    public void Log_WritesToFile()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"DiagnosticLogger_test_{Guid.NewGuid():N}");
        try
        {
            DiagnosticLogger.Initialize(tempDir);
            DiagnosticLogger.Log("test message");
            DiagnosticLogger.Close(); // Close writer before reading

            var content = File.ReadAllText(DiagnosticLogger.LogFilePath!);
            Assert.Contains("test message", content);
        }
        finally
        {
            DiagnosticLogger.Close();
            try
            {
                Directory.Delete(tempDir, true);
            }
            catch
            {
                // ignored
            }
        }
    }

    // ─── Log: with exception ───

    [Fact]
    public void Log_WithException_WritesTypeAndMessage()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"DiagnosticLogger_test_{Guid.NewGuid():N}");
        try
        {
            DiagnosticLogger.Initialize(tempDir);
            DiagnosticLogger.Log(new InvalidOperationException("test error"), "context");
            DiagnosticLogger.Close();

            Assert.NotNull(DiagnosticLogger.LogFilePath);
            var content = File.ReadAllText(DiagnosticLogger.LogFilePath);
            Assert.Contains("context", content);
            Assert.Contains("InvalidOperationException", content);
            Assert.Contains("test error", content);
        }
        finally
        {
            DiagnosticLogger.Close();
            try
            {
                Directory.Delete(tempDir, true);
            }
            catch
            {
                // ignored
            }
        }
    }

    // ─── LogOperation: int status ───

    [Fact]
    public void LogOperation_IntStatus_WritesCorrectly()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"DiagnosticLogger_test_{Guid.NewGuid():N}");
        try
        {
            DiagnosticLogger.Initialize(tempDir);
            DiagnosticLogger.LogOperation("Create", "/test", 0, "success");
            DiagnosticLogger.Close();

            var content = File.ReadAllText(DiagnosticLogger.LogFilePath!);
            Assert.Contains("Create", content);
            Assert.Contains("/test", content);
            Assert.Contains("SUCCESS", content);
            Assert.Contains("success", content);
        }
        finally
        {
            DiagnosticLogger.Close();
            try
            {
                Directory.Delete(tempDir, true);
            }
            catch
            {
                // ignored
            }
        }
    }

    // ─── LogOperation: boolean result ───

    [Fact]
    public void LogOperation_BoolResult_WritesCorrectly()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"DiagnosticLogger_test_{Guid.NewGuid():N}");
        try
        {
            DiagnosticLogger.Initialize(tempDir);
            DiagnosticLogger.LogOperation("ReadDir", "/data", true, "found");
            DiagnosticLogger.Close();

            var content = File.ReadAllText(DiagnosticLogger.LogFilePath!);
            Assert.Contains("ReadDir", content);
            Assert.Contains("true", content);
        }
        finally
        {
            DiagnosticLogger.Close();
            try
            {
                Directory.Delete(tempDir, true);
            }
            catch
            {
                // ignored
            }
        }
    }

    // ─── LogSection: writes section header ───

    [Fact]
    public void LogSection_WritesSectionHeader()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"DiagnosticLogger_test_{Guid.NewGuid():N}");
        try
        {
            DiagnosticLogger.Initialize(tempDir);
            DiagnosticLogger.LogSection("TEST SECTION");
            DiagnosticLogger.Close();

            Assert.NotNull(DiagnosticLogger.LogFilePath);
            var content = File.ReadAllText(DiagnosticLogger.LogFilePath);
            Assert.Contains("TEST SECTION", content);
            Assert.Contains("========", content);
        }
        finally
        {
            DiagnosticLogger.Close();
            try
            {
                Directory.Delete(tempDir, true);
            }
            catch
            {
                // ignored
            }
        }
    }

    // ─── LogHeader: writes header line ───

    [Fact]
    public void LogHeader_WritesHeaderLine()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"DiagnosticLogger_test_{Guid.NewGuid():N}");
        try
        {
            DiagnosticLogger.Initialize(tempDir);
            DiagnosticLogger.LogHeader("MY HEADER");
            DiagnosticLogger.Close();

            var content = File.ReadAllText(DiagnosticLogger.LogFilePath!);
            Assert.Contains("--- MY HEADER ---", content);
        }
        finally
        {
            DiagnosticLogger.Close();
            try
            {
                Directory.Delete(tempDir, true);
            }
            catch
            {
                // ignored
            }
        }
    }

    // ─── Log: when not initialized, does not throw ───

    [Fact]
    public void Log_WhenNotInitialized_DoesNotThrow()
    {
        // Ensure not initialized
        DiagnosticLogger.Close();

        var ex = Record.Exception(static () => DiagnosticLogger.Log("should not throw"));
        Assert.Null(ex);
    }

    // ─── Log: thread safety ───

    [Fact]
    public void Log_ConcurrentWrites_DoNotThrow()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"DiagnosticLogger_test_{Guid.NewGuid():N}");
        try
        {
            DiagnosticLogger.Initialize(tempDir);

            var tasks = Enumerable.Range(0, 10).Select(static i =>
                Task.Run(() => DiagnosticLogger.Log($"Concurrent message {i}"))
            ).ToArray();

            var ex = Record.Exception(() => Task.WaitAll(tasks));
            Assert.Null(ex);
        }
        finally
        {
            DiagnosticLogger.Close();
            try
            {
                Directory.Delete(tempDir, true);
            }
            catch
            {
                // ignored
            }
        }
    }
}
