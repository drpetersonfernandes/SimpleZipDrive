#nullable disable
using System.Security.Principal;
using DokanNet;

namespace SimpleZipDrive.Tests.Fakes;

/// <summary>
/// A fake implementation of <see cref="IDokanFileInfo"/> for testing purposes.
/// </summary>
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

    /// <summary>
    /// Gets or sets the WindowsIdentity to return from <see cref="GetRequestor"/>.
    /// If null, GetRequestor will return null.
    /// </summary>
    public WindowsIdentity MockIdentity { get; set; }

    public bool TryResetTimeout(int milliseconds)
    {
        return true;
    }

    /// <summary>
    /// Gets the Windows identity of the caller.
    /// </summary>
    /// <returns>
    /// The <see cref="MockIdentity"/> if set; otherwise, null.
    /// Null is acceptable in test scenarios where identity verification is not required.
    /// </returns>
    public WindowsIdentity GetRequestor()
    {
        return MockIdentity;
    }
}
