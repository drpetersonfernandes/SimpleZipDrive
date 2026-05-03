#nullable disable
using DokanNet;

namespace SimpleZipDrive.Tests.Fakes;

public class FakeDokanFileInfo : IDokanFileInfo
{
    public bool IsDirectory { get; set; }
    public bool DeleteOnClose { get; set; }
    public bool PagingIo { get; set; }
    public bool SynchronousIo { get; set; }
    public bool NoCache { get; set; }
    public bool DeletePending { get; set; }
    public bool WriteToEndOfFile { get; set; }
    public int ProcessId { get; set; }
    public object Context { get; set; }

    public bool TryResetTimeout(int milliseconds)
    {
        return true;
    }

    public System.Security.Principal.WindowsIdentity GetRequestor()
    {
        // ReSharper disable once NullableWarningSuppressionIsUsed
        return null!;
    }
}
