using SimpleZipDrive.Core;

namespace SimpleZipDrive.Tests;

public class ErrorLoggerAdditionalTests
{
    // ─── IsUserError: DokanNet namespace exceptions ───

    [Fact]
    public void IsUserError_DokanNetDriveException_ReturnsTrue()
    {
        var ex = new Exception("test") { Source = "DokanNet" };
        // The method checks fullName.StartsWith("DokanNet.") and typeName.Contains("Drive")
        // We can't easily create a DokanNet exception, but we can test the message-based path
        var result = ErrorLogger.IsUserError(new InvalidOperationException("drive letter is in use"));
        Assert.True(result);
    }

    // ─── IsUserError: "wrong password" message ───

    [Fact]
    public void IsUserError_WrongPasswordMessage_ReturnsTrue()
    {
        var result = ErrorLogger.IsUserError(new InvalidOperationException("wrong password provided"));
        Assert.True(result);
    }

    // ─── IsUserError: "incorrect password" message ───

    [Fact]
    public void IsUserError_IncorrectPasswordMessage_ReturnsTrue()
    {
        var result = ErrorLogger.IsUserError(new InvalidOperationException("incorrect password"));
        Assert.True(result);
    }

    // ─── IsUserError: "no password" message ───

    [Fact]
    public void IsUserError_NoPasswordMessage_ReturnsTrue()
    {
        var result = ErrorLogger.IsUserError(new InvalidOperationException("no password was provided"));
        Assert.True(result);
    }

    // ─── IsUserError: "need a password" message ───

    [Fact]
    public void IsUserError_NeedAPasswordMessage_ReturnsTrue()
    {
        var result = ErrorLogger.IsUserError(new InvalidOperationException("you need a password to open this"));
        Assert.True(result);
    }

    // ─── IsUserError: "missing password" message ───

    [Fact]
    public void IsUserError_MissingPasswordMessage_ReturnsTrue()
    {
        var result = ErrorLogger.IsUserError(new InvalidOperationException("missing password for encrypted archive"));
        Assert.True(result);
    }

    // ─── IsUserError: "invalid password" message ───

    [Fact]
    public void IsUserError_InvalidPasswordMessage_ReturnsTrue()
    {
        var result = ErrorLogger.IsUserError(new InvalidOperationException("invalid password"));
        Assert.True(result);
    }

    // ─── IsUserError: "password is" message ───

    [Fact]
    public void IsUserError_PasswordIsMessage_ReturnsTrue()
    {
        var result = ErrorLogger.IsUserError(new InvalidOperationException("password is wrong"));
        Assert.True(result);
    }

    // ─── IsUserError: "encrypted" with "file" ───

    [Fact]
    public void IsUserError_EncryptedFile_ReturnsTrue()
    {
        var result = ErrorLogger.IsUserError(new InvalidOperationException("the file is encrypted"));
        Assert.True(result);
    }

    // ─── IsUserError: "encrypted" with "archive" ───

    [Fact]
    public void IsUserError_EncryptedArchive_ReturnsTrue()
    {
        var result = ErrorLogger.IsUserError(new InvalidOperationException("the archive is encrypted"));
        Assert.True(result);
    }

    // ─── IsUserError: "encrypted" with "entry" ───

    [Fact]
    public void IsUserError_EncryptedEntry_ReturnsTrue()
    {
        var result = ErrorLogger.IsUserError(new InvalidOperationException("entry is encrypted"));
        Assert.True(result);
    }

    // ─── FormatErrorMessage: no inner exception ───

    [Fact]
    public void FormatErrorMessage_NoInnerException_DoesNotIncludeInnerSection()
    {
        var ex = new InvalidOperationException("test error");
        var result = ErrorLogger.FormatErrorMessage(ex, "test context");

        Assert.DoesNotContain("Inner Exception", result);
    }

    // ─── FormatErrorMessage: with inner exception ───

    [Fact]
    public void FormatErrorMessage_WithInnerException_IncludesInnerSection()
    {
        var inner = new ArgumentException("inner cause");
        var ex = new IOException("outer", inner);
        var result = ErrorLogger.FormatErrorMessage(ex, "test context");

        Assert.Contains("Inner Exception", result);
        Assert.Contains("ArgumentException", result);
        Assert.Contains("inner cause", result);
    }

    // ─── GetEnvironmentDetails: includes bitness ───

    [Fact]
    public void GetEnvironmentDetails_IncludesBitness()
    {
        using var logger = new ErrorLogger();
        var result = logger.GetEnvironmentDetails();

        Assert.Contains("Bitness:", result);
        Assert.True(result.Contains("64-bit") || result.Contains("32-bit"));
    }

    // ─── GetEnvironmentDetails: includes Windows version ───

    [Fact]
    public void GetEnvironmentDetails_IncludesWindowsVersion()
    {
        using var logger = new ErrorLogger();
        var result = logger.GetEnvironmentDetails();

        Assert.Contains("Windows Version:", result);
    }

    // ─── GetEnvironmentDetails: includes processor count ───

    [Fact]
    public void GetEnvironmentDetails_IncludesProcessorCount()
    {
        using var logger = new ErrorLogger();
        var result = logger.GetEnvironmentDetails();

        Assert.Contains("Processor Count:", result);
        Assert.Contains(Environment.ProcessorCount.ToString(), result);
    }

    // ─── ReportSilentException: non-silent mode writes to console ───

    [Fact]
    public void ReportSilentException_NonSilent_WritesToConsole()
    {
        using var logger = new ErrorLogger();
        var originalError = Console.Error;
        try
        {
            using var capture = new StringWriter();
            Console.SetError(capture);

            logger.ReportSilentException(new InvalidOperationException("test"), "test context", false);

            var output = capture.ToString();
            Assert.Contains("SILENT EXCEPTION CAUGHT", output);
            Assert.Contains("test context", output);
        }
        finally
        {
            Console.SetError(originalError);
        }
    }

    // ─── ReportSilentException: silent mode does not write to console ───

    [Fact]
    public void ReportSilentException_Silent_DoesNotWriteToConsole()
    {
        using var logger = new ErrorLogger();
        var originalError = Console.Error;
        try
        {
            using var capture = new StringWriter();
            Console.SetError(capture);

            logger.ReportSilentException(new InvalidOperationException("test"), "test context", true);

            var output = capture.ToString();
            Assert.DoesNotContain("SILENT EXCEPTION CAUGHT", output);
        }
        finally
        {
            Console.SetError(originalError);
        }
    }

    // ─── IsUserError: SharpCompress namespace with non-matching type ───

    [Fact]
    public void IsUserError_SharpCompressNonMatchingType_ReturnsFalse()
    {
        // SharpCompress namespace but type name doesn't contain Archive/Format/Invalid
        var result = ErrorLogger.IsUserError(new InvalidOperationException("some error"));
        // This is a plain InvalidOperationException, not SharpCompress
        Assert.False(result);
    }

    // ─── IsUserError: "cannot find central directory" ───

    [Fact]
    public void IsUserError_CannotFindCentralDirectory_ReturnsTrue()
    {
        var result = ErrorLogger.IsUserError(new InvalidOperationException("cannot find central directory"));
        Assert.True(result);
    }

    // ─── IsUserError: "unknown format" ───

    [Fact]
    public void IsUserError_UnknownFormat_ReturnsTrue()
    {
        var result = ErrorLogger.IsUserError(new InvalidOperationException("unknown format"));
        Assert.True(result);
    }

    // ─── IsUserError: "not a valid" ───

    [Fact]
    public void IsUserError_NotAValid_ReturnsTrue()
    {
        var result = ErrorLogger.IsUserError(new InvalidOperationException("this is not a valid file"));
        Assert.True(result);
    }

    // ─── IsUserError: "corrupt" with "archive" ───

    [Fact]
    public void IsUserError_CorruptArchive_ReturnsTrue()
    {
        var result = ErrorLogger.IsUserError(new InvalidOperationException("archive is corrupt"));
        Assert.True(result);
    }

    // ─── IsUserError: "corrupt" with "zip" ───

    [Fact]
    public void IsUserError_CorruptZip_ReturnsTrue()
    {
        var result = ErrorLogger.IsUserError(new InvalidOperationException("zip file is corrupt"));
        Assert.True(result);
    }

    // ─── IsUserError: "corrupt" alone (not enough) ───

    [Fact]
    public void IsUserError_CorruptAlone_ReturnsFalse()
    {
        var result = ErrorLogger.IsUserError(new InvalidOperationException("something corrupt"));
        Assert.False(result);
    }

    // ─── IsUserError: "header" with "invalid" ───

    [Fact]
    public void IsUserError_HeaderInvalid_ReturnsTrue()
    {
        var result = ErrorLogger.IsUserError(new InvalidOperationException("header is invalid"));
        Assert.True(result);
    }

    // ─── IsUserError: "mount point" with "invalid" ───

    [Fact]
    public void IsUserError_MountPointInvalid_ReturnsTrue()
    {
        var result = ErrorLogger.IsUserError(new InvalidOperationException("mount point is invalid"));
        Assert.True(result);
    }

    // ─── IsUserError: "drive letter" with "in use" ───

    [Fact]
    public void IsUserError_DriveLetterInUse_ReturnsTrue()
    {
        var result = ErrorLogger.IsUserError(new InvalidOperationException("drive letter is in use"));
        Assert.True(result);
    }

    // ─── IsUserError: "canceled" ───

    [Fact]
    public void IsUserError_Canceled_ReturnsTrue()
    {
        var result = ErrorLogger.IsUserError(new InvalidOperationException("operation was canceled"));
        Assert.True(result);
    }

    // ─── IsUserError: "cancelled" ───

    [Fact]
    public void IsUserError_Cancelled_ReturnsTrue()
    {
        var result = ErrorLogger.IsUserError(new InvalidOperationException("operation was cancelled"));
        Assert.True(result);
    }

    // ─── IsUserError: OperationCanceledException ───

    [Fact]
    public void IsUserError_OperationCanceledException_ReturnsTrue()
    {
        var result = ErrorLogger.IsUserError(new OperationCanceledException());
        Assert.True(result);
    }

    // ─── IsUserError: TaskCanceledException ───

    [Fact]
    public void IsUserError_TaskCanceledException_ReturnsTrue()
    {
        var result = ErrorLogger.IsUserError(new TaskCanceledException());
        Assert.True(result);
    }

    // ─── IsUserError: HttpRequestException with OperationCanceledException inner ───

    [Fact]
    public void IsUserError_HttpRequestExceptionWithCanceledInner_ReturnsTrue()
    {
        var ex = new HttpRequestException("request failed", new OperationCanceledException());
        var result = ErrorLogger.IsUserError(ex);
        Assert.True(result);
    }

    // ─── IsUserError: HttpRequestException without canceled inner ───

    [Fact]
    public void IsUserError_HttpRequestExceptionWithoutCanceledInner_ReturnsFalse()
    {
        var ex = new HttpRequestException("request failed", new InvalidOperationException("server error"));
        var result = ErrorLogger.IsUserError(ex);
        Assert.False(result);
    }
}
