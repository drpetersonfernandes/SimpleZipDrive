using System.Runtime.CompilerServices;

namespace SimpleZipDrive.Tests.Fakes;

internal static class MockBugReport
{
    [ModuleInitializer]
    internal static void Initialize()
    {
        ErrorLogger.SuppressApiCalls = true;
        SimpleZipDrive_WinFsp.ErrorLogger.SuppressApiCalls = true;
    }
}
