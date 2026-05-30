using DokanNet.Logging;
using SimpleZipDrive.Services;

namespace SimpleZipDrive.Tests;

public class DokanPrefixedLoggerTests : IDisposable
{
    private readonly StringWriter _consoleOutCapture;
    private readonly TextWriter _originalOut;
    private readonly ILogger? _logger;

    public DokanPrefixedLoggerTests()
    {
        _originalOut = Console.Out;
        _consoleOutCapture = new StringWriter();
        Console.SetOut(_consoleOutCapture);

        _logger = new DokanPrefixedLogger("[DRIVE] ");
    }

    [Fact]
    public void DebugEnabled_IsTrue()
    {
        if (_logger == null) return;

        Assert.True(_logger.DebugEnabled);
    }

    [Fact]
    public void Debug_WritesFormattedMessage()
    {
        if (_logger == null) return;

        _logger.Debug("test message");
        Assert.Contains("[DRIVE] test message", _consoleOutCapture.ToString());
    }

    [Fact]
    public void Info_WritesFormattedMessage()
    {
        if (_logger == null) return;

        _logger.Info("info message");
        Assert.Contains("[DRIVE] info message", _consoleOutCapture.ToString());
    }

    [Fact]
    public void Warn_WritesFormattedMessage()
    {
        if (_logger == null) return;

        _logger.Warn("warning!");
        Assert.Contains("[DRIVE] warning!", _consoleOutCapture.ToString());
    }

    [Fact]
    public void Error_WritesFormattedMessage()
    {
        if (_logger == null) return;

        _logger.Error("error occurred");
        Assert.Contains("[DRIVE] error occurred", _consoleOutCapture.ToString());
    }

    [Fact]
    public void Fatal_WritesFormattedMessage()
    {
        if (_logger == null) return;

        _logger.Fatal("fatal crash");
        Assert.Contains("[DRIVE] fatal crash", _consoleOutCapture.ToString());
    }

    [Fact]
    public void AllLogMethods_FormatWithArgs()
    {
        if (_logger == null) return;

        _logger.Debug("value is {0}", 42);
        Assert.Contains("[DRIVE] value is 42", _consoleOutCapture.ToString());
    }

    [Fact]
    public void AllLogMethods_MultipleArgs()
    {
        if (_logger == null) return;

        _logger.Info("{0} {1} {2}", "a", "b", "c");
        Assert.Contains("[DRIVE] a b c", _consoleOutCapture.ToString());
    }

    [Fact]
    public void AllLogMethods_NoArgs_NoFormatting()
    {
        if (_logger == null) return;

        _logger.Error("{0} not formatted");
        Assert.Contains("[DRIVE] {0} not formatted", _consoleOutCapture.ToString());
    }

    [Fact]
    public void MultipleCalls_AllWritten()
    {
        if (_logger == null) return;

        _logger.Debug("first");
        _logger.Info("second");
        _logger.Warn("third");

        var output = _consoleOutCapture.ToString();
        Assert.Contains("[DRIVE] first", output);
        Assert.Contains("[DRIVE] second", output);
        Assert.Contains("[DRIVE] third", output);
    }

    [Fact]
    public void Dispose_DoesNotThrow()
    {
        if (_logger == null) return;

        var exception = Record.Exception(() => ((IDisposable)_logger).Dispose());
        Assert.Null(exception);
    }

    [Fact]
    public void CustomPrefix_IsApplied()
    {
        using var capture = new StringWriter();
        Console.SetOut(capture);

        var logger = new DokanPrefixedLogger("[Custom] ");

        logger.Info("hello");
        Assert.Contains("[Custom] hello", capture.ToString());
    }

    public void Dispose()
    {
        Console.SetOut(_originalOut);
        _consoleOutCapture.Dispose();
        GC.SuppressFinalize(this);
    }
}
